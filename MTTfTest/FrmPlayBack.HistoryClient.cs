using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTEmbTest.Playback;
using ZedGraph;

namespace MTEmbTest
{
    public partial class FrmPlayBack
    {
        private readonly CancellationTokenSource _historyStop = new CancellationTokenSource();
        private CancellationTokenSource _historyDetailStop;
        private int _historyDetailGeneration;
        private HistoryReadResult _historyResult;
        private HistoryProjectMetadata _historyProject;
        private string _historyBaseStatus;
        private bool _historyClosed, _historyDirectoryBusy, _historyReadBusy, _historyExportBusy;
        private TaskCompletionSource<bool> _historyCompletion;
        internal Task HistoryLoadTask => _historyCompletion?.Task ?? Task.CompletedTask;
        internal HistoryReadResult LoadedHistory => _historyResult;
        private bool HistoryClosed => _historyClosed || IsDisposed || Disposing;
        private bool HistoryBusy => _historyDirectoryBusy || _historyReadBusy || _historyExportBusy;

        private void InitializeHistoryClient()
        {
            zedGraphControlHistory.ZoomEvent += HistoryZoomed;
            FormClosing += (_, __) => { _historyClosed = true; _historyStop.Cancel(); CancelFeatureDetail(); _historyCompletion?.TrySetResult(false); };
            Disposed += (_, __) => { _historyClosed = true; _historyStop.Cancel(); CancelFeatureDetail(); _historyCompletion?.TrySetResult(false); };
        }
        private void UpdateHistoryAvailability()
        {
            if (HistoryClosed) return;
            BtnFindFile.Enabled = LbFileList.Enabled = !HistoryBusy;
            BtnExportFile.Enabled = !HistoryBusy && _historyResult != null;
            ProgressShow.Visible = HistoryBusy;
        }
        private async Task ChooseHistoryDirectoryAsync()
        {
            if (HistoryBusy || HistoryClosed) return;
            using (var dialog = new FolderBrowserDialog { Description = "选择特征值历史目录", ShowNewFolderButton = false,
                SelectedPath = Directory.Exists(selectedPath) ? selectedPath : string.Empty })
                if (dialog.ShowDialog(this) == DialogResult.OK) await LoadHistoryDirectoryAsync(dialog.SelectedPath);
        }
        internal async Task LoadHistoryDirectoryAsync(string path)
        {
            if (HistoryBusy || HistoryClosed) return;
            _historyDirectoryBusy = true; UpdateHistoryAvailability();
            try
            {
                var root = Path.GetFullPath(path);
                var content = await Task.Run(() => new
                {
                    Names = HistoryDirectoryReader.List(root, "Stat", _historyStop.Token),
                    Configuration = HistoryDirectoryReader.LoadTestConfiguration(root, _historyStop.Token)
                });
                if (HistoryClosed) return;
                selectedPath = root; ClearFeatureHistory();
                LbFileList.Items.Clear(); LbFileList.Items.AddRange(content.Names);
                testConfig = null;
                _historyProject = content.Configuration;
                RtbTestInfo.Text = "试验名称：" + (string.IsNullOrWhiteSpace(_historyProject?.TestName) ? "未提供项目配置" : _historyProject.TestName) +
                    "\r\n试验次数：" + (string.IsNullOrWhiteSpace(_historyProject?.TestTarget) ? "—" : _historyProject.TestTarget) +
                    "\r\n历史目录：" + root + "\r\n双击读取；大文件采用有界峰谷显示，导出仍包含全部记录。";
                _historyBaseStatus = RtbTestInfo.Text;
            }
            catch (Exception ex) { if (!HistoryClosed) RtbTestInfo.Text = "历史目录读取失败：" + ex.GetBaseException().Message; }
            finally { _historyDirectoryBusy = false; UpdateHistoryAvailability(); }
        }
        private void ClearFeatureHistory()
        {
            CancelFeatureDetail();
            _historyResult = null; _loadedSourcePath = null;
            CanForce = CanCurrent = DaqTorque = DaqCurrent = RelTime = null; SourceTime = null; BrakeNo = null;
            listForce?.Clear(); listCanCurrent?.Clear(); listDaqTorque?.Clear(); listDaqCurrent?.Clear();
        }
        internal void OpenHistoryFile(string path)
        {
            if (HistoryClosed || HistoryBusy || bgwA == null || bgwA.IsBusy) return;
            ClearFeatureHistory(); ExportFile = Path.ChangeExtension(path, ".csv");
            _historyCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _historyReadBusy = true;
            try { bgwA.RunWorkerAsync(Path.GetFullPath(path)); }
            catch (Exception ex) { _historyReadBusy = false; _historyCompletion.TrySetResult(false); RtbTestInfo.Text = ex.GetBaseException().Message; }
            UpdateHistoryAvailability();
        }
        private void ReadFeatureHistory(string path)
        {
            var result = HistoryFileReader.Read(path, new HistoryReadOptions(), _historyStop.Token);
            _historyStop.Token.ThrowIfCancellationRequested();
            _historyResult = result; _loadedSourcePath = result.SourcePath;
            CanForce = result.Points.Select(p => p.Force).ToArray(); CanCurrent = result.Points.Select(p => p.CanCurrent).ToArray();
            DaqCurrent = result.Points.Select(p => p.Current).ToArray(); DaqTorque = result.Points.Select(p => p.Torque).ToArray();
            BrakeNo = result.Points.Select(p => p.BrakeNo).ToArray(); SourceTime = result.Points.Select(p => p.Time).ToArray();
            RelTime = result.Points.Select(p => (p.Time - result.FirstTime).TotalSeconds).ToArray();
        }
        private void CompleteFeatureHistory(RunWorkerCompletedEventArgs e)
        {
            _historyReadBusy = false;
            if (HistoryClosed) return;
            var success = false;
            try
            {
                if (e.Error != null || e.Cancelled || _historyResult == null)
                { ClearFeatureHistory(); RtbTestInfo.Text = "历史文件读取失败：" + (e.Error?.GetBaseException().Message ?? "已取消"); return; }
                ApplyFeaturePoints(_historyResult.Points);
                RtbTestInfo.Text = "试验名称：" + (string.IsNullOrWhiteSpace(_historyProject?.TestName) ? "未提供项目配置" : _historyProject.TestName) +
                    "\r\n试验次数：" + (string.IsNullOrWhiteSpace(_historyProject?.TestTarget) ? "—" : _historyProject.TestTarget) +
                    "\r\n历史文件：" + Path.GetFileName(_loadedSourcePath) + "\r\n记录数：" + _historyResult.SourceFrames +
                    "；显示点：" + _historyResult.Points.Length + (_historyResult.DisplayReduced ? "（峰谷降采样；CSV 导出全部记录）" : "（全部记录）") +
                    "\r\n次数范围：<" + BrakeNo[0] + "," + BrakeNo[BrakeNo.Length - 1] + ">";
                _historyBaseStatus = RtbTestInfo.Text;
                success = true;
            }
            catch (Exception ex) { ClearFeatureHistory(); RtbTestInfo.Text = "历史显示失败：" + ex.GetBaseException().Message; }
            finally { UpdateHistoryAvailability(); _historyCompletion?.TrySetResult(success); }
        }
        private static double HistoryGraphValue(double value) => HistoryReadOptions.Finite(value) && Math.Abs(value) <= 1e12 ? value : PointPair.Missing;
        private void ApplyFeaturePoints(HistoryPoint[] points)
        {
            CanForce = points.Select(p => p.Force).ToArray(); CanCurrent = points.Select(p => p.CanCurrent).ToArray();
            DaqCurrent = points.Select(p => p.Current).ToArray(); DaqTorque = points.Select(p => p.Torque).ToArray();
            BrakeNo = points.Select(p => p.BrakeNo).ToArray(); SourceTime = points.Select(p => p.Time).ToArray();
            RelTime = points.Select(p => (p.Time - _historyResult.FirstTime).TotalSeconds).ToArray();
            listForce.Clear(); listCanCurrent.Clear(); listDaqTorque.Clear(); listDaqCurrent.Clear();
            foreach (var point in points)
            {
                listForce.Add(point.BrakeNo, HistoryGraphValue(point.Force)); listCanCurrent.Add(point.BrakeNo, HistoryGraphValue(point.CanCurrent));
                listDaqTorque.Add(point.BrakeNo, HistoryGraphValue(point.Torque)); listDaqCurrent.Add(point.BrakeNo, HistoryGraphValue(point.Current));
            }
            zedGraphControlHistory.AxisChange(); zedGraphControlHistory.Invalidate();
        }

