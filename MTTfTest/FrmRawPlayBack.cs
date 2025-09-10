using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;
using System.Xml.Serialization;
using DataOperation;
using MtEmbTest;
using ZedGraph;

namespace MTEmbTest
{
    public partial class FrmRawPlayBack : Form
    {
        private const int daqRawLogRecordLens = 76; // 固定字节12+8通道 （8*8） 
        private const int canRawLogRecordLens = 76;

        private static readonly ConcurrentDictionary<int, double> EMBHandlerToRecvCanForceScale = new();
        private static readonly ConcurrentDictionary<int, double> EMBHandlerToRecvCanForceOffset = new();

        private static readonly ConcurrentDictionary<int, double> EMBHandlerToRecvCanCurrentScale = new();
        private static readonly ConcurrentDictionary<int, double> EMBHandlerToRecvCanCurrentOffset = new();

        private static readonly ConcurrentDictionary<int, double> EMBHandlerToRecvCanTorqueScale = new();
        private static readonly ConcurrentDictionary<int, double> EMBHandlerToRecvCanTorqueOffset = new();


        private static readonly ConcurrentDictionary<string, double> EMBNameToRecvCanForceScale = new();
        private static readonly ConcurrentDictionary<string, double> EMBNameToRecvCanForceOffset = new();

        private static readonly ConcurrentDictionary<string, double> EMBNameToRecvCanCurrentScale = new();
        private static readonly ConcurrentDictionary<string, double> EMBNameToRecvCanCurrentOffset = new();

        private static readonly ConcurrentDictionary<string, double> EMBNameToRecvCanTorqueScale = new();
        private static readonly ConcurrentDictionary<string, double> EMBNameToRecvCanTorqueOffset = new();

        private static readonly ConcurrentDictionary<string, uint> DirectionToRecvFrame = new();
        private static readonly ConcurrentDictionary<string, string> EMBToDirection = new();
        private static readonly ConcurrentDictionary<int, uint> EMBHandlerToRecvFrame = new();
        private FileInfo[] allFiles;
        private BackgroundWorker bgwA;

        private int[] CanBrakeNo;

        // private double[] CanTorque;
        private double[] CanCurrent;

        // private double[] DaqTorque;
        private double[] CanForce;

        private double[] CanRelTime;
        private DateTime[] CanSourceTime;


        private LineItem curveCanCurrent;

        private LineItem curveDaqCurrent;

        private LineItem curveForce;
        private int[] DaqBrakeNo;
        private double[] DaqCurrent;
        private DateTime[] DaqSourceTime;

        private string EpbName = "";
        private int EpbNo;

        private string ExportFile = "";

        private double[] filterCurrent;
        private int[] FilterDaqBrakeNo;
        private double[] FilterDaqRelTime;
        private DateTime[] FilterDaqTime;
        private PointPairList listCanCurrent;
        private PointPairList listDaqCurrent;
        private PointPairList listForce;

        private ConcurrentDictionary<string, double> ParaNameToOffset = new();
        private ConcurrentDictionary<string, double> ParaNameToScale = new();
        private ConcurrentDictionary<string, double> ParaNameToZeroValue = new();

        private string SafeFile = "";
        private string selectedPath = "";
        private TestConfig testConfig;
        private double XAxisMax;

        private double XAxisMin;


