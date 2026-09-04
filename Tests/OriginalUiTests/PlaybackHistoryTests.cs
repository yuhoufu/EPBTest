using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using MTEmbTest;
using MTEmbTest.Playback;

internal static partial class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private static void RunPlaybackHistoryTests()
    {
        Run("Playback bounded feature envelope exports every source record", PlaybackBoundedFeatureExport);
        Run("Playback raw frozen layout supports reordered variable width matrices", PlaybackRawVariableLayout);
        Run("Playback rejects malformed records ambiguous layouts and unsafe XML", PlaybackRejectsMalformedInputs);
        Run("Playback cancellation and file races preserve external data", PlaybackExportRaceSafety);
        Run("Playback directory metadata is bounded and unambiguous", PlaybackDirectoryMetadata);
        Run("Playback original forms bind actual lists curves and channel layout", PlaybackOriginalForms);
    }

    private static void PlaybackBoundedFeatureExport()
    {
        var root = PlaybackRoot("feature");
        var source = Path.Combine(root, "Stat_large.bin");
        var export = Path.Combine(root, "complete.csv");
        try
        {
            const int frames = 100003;
            using (var writer = new BinaryWriter(new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
                for (var n = 0; n < frames; n++)
                {
                    var force = n == 55555 ? 9999.0 : n % 13;
                    var current = n == 77777 ? -8888.0 : n % 17;
                    WriteFeatureFrame(writer, n, PlaybackTime(n), force, n % 5, current, n % 7);
                }
            var before = PlaybackHash(source);
            var result = HistoryFileReader.Read(source, new HistoryReadOptions(), CancellationToken.None, 120);
            Assert(result.SourceFrames == frames && result.FilteredFrames == frames);
            Assert(result.Points.Length <= 120 && result.DisplayReduced);
            Assert(result.Points.Any(point => point.Force == 9999) && result.Points.Any(point => point.Current == -8888));
            Assert(result.Points.First().Index == 0 && result.Points.Last().Index == frames - 1);
            Assert(HistoryFileReader.FrameCount(77L * 30000000L, 77) == 30000000L);
            var detail = HistoryFileReader.ReadWindow(result, 55550, 55560, 100, CancellationToken.None);
            Assert(!detail.TooMany && detail.MatchingFrames == 11 && detail.Points.Any(point => point.Force == 9999));
            var broad = HistoryFileReader.ReadWindow(result, 0, frames, 100, CancellationToken.None);
            Assert(broad.TooMany && broad.MatchingFrames == frames && broad.Points.Length == 100);

            HistoryFileReader.Export(result, export, false, CancellationToken.None);
            long lines = 0; string header = null;
            using (var reader = new StreamReader(export, Encoding.UTF8))
                while (reader.ReadLine() is string line) { if (lines++ == 0) header = line; }
            Assert(lines == frames + 1L && header == "TimeStamp,RelTime,BrakeNo,CanForce,CanCurrent,DAQCurrent,DAQTorque");
            Assert(PlaybackHash(source).SequenceEqual(before));
        }
        finally { PlaybackDelete(root); }
    }

    private static void PlaybackRawVariableLayout()
    {
        var root = PlaybackRoot("raw-layout"); Directory.CreateDirectory(Path.Combine(root, "Config"));
        var config = Path.Combine(root, "Config", "AIConfig.xml");
        var source = Path.Combine(root, "DAQ_Dev1_Raw_1.bin");
        var export = Path.Combine(root, "raw.csv");
        try
        {
            WriteAiConfiguration(config, writer =>
            {
                WriteAiRow(writer, 30, "Dev1/ai2", "Pressure_1", 1, 1, 0, 0);
                WriteAiRow(writer, 10, "Dev1/ai0", "EPB12_current", 1, 2, 1, 0.5);
                WriteAiRow(writer, 20, "Dev1/ai1", "EPB2_current", 1, 1, 0, 0);
                WriteAiRow(writer, 40, "Dev2/ai0", "EPB9_current", 1, 1, 0, 0);
                WriteAiRow(writer, 50, "Dev1/ai3", "EPB3_current", 0, 1, 0, 0);
            });
            var selectedRaw = new[] { 1.0, 4.0, 2.0, 3.0, 10.0 };
            using (var writer = new BinaryWriter(File.Create(source)))
                for (var n = 0; n < selectedRaw.Length; n++)
                {
                    writer.Write(200 + n); writer.Write(PlaybackTime(n).ToFileTime());
                    writer.Write(selectedRaw[n]); writer.Write(100.0 + n); writer.Write(200.0 + n);
                }
            var layout = HistoryRawLayout.Load(root, 12, 4);
            Assert(layout.Device == "Dev1" && layout.Options.ChannelCount == 3 && layout.Options.ChannelIndex == 0);
            var result = HistoryFileReader.Read(source, layout.Options, CancellationToken.None, 20);
            Assert(result.SourceFrames == 5 && result.FilteredFrames == 2);
            Assert(result.RawPreview.Select(point => point.Current).SequenceEqual(new[] { 2.0, 8.0, 4.0, 6.0, 20.0 }));
            Assert(result.Points.Select(point => point.Current).SequenceEqual(new[] { 6.0, 20.0 }));
            Assert(result.Points.Select(point => point.BrakeNo).SequenceEqual(new[] { 200, 204 }));
            HistoryFileReader.Export(result, export, false, CancellationToken.None);
            Assert(File.ReadLines(export).Count() == 3);
        }
        finally { PlaybackDelete(root); }
    }

    private static void PlaybackRejectsMalformedInputs()
    {
        var root = PlaybackRoot("invalid");
        try
        {
            var partial = Path.Combine(root, "Stat_partial.bin"); File.WriteAllBytes(partial, new byte[76]);
            PlaybackExpect<InvalidDataException>(() => HistoryFileReader.Read(partial, new HistoryReadOptions(), CancellationToken.None));
            var time = Path.Combine(root, "Stat_time.bin");
            using (var writer = new BinaryWriter(File.Create(time)))
            { writer.Write(1); writer.Write(long.MaxValue); writer.Write(new byte[65]); }
            PlaybackExpect<InvalidDataException>(() => HistoryFileReader.Read(time, new HistoryReadOptions(), CancellationToken.None));

            WriteAiConfiguration(Path.Combine(root, "AIConfig.xml"), writer =>
            {
                WriteAiRow(writer, 1, "Dev1/ai0", "EPB1_current", 1, 1, 0, 0);
                WriteAiRow(writer, 1, "Dev1/ai1", "EPB2_current", 1, 1, 0, 0);
            });
            PlaybackExpect<InvalidDataException>(() => HistoryRawLayout.Load(root, 1, 1));
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            WriteAiConfiguration(Path.Combine(root, "Config", "AIConfig.xml"), writer =>
                WriteAiRow(writer, 2, "Dev1/ai0", "EPB1_current", 1, 1, 0, 0));
            PlaybackExpect<InvalidDataException>(() => HistoryRawLayout.Load(root, 1, 1));

            File.WriteAllText(Path.Combine(root, "AIConfig.xml"),
                "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><AIConfigDetail><Records><参数名>&e;</参数名></Records></AIConfigDetail>");
            File.Delete(Path.Combine(root, "Config", "AIConfig.xml"));
            var unsafeXml = PlaybackExpect<InvalidOperationException>(() => HistoryRawLayout.Load(root, 1, 1));
            Assert(unsafeXml.InnerException is XmlException);
        }
        finally { PlaybackDelete(root); }
    }

    private static void PlaybackExportRaceSafety()
    {
        var root = PlaybackRoot("races");
        var source = Path.Combine(root, "Stat.bin"); var target = Path.Combine(root, "target.csv");
        try
        {
            using (var writer = new BinaryWriter(File.Create(source)))
                for (var n = 0; n < 50; n++) WriteFeatureFrame(writer, n, PlaybackTime(n), n, n, n, n);
            var result = HistoryFileReader.Read(source, new HistoryReadOptions(), CancellationToken.None);
            File.WriteAllText(target, "previous"); var cancel = new CancellationTokenSource();
            PlaybackExpect<OperationCanceledException>(() => HistoryFileReader.Export(result, target, true, cancel.Token,
                written => { if (written == 3) cancel.Cancel(); }));
            Assert(File.ReadAllText(target) == "previous" && !Directory.EnumerateFiles(root, ".epb-export-*.tmp").Any());

            using (var stream = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.Read))
            using (var writer = new BinaryWriter(stream)) { stream.Position = 12; writer.Write(98765.0); }
            PlaybackExpect<IOException>(() => HistoryFileReader.Export(result, target, true, CancellationToken.None));
            Assert(File.ReadAllText(target) == "previous");

            result = HistoryFileReader.Read(source, new HistoryReadOptions(), CancellationToken.None);
            PlaybackExpect<IOException>(() => HistoryFileReader.Export(result, target, true, CancellationToken.None,
                written => { if (written == 1) File.WriteAllText(target, "external"); }));
            Assert(File.ReadAllText(target) == "external" && !Directory.EnumerateFiles(root, ".epb-export-*.tmp").Any());

            var competing = Path.Combine(root, "competing.csv");
            PlaybackExpect<IOException>(() => HistoryFileReader.Export(result, competing, false, CancellationToken.None,
                written => { if (written == 1) File.WriteAllText(competing, "competitor"); }));
            Assert(File.ReadAllText(competing) == "competitor");

            var alias = Path.Combine(root, "alias.csv");
            if (CreateHardLink(alias, source, IntPtr.Zero))
                PlaybackExpect<InvalidOperationException>(() => HistoryFileReader.Export(result, alias, true, CancellationToken.None));
            Assert(!Directory.EnumerateFiles(root, ".epb-export-*.tmp").Any());
        }
        finally { PlaybackDelete(root); }
    }

    private static void PlaybackDirectoryMetadata()
    {
        var root = PlaybackRoot("directory"); Directory.CreateDirectory(Path.Combine(root, "Config"));
        try
        {
            var configuration = "<TestConfig><TestName>10243-028</TestName><TestTarget>200000</TestTarget></TestConfig>";
            File.WriteAllText(Path.Combine(root, "TestConfig.xml"), configuration);
            File.WriteAllText(Path.Combine(root, "Config", "TestConfig.xml"), configuration);
            File.WriteAllBytes(Path.Combine(root, "Stat_B.bin"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(root, "other.bin"), new byte[] { 2 });
            var metadata = HistoryDirectoryReader.LoadTestConfiguration(root, CancellationToken.None);
            Assert(metadata.TestName == "10243-028" && metadata.TestTarget == "200000");
            Assert(HistoryDirectoryReader.List(root, "Stat", CancellationToken.None).SequenceEqual(new[] { "Stat_B.bin" }));

            File.WriteAllText(Path.Combine(root, "Config", "TestConfig.xml"), configuration.Replace("200000", "1"));
            PlaybackExpect<InvalidDataException>(() => HistoryDirectoryReader.LoadTestConfiguration(root, CancellationToken.None));
            File.Delete(Path.Combine(root, "Config", "TestConfig.xml"));
            File.WriteAllText(Path.Combine(root, "TestConfig.xml"),
                "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><TestConfig><TestName>&e;</TestName></TestConfig>");
            PlaybackExpect<XmlException>(() => HistoryDirectoryReader.LoadTestConfiguration(root, CancellationToken.None));
        }
        finally { PlaybackDelete(root); }
    }

    private static void PlaybackOriginalForms()
    {
        var featureRoot = PlaybackRoot("feature-form");
        var rawRoot = PlaybackRoot("raw-form"); Directory.CreateDirectory(Path.Combine(rawRoot, "Config"));
        try
        {
            File.WriteAllText(Path.Combine(featureRoot, "TestConfig.xml"),
                "<TestConfig><TestName>10243-028</TestName><TestTarget>200000</TestTarget></TestConfig>");
            using (var writer = new BinaryWriter(File.Create(Path.Combine(featureRoot, "Stat_1.bin"))))
                for (var n = 0; n < 3; n++) WriteFeatureFrame(writer, 21500 + n, PlaybackTime(n), n, n, n, n);
            using (var form = new FrmPlayBack())
            {
                PlaybackShowOffscreen(form);
                PlaybackAwait(form.LoadHistoryDirectoryAsync(featureRoot));
                var list = ReadField<object>(form, "LbFileList"); Assert(PlaybackItemCount(list) == 1);
                Assert(((Control)ReadField<object>(form, "RtbTestInfo")).Text.Contains("10243-028"));
                list.GetType().GetProperty("SelectedIndex").SetValue(list, 0, null);
                InvokePrivate(form, "LbFileList_DoubleClick", list, EventArgs.Empty);
                PlaybackAwait(form.HistoryLoadTask);
                Assert(form.LoadedHistory.SourceFrames == 3);
                Assert(((Control)ReadField<object>(form, "BtnFindFile")).Enabled &&
                    ((Control)ReadField<object>(form, "BtnExportFile")).Enabled);
                PlaybackCapture(form, "2026-09-05_R26_原特征值回放_隔离模拟_1440x900.png");
                var check = ReadField<object>(form, "ChkForce");
                check.GetType().GetProperty("Checked").SetValue(check, false, null);
                var curve = ReadField<object>(form, "curveForce");
                Assert(!(bool)curve.GetType().GetProperty("IsVisible").GetValue(curve, null));
            }

            WriteAiConfiguration(Path.Combine(rawRoot, "Config", "AIConfig.xml"), writer =>
            {
                WriteAiRow(writer, 2, "Dev1/ai1", "EPB2_current", 1, 1, 0, 0);
                WriteAiRow(writer, 1, "Dev1/ai0", "EPB12_current", 1, 2, 1, 0.5);
            });
            using (var writer = new BinaryWriter(File.Create(Path.Combine(rawRoot, "DAQ_Dev1_Raw_1.bin"))))
                for (var n = 0; n < 4; n++)
                { writer.Write(300 + n); writer.Write(PlaybackTime(n).ToFileTime()); writer.Write((double)n); writer.Write(20.0 + n); }
            using (var form = new FrmRawPlayBack())
            {
                var combo = (Control)ReadField<object>(form, "CmbEpbNo"); combo.Text = "EPB12";
                PlaybackShowOffscreen(form);
                PlaybackAwait(form.LoadHistoryDirectoryAsync(rawRoot));
                var list = ReadField<object>(form, "LbFileList"); Assert(PlaybackItemCount(list) == 1);
                list.GetType().GetProperty("SelectedIndex").SetValue(list, 0, null);
                InvokePrivate(form, "LbFileList_DoubleClick", list, EventArgs.Empty);
                PlaybackAwait(form.HistoryLoadTask);
                Assert(form.LoadedHistory.SourceFrames == 4);
                Assert(ReadField<double[]>(form, "filterCurrent").Length > 0);
                Assert(((Control)ReadField<object>(form, "BtnChoiseFolder")).Enabled &&
                    ((Control)ReadField<object>(form, "BtnExportFile")).Enabled && combo.Enabled);
                PlaybackCapture(form, "2026-09-05_R26_原始数据回放_隔离模拟_1440x900.png");
                form.Dispose();
                InvokePrivate(form, "bgwA_Completed", null, new RunWorkerCompletedEventArgs(null, null, false));
            }
        }
        finally { PlaybackDelete(featureRoot); PlaybackDelete(rawRoot); }
    }

    private static string PlaybackRoot(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "MTTFTest.Playback." + name + "." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }

    private static DateTime PlaybackTime(int index) => new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Local).AddMilliseconds(index);

    private static void WriteFeatureFrame(BinaryWriter writer, int brake, DateTime time,
        double force, double canCurrent, double current, double torque)
    {
        writer.Write(brake); writer.Write(time.ToFileTime()); writer.Write(force); writer.Write(canCurrent);
        writer.Write(torque); writer.Write(current); writer.Write(new byte[33]);
    }

    private static void WriteAiConfiguration(string path, Action<XmlWriter> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var writer = XmlWriter.Create(path, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            writer.WriteStartElement("AIConfigDetail"); rows(writer); writer.WriteEndElement();
        }
    }

    private static void WriteAiRow(XmlWriter writer, int order, string physical, string parameter,
        int enabled, double scale, double offset, double zero)
    {
        writer.WriteStartElement("Records");
        writer.WriteElementString("序号", order.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteElementString("物理通道", physical); writer.WriteElementString("参数名", parameter);
        writer.WriteElementString("单位", "A");
        writer.WriteElementString("变换斜率", scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteElementString("变换截距", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteElementString("参数类型", "电流");
        writer.WriteElementString("是否启用", enabled.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteElementString("零位漂移", zero.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }

    private static byte[] PlaybackHash(string path)
    {
        using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return hash.ComputeHash(stream);
    }

    private static T PlaybackExpect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void PlaybackAwait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        if (!task.IsCompleted) throw new TimeoutException("WinForms playback operation did not complete.");
        task.GetAwaiter().GetResult();
    }

    private static int PlaybackItemCount(object list)
    {
        var items = list.GetType().GetProperty("Items").GetValue(list, null);
        return (int)items.GetType().GetProperty("Count").GetValue(items, null);
    }

    private static void PlaybackCapture(Form form, string name)
    {
        var repository = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        var directory = Path.Combine(repository, "docs", "02_Issues", "截图"); Directory.CreateDirectory(directory);
        form.Size = new Size(1440, 900); form.PerformLayout();
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(directory, name));
        }
    }

    private static void PlaybackShowOffscreen(Form form)
    {
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
        form.WindowState = FormWindowState.Normal; form.Location = new Point(-20000, -20000);
        form.Size = new Size(1440, 900); form.Show(); form.Size = new Size(1440, 900); Application.DoEvents();
    }

    private static void PlaybackDelete(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