        private void HistoryZoomed(ZedGraphControl sender, ZoomState oldState, ZoomState newState) => RequestFeatureDetail();

        private async void RequestFeatureDetail()
        {
            var source = _historyResult;
            if (source == null || !source.DisplayReduced || HistoryClosed) return;
            var minimum = zedGraphControlHistory.GraphPane.XAxis.Scale.Min;
            var maximum = zedGraphControlHistory.GraphPane.XAxis.Scale.Max;
            var generation = Interlocked.Increment(ref _historyDetailGeneration);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(_historyStop.Token);
            var prior = Interlocked.Exchange(ref _historyDetailStop, stop); CancelWithoutThrow(prior);
            try
            {
                await Task.Delay(120, stop.Token);
                var window = await Task.Run(() => HistoryFileReader.ReadWindow(source, minimum, maximum,
                    HistoryFileReader.MaximumDisplayPoints, stop.Token));
                if (HistoryClosed || generation != Volatile.Read(ref _historyDetailGeneration) || !ReferenceEquals(source, _historyResult)) return;
                ApplyFeaturePoints(window.TooMany || window.Points.Length == 0 ? source.Points : window.Points);
                RtbTestInfo.Text = _historyBaseStatus + (window.TooMany ? "\r\n当前缩放范围仍超过显示上限，保持峰谷总览。" :
                    "\r\n当前缩放范围已读取完整明细：" + window.MatchingFrames + " 点。");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!HistoryClosed && generation == Volatile.Read(ref _historyDetailGeneration)) RtbTestInfo.Text = _historyBaseStatus + "\r\n明细读取失败：" + ex.GetBaseException().Message; }
            finally
            {
                Interlocked.CompareExchange(ref _historyDetailStop, null, stop);
                stop.Dispose();
            }
        }

