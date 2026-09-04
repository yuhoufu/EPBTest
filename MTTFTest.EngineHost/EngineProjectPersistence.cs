using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Config;
using DataOperation;

namespace MTTFTest.EngineHost
{
    /// <summary>
    /// Owns the project's V3 index/ring pair. Never opens a legacy index against
    /// newly created rings, and never derives data paths from the UI working directory.
    /// </summary>
    internal sealed class EngineProjectPersistence : IDisposable
    {
        internal const string DirectoryName = "V3Persistence";
        private FileStream _ownership;
        internal EpbDiskWriter Writer { get; private set; }
        internal DiskWriterRecorderAdapter Recorder { get; private set; }
        internal string RootDirectory { get; private set; }

        public sealed class StorageBinding
        {
            public int Version { get; set; } = 1;
            public int RecordBytes { get; set; } = SampleRecord.Size;
            public int FileSizeMb { get; set; }
            public string ProjectName { get; set; }
            public int[] ImportedFormal { get; set; }
            public long[] ImportedMechanical { get; set; }
        }

        internal static EngineProjectPersistence Open(TestConfig test, IAppLogger log,
            int initialFileSizeMb = 100, Action<string> boundary = null)
        {
            if (test == null || string.IsNullOrWhiteSpace(test.StoreDir) ||
                string.IsNullOrWhiteSpace(test.TestName) || test.TestName != Path.GetFileName(test.TestName) ||
                test.TestName == "." || test.TestName == ".." || test.TestName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("PersistenceProjectPathInvalid");
            if (initialFileSizeMb < 1 || initialFileSizeMb > 2048)
                throw new ArgumentOutOfRangeException(nameof(initialFileSizeMb));
            var project = Path.GetFullPath(Path.Combine(test.StoreDir, test.TestName));
            Directory.CreateDirectory(project);
            var result = new EngineProjectPersistence { RootDirectory = Path.Combine(project, DirectoryName) };
            try
            {
                result._ownership = new FileStream(Path.Combine(project, "V3Persistence.owner.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (!Directory.Exists(result.RootDirectory))
                {
                    var binding = new StorageBinding
                    {
                        ProjectName = test.TestName, FileSizeMb = initialFileSizeMb,
                        ImportedFormal = Enumerable.Range(1, 12).Select(ch => test.GetEpbRecord(ch).RunCount).ToArray(),
                        ImportedMechanical = Enumerable.Range(1, 12).Select(ch => test.GetEpbRecord(ch).EffectiveMechanicalCycleCount).ToArray()
                    };
                    ValidateBinding(binding, test.TestName);
                    // A crash before rename leaves only an unreferenced preparation
                    // directory. It is retained for inspection, never adopted as live data.
                    var staging = Path.Combine(project, ".V3Persistence.preparing." + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(staging);
                    using (var writer = new EpbDiskWriter(CreatePolicy(staging, binding, test, log)))
                        writer.ImportProgressBaseline(binding.ImportedFormal, binding.ImportedMechanical);
                    var bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(binding));
                    using (var file = new FileStream(Path.Combine(staging, "binding.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        file.Write(bytes, 0, bytes.Length);
                        file.Flush(true);
                    }
                    ValidateFiles(staging, binding);
                    boundary?.Invoke("BeforePublish");
                    Directory.Move(staging, result.RootDirectory);
                    boundary?.Invoke("Published");
                    log?.Info("V3项目数据目录已发布；仅导入历史计数，旧索引和采样文件保持原位。" + result.RootDirectory, "落盘");
                }
                RejectReparse(result.RootDirectory);
                var bindingPath = Path.Combine(result.RootDirectory, "binding.json");
                RejectReparse(bindingPath);
                var info = new FileInfo(bindingPath);
                if (!info.Exists || info.Length <= 0 || info.Length > 16384)
                    throw new InvalidDataException("PersistenceBindingMissingOrInvalid");
                var existing = new JavaScriptSerializer { MaxJsonLength = 16384 }
                    .Deserialize<StorageBinding>(File.ReadAllText(bindingPath, Encoding.UTF8));
                ValidateBinding(existing, test.TestName);
                ValidateFiles(result.RootDirectory, existing);
                result.Writer = new EpbDiskWriter(CreatePolicy(result.RootDirectory, existing, test, log));
                result.Recorder = new DiskWriterRecorderAdapter(result.Writer);
                result.Writer.AdvanceRunTime(Enumerable.Range(1, 12).Select(ch => Math.Max(0, test.GetEpbRecord(ch).RunTimeSpan.Ticks)).ToArray());
                var times = result.Writer.ReadDurableProgress();
                var durableFormal = Enumerable.Range(1, 12).Select(result.Writer.GetCompletedFormalCycleCount).ToArray();
                var durableMechanical = Enumerable.Range(1, 12).Select(result.Writer.GetMechanicalCycleCompletedCount).ToArray();
                for (var index = 0; index < 12; index++)
                    if (durableFormal[index] < existing.ImportedFormal[index] ||
                        durableMechanical[index] < existing.ImportedMechanical[index] ||
                        result.Writer.GetMaxCycleNumber(index + 1) < durableFormal[index])
                        throw new InvalidDataException("PersistenceProgressRegressed:" + (index + 1));
                // Reconcile only completed facts; an interrupted cycle's allocated
                // number must never become RunCount. Never reset time or isolation.
                foreach (var ch in Enumerable.Range(1, 12))
                {
                    var record = test.GetEpbRecord(ch);
                    record.RunCount = Math.Max(record.RunCount, durableFormal[ch - 1]);
                    record.MechanicalCycleCount = Math.Max(record.EffectiveMechanicalCycleCount,
                        durableMechanical[ch - 1]);
                    record.RunTime = TimeSpan.FromTicks(times[ch - 1].RunTimeTicks).ToString("c", System.Globalization.CultureInfo.InvariantCulture);
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static void ValidateBinding(StorageBinding binding, string projectName)
        {
            if (binding == null || binding.Version != 1 || binding.RecordBytes != SampleRecord.Size ||
                binding.FileSizeMb < 1 || binding.FileSizeMb > 2048 || binding.ProjectName != projectName ||
                binding.ImportedFormal?.Length != 12 || binding.ImportedMechanical?.Length != 12 ||
                Enumerable.Range(0, 12).Any(i => binding.ImportedFormal[i] < 0 || binding.ImportedMechanical[i] < binding.ImportedFormal[i]))
                throw new InvalidDataException("PersistenceBindingInvalid");
        }

        private static void ValidateFiles(string root, StorageBinding binding)
        {
            RejectReparse(root);
            var index = Path.Combine(root, "index.db");
            RejectReparse(index);
            if (!File.Exists(index) || new FileInfo(index).Length == 0)
                throw new InvalidDataException("PersistenceIndexMissing");
            for (var ch = 1; ch <= 12; ch++)
            {
                var path = Path.Combine(root, "EPB" + ch + "_sliding.dat");
                RejectReparse(path);
                if (!File.Exists(path) || new FileInfo(path).Length != binding.FileSizeMb * 1024L * 1024L)
                    throw new InvalidDataException("PersistenceRingMissingOrResized:" + ch);
            }
        }

        private static void RejectReparse(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("PersistenceReparsePointRejected:" + path);
        }

        private static DataRetentionPolicy CreatePolicy(string root, StorageBinding binding, TestConfig test, IAppLogger log)
        {
            var program = ProgramStoragePolicy.Load(message => log?.Warn(message, "落盘"));
            var latest = test.DataStorageRetention?.Latest ?? new LatestSnapshotRetentionConfig();
            return new DataRetentionPolicy
            {
                DataStorePath = root, IndexAndExportPath = root, FileSizeMb = binding.FileSizeMb,
                RequireDurableCommits = true,
                RetainLatestCycles = 10, CleanupMode = "archive",
                RetainLatestStopPackagesPerChannel = latest.RetainStopPackagesPerChannel,
                RetainAllLatestStopPackages = latest.RetentionMode == StorageRetentionMode.Unlimited,
                LatestStorageLevel = program.Latest, AlarmStorageLevel = program.Alarm, LearningStorageLevel = program.Learning,
                HistoricalEnabled = program.HistoricalEnabled, HistoricalRetainCyclesPerChannel = program.HistoricalRetainCyclesPerChannel,
                RetentionWarningSink = message => log?.Warn(message, "落盘")
            };
        }

        public void Dispose()
        {
            try { Writer?.Dispose(); }
            finally { Writer = null; Recorder = null; _ownership?.Dispose(); _ownership = null; }
        }
    }
}