        public FrmRawPlayBack()
        {
            InitializeComponent();
            // 创建自定义标题栏
            var titleBar = new Panel
            {
                Height = 30,
                Dock = DockStyle.Top,
                BackColor = Color.AliceBlue
            };

            // 添加自定义按钮
            var btnClose = new Button
            {
                Text = "X",
                Size = new Size(30, 30),
                Dock = DockStyle.Right
            };
            btnClose.Click += (s, e) => Close();

            titleBar.Controls.Add(btnClose);
            Controls.Add(titleBar);

            // 添加拖拽功能
            titleBar.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };
        }

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        private void InitializeCurve()
        {
            try
            {
                var fontSize = 8;

                // 保留原有初始化代码
                var pane = zedGraphControlHistory.GraphPane;
                // 设置 X 轴和 Y 轴以及刻度线为灰色


                pane.XAxis.Color = Color.Gray;
                pane.XAxis.MajorTic.Color = Color.Gray;
                pane.XAxis.MinorTic.Size = 0.0f;

                pane.YAxis.Color = Color.Gray;
                pane.YAxis.MajorTic.Color = Color.Gray;
                pane.YAxis.MinorTic.Size = 0.0f;


                pane.Title.IsVisible = false;
                pane.XAxis.Title.Text = "RelativeTime(S)";
                pane.YAxis.Title.IsVisible = false;
                pane.XAxis.Title.IsVisible = true;


                pane.Fill = new Fill(Color.FromArgb(255, 255, 255));
                pane.Chart.Fill = new Fill(Color.FromArgb(248, 248, 248));


                pane.Chart.Border.IsVisible = false;
                //边框不可见，若可见不显示坐标轴颜色

                // 设置图例背景色和曲线区域一致
                pane.Legend.Fill = new Fill(Color.FromArgb(255, 255, 255));


                // 设置图例字体为白色，不显示边框
                pane.Legend.FontSpec.FontColor = Color.FromArgb(80, 160, 255);
                pane.Legend.FontSpec.Size = fontSize;
                pane.Legend.Border.IsVisible = false;


                //   pane.XAxis.Type = AxisType.Date;
                //   pane.XAxis.Scale.Format = "HH:mm:ss";

                pane.XAxis.Type = AxisType.Linear;
                // pane.XAxis.Scale.Format = "HH:mm:ss";


                pane.XAxis.Title.FontSpec.FontColor = Color.FromArgb(80, 160, 255);
                pane.XAxis.Scale.FontSpec.FontColor = Color.FromArgb(80, 160, 255);

                // 设置 X 轴和 Y 轴的网格线为实线且可见
                pane.XAxis.MajorGrid.IsVisible = true;
                pane.XAxis.MajorGrid.Color = Color.Gray;
                pane.XAxis.MajorGrid.DashOn = float.MaxValue; // 设置为实线
                pane.XAxis.MajorGrid.DashOff = 0;

                // 调小坐标轴文字字体大小
                pane.XAxis.Title.FontSpec.Size = fontSize;
                pane.XAxis.Scale.FontSpec.Size = fontSize;


                pane.YAxis.Title.FontSpec.FontColor = Color.FromArgb(80, 160, 255);
                pane.YAxis.Scale.FontSpec.FontColor = Color.FromArgb(80, 160, 255);
                pane.YAxis.Title.FontSpec.Size = fontSize;
                pane.YAxis.Scale.FontSpec.Size = fontSize;
                pane.YAxis.MajorGrid.IsVisible = true;
                pane.YAxis.MajorGrid.Color = Color.Gray;
                pane.YAxis.MajorGrid.DashOn = float.MaxValue;
                pane.YAxis.MajorGrid.DashOff = 0;

                pane.YAxis.MajorTic.Size = 0.0f;
                pane.YAxis.MinorTic.Size = 0.0f;
                pane.YAxis.MajorTic.IsOpposite = false;


                pane.Y2Axis.IsVisible = true;
                pane.Y2Axis.Title.FontSpec.FontColor = Color.Lime;
                pane.Y2Axis.Color = Color.Lime;
                pane.Y2Axis.Scale.FontSpec.FontColor = Color.Lime;
                pane.Y2Axis.Title.FontSpec.Size = fontSize;
                pane.Y2Axis.Scale.FontSpec.Size = fontSize;
                pane.Y2Axis.MajorGrid.IsVisible = false;
                pane.Y2Axis.MajorGrid.IsZeroLine = false;

                pane.Y2Axis.MajorTic.Size = 0.0f;
                pane.Y2Axis.MinorTic.Size = 0.0f;


                var CanCurrentYAxis = new Y2Axis("");
                pane.Y2AxisList.Add(CanCurrentYAxis);
                CanCurrentYAxis.IsVisible = true;
                CanCurrentYAxis.Title.FontSpec.FontColor = Color.Purple;
                CanCurrentYAxis.Color = Color.Purple;
                CanCurrentYAxis.Scale.FontSpec.FontColor = Color.Purple;
                CanCurrentYAxis.Title.FontSpec.Size = fontSize;
                CanCurrentYAxis.Scale.FontSpec.Size = fontSize;
                CanCurrentYAxis.MajorGrid.IsVisible = false;
                CanCurrentYAxis.MajorGrid.IsZeroLine = false;


                listForce = new PointPairList();
                curveForce = pane.AddCurve("Act_Force(N)", listForce, Color.FromArgb(80, 160, 255), SymbolType.None);
                curveForce.Line.Width = 2;
                curveForce.YAxisIndex = 0;
                curveForce.IsY2Axis = false;


                listDaqCurrent = new PointPairList();
                curveDaqCurrent = pane.AddCurve("DAQ_Current(A)", listDaqCurrent, Color.Lime, SymbolType.None);
                curveDaqCurrent.Line.Width = 2;
                curveDaqCurrent.YAxisIndex = 0;
                curveDaqCurrent.IsY2Axis = true;


                listCanCurrent = new PointPairList();
                curveCanCurrent = pane.AddCurve("Act_Current(A)", listCanCurrent, Color.Purple, SymbolType.None);
                curveCanCurrent.Line.Width = 2;
                curveCanCurrent.YAxisIndex = pane.Y2AxisList.Count - 1;
                curveCanCurrent.IsY2Axis = true; // 


                //  zedGraphControlHistory.GraphPane.XAxis.Scale.Max = ClsGlobal.XDuration;
                //  zedGraphControlHistory.GraphPane.XAxis.Scale.Min = 0.0;


                zedGraphControlHistory.GraphPane.XAxis.Scale.MagAuto = false;
                zedGraphControlHistory.GraphPane.XAxis.Scale.FormatAuto = false;
                zedGraphControlHistory.GraphPane.YAxis.Scale.MagAuto = false;
                zedGraphControlHistory.GraphPane.YAxis.Scale.FormatAuto = false;

                zedGraphControlHistory.GraphPane.Y2Axis.Scale.MagAuto = false;
                zedGraphControlHistory.GraphPane.Y2Axis.Scale.FormatAuto = false;


                CanCurrentYAxis.Scale.MagAuto = false;
                CanCurrentYAxis.Scale.FormatAuto = false;

                zedGraphControlHistory.AxisChange();
                zedGraphControlHistory.Invalidate();
                zedGraphControlHistory.Refresh();
            }

            catch (Exception ex)
            {
                MessageBox.Show("初始化曲线显示失败！" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void FrmPlayBack_Load(object sender, EventArgs e)
        {
            InitializeCurve();

            // 方向和接收帧ID关系  如RL-1536
            MakeDirectionMapping();

            EpbName = CmbEpbNo.Text; // 默认EPB1


            bgwA = new BackgroundWorker();
            bgwA.WorkerReportsProgress = true;
            bgwA.DoWork += bgwA_DoWork;
            bgwA.RunWorkerCompleted += bgwA_Completed;
        }

        private void bgwA_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                var bgworker = sender as BackgroundWorker;
                var FileName = e.Argument.ToString();
                ReadData(FileName);
            }

            catch (Exception ex)
            {
            }
        }


        /// <summary>
        ///     数据处理完成--Old
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void bgwA_Completed_Old(object sender, RunWorkerCompletedEventArgs e)
        {
            if (CanForce != null)
            {
                for (var i = 0; i < CanForce.Length; i++)
                {
                    var x = CanSourceTime[i].Subtract(CanSourceTime[0]).TotalSeconds;

                    listForce.Add(x, CanForce[i]);
                    listCanCurrent.Add(x, CanCurrent[i]);
                }


                filterCurrent = ClsDataFilter.MakeMedianFilterReducePoint(ref DaqCurrent, ClsGlobal.MedianLens);

                var daqspan = 1.0 / ClsGlobal.DaqFrequency * ClsGlobal.MedianLens;

                var DaqDataLens = DaqCurrent.Length / ClsGlobal.MedianLens;
                FilterDaqRelTime = new double[DaqDataLens];
                FilterDaqBrakeNo = new int[DaqDataLens];
                FilterDaqTime = new DateTime[DaqDataLens];

                for (var i = 0; i < DaqDataLens; i++)
                {
                    FilterDaqTime[i] = DaqSourceTime[i * ClsGlobal.MedianLens];
                    FilterDaqRelTime[i] =
                        DaqSourceTime[i * ClsGlobal.MedianLens].Subtract(DaqSourceTime[0]).TotalSeconds;
                    FilterDaqBrakeNo[i] = DaqBrakeNo[i * ClsGlobal.MedianLens];
                }


                for (var i = 0; i < DaqDataLens; i++)
                    //DaqRelTime[i] = daqspan * (double)i;
                    listDaqCurrent.Add(FilterDaqRelTime[i], filterCurrent[i]);


                RtbTestInfo.Clear();
                RtbTestInfo.AppendText("试验名称: " + testConfig.TestName + "\n");
                RtbTestInfo.AppendText("试验环境: " + testConfig.TestEnvir + "\n");
                RtbTestInfo.AppendText("试验周期: " + testConfig.TestSpan.ToString("f2") + "S\n");
                RtbTestInfo.AppendText("试验次数: " + testConfig.TestTarget + "\n");
                RtbTestInfo.AppendText("当前范围: <" + CanBrakeNo[0] + "," + CanBrakeNo[CanBrakeNo.Length - 1] + ">\n");

                ProgressShow.Visible = false;
                Application.DoEvents();


                zedGraphControlHistory.GraphPane.XAxis.Scale.Max = FilterDaqRelTime[FilterDaqRelTime.Length - 1];
                zedGraphControlHistory.GraphPane.XAxis.Scale.Min = 0.0;


                XAxisMin = 0.0;
                XAxisMax = FilterDaqRelTime[FilterDaqRelTime.Length - 1];

                zedGraphControlHistory.AxisChange();
                zedGraphControlHistory.Invalidate();
            }

            else
            {
                MessageBox.Show("记录数据为空！");
            }
        }

        /// <summary>
        ///     数据处理完成
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void bgwA_Completed(object sender, RunWorkerCompletedEventArgs e)
        {
            // 检查DAQ数据是否有效
            if (DaqCurrent is { Length: > 0 })
            {
                // 对DAQ电流数据进行中值滤波
                filterCurrent = ClsDataFilter.MakeMedianFilterReducePoint(ref DaqCurrent, ClsGlobal.MedianLens);

                // 计算滤波后的数据长度和时间间隔
                var daqspan = 1.0 / ClsGlobal.DaqFrequency * ClsGlobal.MedianLens;
                var DaqDataLens = DaqCurrent.Length / ClsGlobal.MedianLens;

                // 初始化滤波后的时间相关数组
                FilterDaqRelTime = new double[DaqDataLens];
                FilterDaqBrakeNo = new int[DaqDataLens];
                FilterDaqTime = new DateTime[DaqDataLens];

                // 填充滤波后的时间数据
                for (var i = 0; i < DaqDataLens; i++)
                {
                    FilterDaqTime[i] = DaqSourceTime[i * ClsGlobal.MedianLens];
                    FilterDaqRelTime[i] =
                        DaqSourceTime[i * ClsGlobal.MedianLens].Subtract(DaqSourceTime[0]).TotalSeconds;
                    FilterDaqBrakeNo[i] = DaqBrakeNo[i * ClsGlobal.MedianLens];
                }

                // 将滤波后的数据添加到图表列表
                for (var i = 0; i < DaqDataLens; i++) listDaqCurrent.Add(FilterDaqRelTime[i], filterCurrent[i]);

                // 更新UI显示 -- 有bug，暂时注释
                /*RtbTestInfo.Clear();
                RtbTestInfo.AppendText("试验名称: " + testConfig.TestName + "\n");
                RtbTestInfo.AppendText("试验环境: " + testConfig.TestEnvir + "\n");
                RtbTestInfo.AppendText("试验周期: " + testConfig.TestSpan.ToString("f2") + "S\n");
                RtbTestInfo.AppendText("试验次数: " + testConfig.TestTarget + "\n");*/

                // 隐藏进度条并处理UI事件
                ProgressShow.Visible = false;
                Application.DoEvents();

                // 设置图表X轴范围
                zedGraphControlHistory.GraphPane.XAxis.Scale.Max = FilterDaqRelTime[FilterDaqRelTime.Length - 1];
                zedGraphControlHistory.GraphPane.XAxis.Scale.Min = 0.0;

                // 更新全局变量
                XAxisMin = 0.0;
                XAxisMax = FilterDaqRelTime[FilterDaqRelTime.Length - 1];

                // 刷新图表
                zedGraphControlHistory.AxisChange();
                zedGraphControlHistory.Invalidate();
            }
            else
            {
                MessageBox.Show("DAQ记录数据为空！");
            }
        }


        private void ReadData(string FileName)
        {
            try
            {
                // CAN数据的读取处理，旧代码，注释掉；
                /*
                using (var fs = new FileStream(FileName, FileMode.Open))
                {
                    var sr = new BinaryReader(fs);
                    var FileLens = (int)fs.Length;
                    var Frames = FileLens / canRawLogRecordLens;

                    CanBrakeNo = new int[Frames];
                    CanSourceTime = new DateTime[Frames];
                    CanForce = new double[Frames];
                    CanCurrent = new double[Frames];

                    CanRelTime = new double[Frames];


                    for (var i = 0; i < Frames; i++) //测试了一整，还是这个最快
                    {
                        CanBrakeNo[i] = sr.ReadInt32();


                        CanRelTime[i] = ClsGlobal.CanRecvTimeSpanMillSecs / 1000.0 * i;


                        CanSourceTime[i] = DateTime.FromFileTime(sr.ReadInt64());

                        var Data = sr.ReadBytes(64);


                        double forceValue = 0;
                        double currentValue = 0;
                        byte faultflg = 0;
                        double torque = 0;
                        ClsBitFieldParser.ParseClampData(Data,
                            EMBHandlerToRecvCanForceScale[EpbNo - 1],
                            EMBHandlerToRecvCanTorqueScale[EpbNo - 1],
                            EMBHandlerToRecvCanCurrentScale[EpbNo - 1],
                            out forceValue, out faultflg, out torque, out currentValue);

                        CanForce[i] = forceValue;
                        CanCurrent[i] = currentValue;
                    }

                    sr.Close();
                    fs.Close();
                }

                var daqFile = FileName.Replace("EMB_CAN" + EpbNo, "DAQ_Dev1");
                */

                var daqFile = FileName;

                using (var fs = new FileStream(daqFile, FileMode.Open))
                {
                    var sr = new BinaryReader(fs);
                    var FileLens = (int)fs.Length;
                    var Frames = FileLens / daqRawLogRecordLens;


                    DaqCurrent = new double[Frames];
                    DaqSourceTime = new DateTime[Frames];
                    DaqBrakeNo = new int[Frames];


                    for (var i = 0; i < Frames; i++) //测试了一整，还是这个最快
                    {
                        DaqBrakeNo[i] = sr.ReadInt32();
                        DaqSourceTime[i] = DateTime.FromFileTime(sr.ReadInt64());

                        var Data = sr.ReadBytes(daqRawLogRecordLens - 12);

                        var currentRaw = BitConverter.ToDouble(Data, (EpbNo - 1) * 8);

                        DaqCurrent[i] = (currentRaw - ParaNameToZeroValue[EpbName]) * ParaNameToScale[EpbName] +
                                        ParaNameToOffset[EpbName];
                    }

                    sr.Close();
                    fs.Close();
                }
            }
            catch (Exception ex)
            {
                // ex.Message;
            }
        }


        private void ShowOrHideCurve()
        {
            curveForce.IsVisible = ChkForce.Checked;
            zedGraphControlHistory.GraphPane.YAxis.IsVisible = ChkForce.Checked;

            curveDaqCurrent.IsVisible = ChkDaqCurrent.Checked;
            zedGraphControlHistory.GraphPane.Y2Axis.IsVisible = ChkDaqCurrent.Checked;

            //curveDaqTorque.IsVisible = ChkDaqTorque.Checked;
            //zedGraphControlHistory.GraphPane.YAxisList[1].IsVisible = ChkDaqTorque.Checked;


            curveCanCurrent.IsVisible = ChkPressure.Checked;
            zedGraphControlHistory.GraphPane.Y2AxisList[1].IsVisible = ChkPressure.Checked;

            zedGraphControlHistory.AxisChange();
            zedGraphControlHistory.Invalidate();
        }

        private void ChkForce_CheckedChanged(object sender, EventArgs e)
        {
            ShowOrHideCurve();
        }

        private void ChkCurrent_CheckedChanged(object sender, EventArgs e)
        {
            ShowOrHideCurve();
        }

        private void ChkDaqCurrent_CheckedChanged(object sender, EventArgs e)
        {
            ShowOrHideCurve();
        }

        private void ChkDaqTorque_CheckedChanged(object sender, EventArgs e)
        {
            ShowOrHideCurve();
        }

        private void BtnExportFile_Click(object sender, EventArgs e)
        {
            if (FilterDaqTime == null)
            {
                MessageBox.Show("数据集为空，无法导出！");
                return;
            }


            var saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
            saveFileDialog.Title = "Export to CSV";
            // saveFileDialog.FileName = "data_export_" + DateTime.Now.ToString("yyyyMMdd") + ".csv";
            saveFileDialog.FileName = ExportFile;

            if (saveFileDialog.ShowDialog() == DialogResult.OK)
                try
                {
                    ProgressShow.Visible = true;
                    ProgressShow.BringToFront(); // 确保在最上层
                    Application.DoEvents();


                    ExportData(FilterDaqTime, FilterDaqRelTime, FilterDaqBrakeNo, CanBrakeNo, CanForce, CanCurrent,
                        filterCurrent, saveFileDialog.FileName);

                    ProgressShow.Visible = false;

                    Application.DoEvents();

                    MessageBox.Show("导出完成！");
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
        }


        private void MakeDirectionMapping()
        {
            DirectionToRecvFrame.Clear();
            DirectionToRecvFrame["FL"] = (uint)Convert.ToInt32(ClsGlobal.FL_Recv, 16);
            DirectionToRecvFrame["FR"] = (uint)Convert.ToInt32(ClsGlobal.FR_Recv, 16);
            DirectionToRecvFrame["RL"] = (uint)Convert.ToInt32(ClsGlobal.RL_Recv, 16);
            DirectionToRecvFrame["RR"] = (uint)Convert.ToInt32(ClsGlobal.RR_Recv, 16);
        }


        public void LoadEMBHandlerAndFrameNo(string xmlPath)
        {
            try
            {
                // 创建DataTable结构
                var dt = new DataTable();
                dt.Columns.Add("名称", typeof(string));
                dt.Columns.Add("型号", typeof(string));
                dt.Columns.Add("产品编号", typeof(string));
                dt.Columns.Add("方向", typeof(string));

                // 加载XML文件

                var xdoc = XDocument.Load(xmlPath);

                // 解析XML数据
                foreach (var emb in xdoc.Descendants("EPB"))
                    dt.Rows.Add(
                        (string)emb.Element("名称"),
                        (string)emb.Element("型号"),
                        (string)emb.Element("产品编号"),
                        (string)emb.Element("方向")
                    );

                for (var i = 0; i < dt.Rows.Count; i++)
                    EMBToDirection[dt.Rows[i]["名称"].ToString()] = dt.Rows[i]["方向"].ToString();

                var sortedKeys = EMBToDirection.Keys.OrderBy(key => key).ToList();

                var handleNo = -1;

                foreach (var key in sortedKeys)
                {
                    handleNo++;

                    EMBHandlerToRecvFrame[handleNo] = DirectionToRecvFrame[EMBToDirection[key]];
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载EMB配置失败: {ex.Message}");
            }
        }

        public void LoadCanDbc()
        {
            foreach (var frame in EMBHandlerToRecvFrame)
            {
                var SendFactor = 0.0;
                var SendOffset = 0.0;
                var DbcMsg = DbcParser.TryGetFactorOffset(ClsGlobal.Dbc, frame.Value, "actClampForce", out SendFactor,
                    out SendOffset);
                if (DbcMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    MessageBox.Show(DbcMsg);
                    return;
                }

                EMBHandlerToRecvCanForceScale[frame.Key] = SendFactor;
                EMBHandlerToRecvCanForceOffset[frame.Key] = SendOffset;

                var EmbName = "EPB" + (frame.Key + 1);

                EMBNameToRecvCanForceScale[EmbName] = SendFactor;
                EMBNameToRecvCanForceOffset[EmbName] = SendOffset;
            }


            foreach (var frame in EMBHandlerToRecvFrame)
            {
                var SendFactor = 0.0;
                var SendOffset = 0.0;
                var DbcMsg = DbcParser.TryGetFactorOffset(ClsGlobal.Dbc, frame.Value, "dcCurrent", out SendFactor,
                    out SendOffset);
                if (DbcMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    MessageBox.Show(DbcMsg);
                    return;
                }

                EMBHandlerToRecvCanCurrentScale[frame.Key] = SendFactor;
                EMBHandlerToRecvCanCurrentOffset[frame.Key] = SendOffset;

                var EmbName = "EPB" + (frame.Key + 1);

                EMBNameToRecvCanCurrentScale[EmbName] = SendFactor;
                EMBNameToRecvCanCurrentOffset[EmbName] = SendOffset;
            }

            foreach (var frame in EMBHandlerToRecvFrame)
            {
                var SendFactor = 0.0;
                var SendOffset = 0.0;
                var DbcMsg = DbcParser.TryGetFactorOffset(ClsGlobal.Dbc, frame.Value, "actTorque", out SendFactor,
                    out SendOffset);
                if (DbcMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    MessageBox.Show(DbcMsg);
                    return;
                }

                EMBHandlerToRecvCanTorqueScale[frame.Key] = SendFactor;
                EMBHandlerToRecvCanTorqueOffset[frame.Key] = SendOffset;

                var EmbName = "EPB" + (frame.Key + 1);

                EMBNameToRecvCanTorqueScale[EmbName] = SendFactor;
                EMBNameToRecvCanTorqueOffset[EmbName] = SendOffset;
            }
        }

        /// <summary>
        ///     选择文件夹
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BtnChoiceFolder_Click(object sender, EventArgs e)
        {
            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = @"选择文件夹";
                folderDialog.ShowNewFolderButton = false;
                folderDialog.SelectedPath = @"D:\Github\wanxiang\EPBTest\MTTfTest\bin\Debug\DataStore\"; // 默认选中的路径

                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    selectedPath = folderDialog.SelectedPath;

                    try
                    {
                        EpbNo = int.Parse(CmbEpbNo.Text.Replace("EPB", ""));
                        EpbName = CmbEpbNo.Text;


                        // 获取文件夹中所有文件
                        allFiles = new DirectoryInfo(selectedPath).GetFiles("*.*", SearchOption.TopDirectoryOnly);
                        // var SelectPart = allFiles.Where(file => FilterCondition(file, "CAN" + EpbNo + "_Raw", "bin")) // 暂时注释
                        var SelectPart = allFiles
                            .Where(file => FilterCondition(file, "DAQ_Dev1_Raw", "bin")) // 指定显示Dev1的记录文件
                            .OrderBy(file => file.CreationTime) // 按创建时间升序
                            .ToArray();

                        // 提取纯文件名（不含路径）
                        var fileNames = SelectPart
                            .Select(file => file.Name)
                            .ToArray();

                        LbFileList.Items.Clear();
                        LbFileList.Items.AddRange(fileNames);

                        var xmlPath = Path.Combine(selectedPath, @"TestConfig.xml");
                        // LoadTestConfigFromXml(xmlPath); // 暂时注释

                        xmlPath = Path.Combine(selectedPath, @"EMBControl.XML");
                        //LoadEMBHandlerAndFrameNo(xmlPath); // 暂时注释


                        // LoadCanDbc();


                        var ReadMsg = ClsXmlOperation.GetDaqScaleMapping(selectedPath + @"\AIConfig.xml", "Dev1",
                            out ParaNameToScale);
                        if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                        {
                            MessageBox.Show(ReadMsg);
                            return;
                        }

                        ReadMsg = ClsXmlOperation.GetDaqOffsetMapping(selectedPath + @"\AIConfig.xml", "Dev1",
                            out ParaNameToOffset);
                        if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                        {
                            MessageBox.Show(ReadMsg);
                            return;
                        }

                        ReadMsg = ClsXmlOperation.GetDaqZeroValueMapping(selectedPath + @"\AIConfig.xml", "Dev1",
                            out ParaNameToZeroValue);
                        if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0) MessageBox.Show(ReadMsg);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($@"错误: {ex.Message}");
                    }
                }
            }
        }