        private void CancelFeatureDetail()
        {
            Interlocked.Increment(ref _historyDetailGeneration);
            CancelWithoutThrow(Interlocked.Exchange(ref _historyDetailStop, null));
        }

        private static void CancelWithoutThrow(CancellationTokenSource source)
        {
            if (source == null) return;
            try { source.Cancel(); } catch (ObjectDisposedException) { }
        }
        private async Task ChooseHistoryExportAsync()
        {
            if (HistoryBusy || HistoryClosed || _historyResult == null) return;
            using (var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = ExportFile,
                AddExtension = true, DefaultExt = "csv", OverwritePrompt = true, Title = "导出完整特征记录" })
                if (dialog.ShowDialog(this) == DialogResult.OK) await ExportHistoryToAsync(dialog.FileName, true);
        }
        internal async Task<bool> ExportHistoryToAsync(string path, bool overwrite)
        {
            if (HistoryBusy || HistoryClosed || _historyResult == null) return false;
            var source = _historyResult; _historyExportBusy = true; UpdateHistoryAvailability();
            try
            {
                await Task.Run(() => HistoryFileReader.Export(source, path, overwrite, _historyStop.Token));
                if (!HistoryClosed) RtbTestInfo.Text += "\r\n完整导出成功：" + path;
                return true;
            }
            catch (Exception ex) { if (!HistoryClosed) RtbTestInfo.Text += "\r\n导出失败：" + ex.GetBaseException().Message; return false; }
            finally { _historyExportBusy = false; UpdateHistoryAvailability(); }
        }
    }
}
