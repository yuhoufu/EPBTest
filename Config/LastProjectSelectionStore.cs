using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Config
{
    [DataContract]
    public sealed class LastProjectSelection
    {
        [DataMember(Name = "schemaVersion", Order = 1)]
        public int SchemaVersion { get; set; } = 1;

        [DataMember(Name = "storeDir", Order = 2)]
        public string StoreDir { get; set; } = string.Empty;

        [DataMember(Name = "testName", Order = 3)]
        public string TestName { get; set; } = string.Empty;

        [DataMember(Name = "savedUtc", Order = 4)]
        public DateTime SavedUtc { get; set; }
    }

    public sealed class LastProjectRestoreResult
    {
        public bool SelectionFound { get; internal set; }
        public bool Restored { get; internal set; }
        public bool UsedBootstrap { get; internal set; }
        public string ProjectRoot { get; internal set; } = string.Empty;
        public string Message { get; internal set; } = string.Empty;
    }

    /// <summary>
    /// Stores only the last selected project identity outside the application directory.
    /// Program-level settings remain owned by MTTFTest.exe.config.
    /// </summary>
    public static class LastProjectSelectionStore
    {
        private static readonly object Gate = new object();

        public static string DefaultStatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Wanxiang",
            "EPBTest",
            "user-state.json");

        public static LastProjectRestoreResult Restore(
            GlobalConfig config,
            string bootstrapProjectRoot = null,
            IAppLogger logger = null,
            string statePath = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var path = string.IsNullOrWhiteSpace(statePath) ? DefaultStatePath : statePath;
            LastProjectSelection selection = null;
            var usedBootstrap = false;

            if (File.Exists(path))
            {
                try
                {
                    selection = Read(path);
                }
                catch (Exception ex)
                {
                    return Failure(true, false, string.Empty,
                        $"最后项目状态文件无法读取，已回退默认项目。State={path}; Error={ex.Message}", logger);
                }
            }
            else if (!string.IsNullOrWhiteSpace(bootstrapProjectRoot))
            {
                try
                {
                    var normalizedRoot = Path.GetFullPath(bootstrapProjectRoot.Trim())
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var parent = Directory.GetParent(normalizedRoot);
                    var name = Path.GetFileName(normalizedRoot);
                    if (parent == null || string.IsNullOrWhiteSpace(name))
                        throw new InvalidDataException("初始项目路径无法拆分为存储目录和项目名称。");
                    selection = new LastProjectSelection
                    {
                        StoreDir = parent.FullName,
                        TestName = name,
                        SavedUtc = DateTime.UtcNow
                    };
                    usedBootstrap = true;
                }
                catch (Exception ex)
                {
                    return Failure(true, true, bootstrapProjectRoot,
                        $"初始项目路径无效，已回退默认项目。Path={bootstrapProjectRoot}; Error={ex.Message}", logger);
                }
            }
            else
            {
                return new LastProjectRestoreResult
                {
                    SelectionFound = false,
                    Message = "未保存最后项目，继续使用默认项目。"
                };
            }

            var projectRoot = ConfigLoader.GetProjectRootDir(selection.StoreDir, selection.TestName);
            var projectConfig = ConfigLoader.GetProjectTestConfigPath(selection.StoreDir, selection.TestName);
            if (string.IsNullOrWhiteSpace(projectConfig) || !File.Exists(projectConfig))
                return Failure(true, usedBootstrap, projectRoot,
                    $"上次项目当前不可用，已回退默认项目且保留原选择。Project={projectRoot}; Expected={projectConfig}", logger);

            try
            {
                config.Test = ConfigLoader.LoadProjectTestConfig(
                    selection.StoreDir,
                    selection.TestName,
                    logger);
                if (usedBootstrap)
                {
                    if (!TrySave(selection.StoreDir, selection.TestName, out var saveError, path))
                        logger?.Warn($"初始项目已恢复，但保存独立项目选择失败：{saveError}", "配置");
                }
                var message = $"已自动恢复上次项目：{projectRoot}";
                logger?.Info(message, "配置");
                return new LastProjectRestoreResult
                {
                    SelectionFound = true,
                    Restored = true,
                    UsedBootstrap = usedBootstrap,
                    ProjectRoot = projectRoot,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                return Failure(true, usedBootstrap, projectRoot,
                    $"上次项目配置加载失败，已回退默认项目且保留原选择。Project={projectRoot}; Error={ex.Message}", logger);
            }
        }

        public static bool TrySave(
            string storeDir,
            string testName,
            out string error,
            string statePath = null)
        {
            error = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(storeDir))
                    throw new ArgumentException("项目存储目录不能为空。", nameof(storeDir));
                if (string.IsNullOrWhiteSpace(testName))
                    throw new ArgumentException("项目名称不能为空。", nameof(testName));
                var normalizedStore = Path.GetFullPath(storeDir.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var projectConfig = ConfigLoader.GetProjectTestConfigPath(normalizedStore, testName.Trim());
                if (string.IsNullOrWhiteSpace(projectConfig) || !File.Exists(projectConfig))
                    throw new FileNotFoundException("仅能记住已成功保存且可重新加载的项目。", projectConfig);

                var selection = new LastProjectSelection
                {
                    StoreDir = normalizedStore,
                    TestName = testName.Trim(),
                    SavedUtc = DateTime.UtcNow
                };
                WriteAtomic(
                    string.IsNullOrWhiteSpace(statePath) ? DefaultStatePath : statePath,
                    selection);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static LastProjectRestoreResult Failure(
            bool selectionFound,
            bool usedBootstrap,
            string projectRoot,
            string message,
            IAppLogger logger)
        {
            logger?.Warn(message, "配置");
            return new LastProjectRestoreResult
            {
                SelectionFound = selectionFound,
                Restored = false,
                UsedBootstrap = usedBootstrap,
                ProjectRoot = projectRoot ?? string.Empty,
                Message = message
            };
        }

        private static LastProjectSelection Read(string path)
        {
            lock (Gate)
            using (var stream = File.OpenRead(path))
            {
                var serializer = new DataContractJsonSerializer(typeof(LastProjectSelection));
                var value = serializer.ReadObject(stream) as LastProjectSelection;
                if (value == null || value.SchemaVersion != 1 ||
                    string.IsNullOrWhiteSpace(value.StoreDir) ||
                    string.IsNullOrWhiteSpace(value.TestName))
                    throw new InvalidDataException("最后项目状态内容不完整或版本不受支持。");
                return value;
            }
        }

        private static void WriteAtomic(string path, LastProjectSelection selection)
        {
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(directory))
                    throw new InvalidDataException("最后项目状态路径缺少目录。", new ArgumentException(nameof(path)));
                Directory.CreateDirectory(directory);
                var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(LastProjectSelection));
                        serializer.WriteObject(stream, selection);
                        stream.Flush(true);
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
        }
    }
}