        private bool FilterCondition(FileInfo file, string nameFilter, string extensionFilter)
        {
            // 1. 文件名过滤
            var nameValid = string.IsNullOrWhiteSpace(nameFilter) ||
                            file.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0;

            // 2. 扩展名过滤
            var extensionValid = string.IsNullOrWhiteSpace(extensionFilter) ||
                                 extensionFilter.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Any(ext =>
                                         file.Extension.Equals(
                                             ext.StartsWith(".") ? ext : "." + ext,
                                             StringComparison.OrdinalIgnoreCase));

            // 3. 组合条件
            return nameValid && extensionValid;
        }


        /// <summary>
        ///     选择具体文件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LbFileList_DoubleClick(object sender, EventArgs e)
        {
            try
            {
                SafeFile = LbFileList.SelectedItem.ToString();
                var CurFileName = selectedPath + "\\" + SafeFile;
                ExportFile = CurFileName.Replace(".bin", ".csv");


                CanForce = null;
                CanCurrent = null;
                CanBrakeNo = null;
                CanSourceTime = null;
                DaqBrakeNo = null;
                DaqSourceTime = null;
                DaqCurrent = null;
                filterCurrent = null;
                CanRelTime = null;
                FilterDaqRelTime = null;
                FilterDaqBrakeNo = null;
                FilterDaqTime = null;

                listForce.Clear();
                listDaqCurrent.Clear();
                listCanCurrent.Clear();
                zedGraphControlHistory.AxisChange();
                zedGraphControlHistory.Invalidate();

                ProgressShow.Location = new Point(
                    zedGraphControlHistory.Left + (zedGraphControlHistory.Width - ProgressShow.Width) / 2,
                    zedGraphControlHistory.Top + (zedGraphControlHistory.Height - ProgressShow.Height) / 2
                );

                ProgressShow.Visible = true;
                ProgressShow.BringToFront(); // 确保在最上层
                Application.DoEvents();


                bgwA.RunWorkerAsync(CurFileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        public void LoadTestConfigFromXml(string xmlPath)
        {
            try
            {
                if (!File.Exists(xmlPath))
                {
                    MessageBox.Show("未发现试验信息文件！");
                    return;
                }


                testConfig = LoadTestConfigFromFile(xmlPath);
                if (testConfig == null)
                {
                    MessageBox.Show("试验信息文件读取失败！");
                    return;
                }

                RtbTestInfo.Clear();

                testConfig.TestSpan = 1.0 / double.Parse(testConfig.TestCycle);

                RtbTestInfo.AppendText("试验名称: " + testConfig.TestName + "\n");
                RtbTestInfo.AppendText("试验环境: " + testConfig.TestEnvir + "\n");
                RtbTestInfo.AppendText("试验周期: " + testConfig.TestSpan.ToString("f2") + "S\n");
                RtbTestInfo.AppendText("试验次数: " + testConfig.TestTarget + "\n");
            }

            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }


        private TestConfig LoadTestConfigFromFile(string xmlPath)
        {
            try
            {
                var serializer = new XmlSerializer(typeof(TestConfig));
                using (var reader = new StreamReader(xmlPath))
                {
                    return (TestConfig)serializer.Deserialize(reader);
                }
            }
            catch
            {
                return new TestConfig(); // 返回空配置避免异常
            }
        }

        private void CmbEmbNo_SelectedIndexChanged(object sender, EventArgs e)
        {
            EpbNo = int.Parse(CmbEpbNo.Text.Replace("EPB", ""));
            EpbName = CmbEpbNo.Text;

            if (selectedPath.Length < 1) return;

            // 获取文件夹中所有文件
            allFiles = new DirectoryInfo(selectedPath).GetFiles("*.*", SearchOption.TopDirectoryOnly);
            var SelectPart = allFiles.Where(file => FilterCondition(file, "CAN" + EpbNo + "_Raw", "bin"))
                .OrderBy(file => file.CreationTime) // 按创建时间升序
                .ToArray();

            // 提取纯文件名（不含路径）
            var fileNames = SelectPart
                .Select(file => file.Name)
                .ToArray();

            LbFileList.Items.Clear();
            LbFileList.Items.AddRange(fileNames);
        }


        private void ExportData(
            DateTime[] FilterDaqTime,
            double[] FilterDaqRelTime,
            int[] FilterDaqBrakeNo,
            int[] CanBrakeNo,
            double[] CanForce,
            double[] CanCurrent,
            double[] filterCurrent,
            string ExportFileName)
        {
            // 验证数组长度一致性
            var baseLength = FilterDaqBrakeNo.Length;
            if (FilterDaqTime.Length != baseLength ||
                FilterDaqRelTime.Length != baseLength)
                //||
                //filterCurrent.Length != baseLength)
                throw new ArgumentException(
                    "FilterDaqTime, FilterDaqRelTime, FilterDaqBrakeNo and filterCurrent arrays must have the same length");

            var canLength = CanBrakeNo.Length;
            if (CanForce.Length != canLength || CanCurrent.Length != canLength)
                throw new ArgumentException("CanBrakeNo, CanForce and CanCurrent arrays must have the same length");

            // 创建调整后的列表
            var adjustedTime = new List<DateTime>();
            var adjustedRelTime = new List<double>();
            var adjustedDaqBrake = new List<int>();
            var adjustedCanBrake = new List<int>();
            var adjustedCanForce = new List<double>();
            var adjustedCanCurrent = new List<double>();
            var adjustedFilterCurrent = new List<double>();

            var j = 0; // CAN数据的索引
            var lastCanForce = 0.0;
            var lastCanCurrent = 0.0;
            var hasPreviousCanValue = false;

            // 处理每一行数据
            for (var i = 0; i < baseLength; i++)
                // 检查是否需要插入行
                if (j < canLength && FilterDaqBrakeNo[i] < CanBrakeNo[j])
                {
                    // 插入新行（使用上一个CAN值）
                    adjustedTime.Add(FilterDaqTime[i]);
                    adjustedRelTime.Add(FilterDaqRelTime[i]);
                    adjustedDaqBrake.Add(FilterDaqBrakeNo[i]);
                    adjustedCanBrake.Add(FilterDaqBrakeNo[i]); // 对齐到DAQ刹车号
                    adjustedFilterCurrent.Add(filterCurrent[i]);

                    if (hasPreviousCanValue)
                    {
                        adjustedCanForce.Add(lastCanForce);
                        adjustedCanCurrent.Add(lastCanCurrent);
                    }
                    else
                    {
                        // 如果没有先前的CAN值，使用0作为默认值
                        adjustedCanForce.Add(0.0);
                        adjustedCanCurrent.Add(0.0);
                    }
                }
                // 检查是否需要跳过当前CAN行
                else if (j < canLength && FilterDaqBrakeNo[i] > CanBrakeNo[j])
                {
                    // 保存当前CAN值，但不添加到输出
                    lastCanForce = CanForce[j];
                    lastCanCurrent = CanCurrent[j];
                    hasPreviousCanValue = true;
                    j++;
                    i--; // 重新处理当前DAQ行
                }
                // 正常处理匹配的行
                else if (j < canLength && FilterDaqBrakeNo[i] == CanBrakeNo[j])
                {
                    // 添加当前行
                    adjustedTime.Add(FilterDaqTime[i]);
                    adjustedRelTime.Add(FilterDaqRelTime[i]);
                    adjustedDaqBrake.Add(FilterDaqBrakeNo[i]);
                    adjustedCanBrake.Add(CanBrakeNo[j]);
                    adjustedCanForce.Add(CanForce[j]);
                    adjustedCanCurrent.Add(CanCurrent[j]);
                    adjustedFilterCurrent.Add(filterCurrent[i]);

                    // 保存当前CAN值
                    lastCanForce = CanForce[j];
                    lastCanCurrent = CanCurrent[j];
                    hasPreviousCanValue = true;
                    j++;
                }
                // 处理CAN数据耗尽的情况
                else if (j >= canLength)
                {
                    // 添加剩余DAQ行（使用上一个CAN值）
                    adjustedTime.Add(FilterDaqTime[i]);
                    adjustedRelTime.Add(FilterDaqRelTime[i]);
                    adjustedDaqBrake.Add(FilterDaqBrakeNo[i]);
                    adjustedCanBrake.Add(FilterDaqBrakeNo[i]); // 对齐到DAQ刹车号
                    adjustedFilterCurrent.Add(filterCurrent[i]);

                    if (hasPreviousCanValue)
                    {
                        adjustedCanForce.Add(lastCanForce);
                        adjustedCanCurrent.Add(lastCanCurrent);
                    }
                    else
                    {
                        adjustedCanForce.Add(0.0);
                        adjustedCanCurrent.Add(0.0);
                    }
                }

            // 写入CSV文件
            using (var writer = new StreamWriter(ExportFileName, false, Encoding.UTF8))
            {
                // 写入标题行
                writer.WriteLine("TimeStamp,RelTime,DAQBrakeNo,CanBrakeNo,CanForce,CanCurrent,DAQCurrent");

                // 写入数据行
                for (var i = 0; i < adjustedTime.Count; i++)
                    writer.WriteLine(
                        $"{adjustedTime[i]:yyyy-MM-dd HH:mm:ss.fff}," +
                        $"{adjustedRelTime[i]:0.000}," +
                        $"{adjustedDaqBrake[i]}," +
                        $"{adjustedCanBrake[i]}," +
                        $"{adjustedCanForce[i]:0.000}," +
                        $"{adjustedCanCurrent[i]:0.000}," +
                        $"{adjustedFilterCurrent[i]:0.000}");
            }
        }

        private void BtnPanLeft_Click(object sender, EventArgs e)
        {
            PercentPanXAxis(-0.9);
        }

        private void BtnPanRight_Click(object sender, EventArgs e)
        {
            PercentPanXAxis(0.9);
        }

        private void PercentPanXAxis(double shift)
        {
            if (zedGraphControlHistory.GraphPane == null) return;
            var pane = zedGraphControlHistory.GraphPane;

            var AbsValue = (pane.XAxis.Scale.Max - pane.XAxis.Scale.Min) * shift;


            // 计算新范围
            var newMin = pane.XAxis.Scale.Min + AbsValue;
            var newMax = pane.XAxis.Scale.Max + AbsValue;

            // 可选：检查范围是否超出数据边界
            if (newMin < XAxisMin) newMin = XAxisMin;

            if (newMax > XAxisMax) newMax = XAxisMax;

            // 应用新范围
            pane.XAxis.Scale.Min = newMin;
            pane.XAxis.Scale.Max = newMax;

            // 刷新图表
            zedGraphControlHistory.AxisChange();
            zedGraphControlHistory.Invalidate();
        }
    }
}