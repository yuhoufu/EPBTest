using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTEmbTest.Playback;
using MtEmbTest;
using ZedGraph;

namespace MTEmbTest
{
    public partial class FrmRawPlayBack
    {
        private readonly CancellationTokenSource _historyStop = new CancellationTokenSource();
        private CancellationTokenSource _historyDetailStop;
        private int _historyDetailGeneration;
        private HistoryRawLayout _historyRawLayout;
        private HistoryReadResult _historyResult;
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
            FormClosing += (_, __) => { _historyClosed = true; _historyStop.Cancel(); CancelRawDetail(); _historyCompletion?.TrySetResult(false); };
            Disposed += (_, __) => { _historyClosed = true; _historyStop.Cancel(); CancelRawDetail(); _historyCompletion?.TrySetResult(false); };
        }
        private void UpdateHistoryAvailability()
        {
            if (HistoryClosed) return;
            BtnChoiseFolder.Enabled = CmbEpbNo.Enabled = LbFileList.Enabled = !HistoryBusy;
            BtnExportFile.Enabled = !HistoryBusy && _historyResult != null;
            ProgressShow.Visible = HistoryBusy;
        }
        private async Task ChooseHistoryDirectoryAsync()
        {
            if (HistoryBusy || HistoryClosed) return;
            using (var dialog = new FolderBrowserDialog { Description = "选择原始采集历史目录", ShowNewFolderButton = false,
                SelectedPath = Directory.Exists(selectedPath) ? selectedPath : string.Empty })
                if (dialog.ShowDialog(this) == DialogResult.OK) await LoadHistoryDirectoryAsync(dialog.SelectedPath);
        }
        internal async Task LoadHistoryDirectoryAsync(string path)
        {
            if (HistoryBusy || HistoryClosed) return;
            var channel = EpbNo; if (channel < 1 || channel > 12) channel = 1;
            var median = Math.Max(1, ClsGlobal.MedianLens);
            _historyDirectoryBusy = true; UpdateHistoryAvailability();
            try
            {
                var root = Path.GetFullPath(path);
                var result = await Task.Run(() =>
                {
                    _historyStop.Token.ThrowIfCancellationRequested(); var layout = HistoryRawLayout.Load(root, channel, median);
                    var names = HistoryDirectoryReader.List(root, "DAQ_" + layout.Device + "_Raw", _historyStop.Token);
                    return new { Layout = layout, Names = names };
                });
                if (HistoryClosed) return;
                ClearRawHistory(); selectedPath = root; _historyRawLayout = result.Layout;
                EpbNo = channel; EpbName = "EPB" + channel;
                LbFileList.Items.Clear(); LbFileList.Items.AddRange(result.Names);
                RtbTestInfo.Text = EpbName + "：" + _historyRawLayout.Device + "，历史矩阵 " + _historyRawLayout.Options.ChannelCount +
                    " 列，电流列 " + (_historyRawLayout.Options.ChannelIndex + 1) + "。\r\n配置快照：" + _historyRawLayout.ConfigurationPath;
                _historyBaseStatus = RtbTestInfo.Text;
            }
            catch (Exception ex)
            {
                if (!HistoryClosed) { ClearRawHistory(); _historyRawLayout = null; LbFileList.Items.Clear(); RtbTestInfo.Text = "历史目录读取失败：" + ex.GetBaseException().Message; }
            }
            finally { _historyDirectoryBusy = false; UpdateHistoryAvailability(); }
        }
        private async Task ChangeHistoryChannelAsync()
        {
            if (HistoryClosed || HistoryBusy) return;
            if (!int.TryParse(CmbEpbNo.Text.Replace("EPB", string.Empty), out var channel) || channel < 1 || channel > 12) return;
            EpbNo = channel; EpbName = "EPB" + channel;
            if (!string.IsNullOrWhiteSpace(selectedPath)) await LoadHistoryDirectoryAsync(selectedPath);
        }
        private void ClearRawHistory()
        {
            CancelRawDetail();
            _historyResult = null; _loadedSourcePath = null;
            DaqCurrent = filterCurrent = FilterDaqRelTime = null; DaqSourceTime = FilterDaqTime = null; DaqBrakeNo = FilterDaqBrakeNo = null;
            listForce?.Clear(); listCanCurrent?.Clear(); listDaqCurrent?.Clear();
        }
        internal void OpenHistoryFile(string path)
        {
            if (HistoryClosed || HistoryBusy || bgwA == null || bgwA.IsBusy || _historyRawLayout == null) return;
            ClearRawHistory(); ExportFile = Path.ChangeExtension(path, ".csv");
            _historyCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _historyReadBusy = true;
            try { bgwA.RunWorkerAsync(Path.GetFullPath(path)); }
            catch (Exception ex) { _historyReadBusy = false; _historyCompletion.TrySetResult(false); RtbTestInfo.Text = ex.GetBaseException().Message; }
            UpdateHistoryAvailability();
        }
        private void ReadRawHistory(string path)
        {
            var options = _historyRawLayout?.Options.Clone();
            if (options == null)
            {
                // Retained for the historical private read seam. Production file
                // selection always resolves the exact frozen AI configuration first.
                if (EpbNo < 1 || EpbNo > 12 || !ParaNameToScale.TryGetValue(EpbName, out var scale) ||
                    !ParaNameToOffset.TryGetValue(EpbName, out var offset) || !ParaNameToZeroValue.TryGetValue(EpbName, out var zero))
                    throw new InvalidDataException("历史 AI 配置缺少通道参数。");
                options = new HistoryReadOptions { Raw = true, ChannelCount = EpbNo <= 8 ? 8 : 7, ChannelIndex = (EpbNo <= 8 ? EpbNo : EpbNo - 8) - 1,
                    Scale = scale, Offset = offset, Zero = zero, MedianLength = Math.Max(1, ClsGlobal.MedianLens) };
            }
            var result = HistoryFileReader.Read(path, options, _historyStop.Token); _historyStop.Token.ThrowIfCancellationRequested();
            _historyResult = result; _loadedSourcePath = result.SourcePath;
            DaqCurrent = result.RawPreview.Select(p => p.Current).ToArray(); DaqSourceTime = result.RawPreview.Select(p => p.Time).ToArray();
            DaqBrakeNo = result.RawPreview.Select(p => p.BrakeNo).ToArray();
            filterCurrent = result.Points.Select(p => p.Current).ToArray(); FilterDaqTime = result.Points.Select(p => p.Time).ToArray();
            FilterDaqBrakeNo = result.Points.Select(p => p.BrakeNo).ToArray(); FilterDaqRelTime = result.Points.Select(p => (p.Time - result.FirstTime).TotalSeconds).ToArray();
        }
        private void CompleteRawHistory(RunWorkerCompletedEventArgs e)
        {
            _historyReadBusy = false;
            if (HistoryClosed) return;
            var success = false;
            try
            {
                if (e.Error != null || e.Cancelled || _historyResult == null)
                { ClearRawHistory(); RtbTestInfo.Text = "历史文件读取失败：" + (e.Error?.GetBaseException().Message ?? "已取消"); return; }
                ApplyRawPoints(_historyResult.Points, true);
                RtbTestInfo.Text = EpbName + " 历史文件：" + Path.GetFileName(_loadedSourcePath) + "\r\n原始帧：" + _historyResult.SourceFrames +
                    "；中值后：" + _historyResult.FilteredFrames + "；显示点：" + filterCurrent.Length +
                    (_historyResult.DisplayReduced ? "（峰谷降采样；CSV 导出全部中值结果）" : "（全部中值结果）");
                _historyBaseStatus = RtbTestInfo.Text;
                success = true;
            }
            catch (Exception ex) { ClearRawHistory(); RtbTestInfo.Text = "历史显示失败：" + ex.GetBaseException().Message; }
            finally { UpdateHistoryAvailability(); _historyCompletion?.TrySetResult(success); }
        }
        private void ApplyRawPoints(HistoryPoint[] points, bool resetAxis)
        {
            filterCurrent = points.Select(p => p.Current).ToArray(); FilterDaqTime = points.Select(p => p.Time).ToArray();
            FilterDaqBrakeNo = points.Select(p => p.BrakeNo).ToArray();
            FilterDaqRelTime = points.Select(p => (p.Time - _historyResult.FirstTime).TotalSeconds).ToArray();
            listDaqCurrent.Clear();
            for (var i = 0; i < points.Length; i++)
                listDaqCurrent.Add(FilterDaqRelTime[i], HistoryReadOptions.Finite(filterCurrent[i]) && Math.Abs(filterCurrent[i]) <= 1e12 ? filterCurrent[i] : PointPair.Missing);
            if (resetAxis && points.Length != 0)
            {
                XAxisMin = Math.Min(0, FilterDaqRelTime.Min()); XAxisMax = Math.Max(XAxisMin + 0.001, FilterDaqRelTime.Max());
                zedGraphControlHistory.GraphPane.XAxis.Scale.Min = XAxisMin; zedGraphControlHistory.GraphPane.XAxis.Scale.Max = XAxisMax;
            }
            zedGraphControlHistory.AxisChange(); zedGraphControlHistory.Invalidate();
        }

        private void HistoryZoomed(ZedGraphControl sender, ZoomState oldState, ZoomState newState) => RequestRawDetail();

        private async void RequestRawDetail()
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
                ApplyRawPoints(window.TooMany || window.Points.Length == 0 ? source.Points : window.Points, false);
                RtbTestInfo.Text = _historyBaseStatus + (window.TooMany ? "\r\n当前缩放范围仍超过显示上限，保持峰谷总览。" :
                    "\r\n当前缩放范围已读取完整中值明细：" + window.MatchingFrames + " 点。");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!HistoryClosed && generation == Volatile.Read(ref _historyDetailGeneration)) RtbTestInfo.Text = _historyBaseStatus + "\r\n明细读取失败：" + ex.GetBaseException().Message; }
            finally
            {
                Interlocked.CompareExchange(ref _historyDetailStop, null, stop);
                stop.Dispose();
            }
        }

        private void CancelRawDetail()
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
                AddExtension = true, DefaultExt = "csv", OverwritePrompt = true, Title = "导出完整中值采集记录" })
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
