using DataOperation;
using MtEmbTest;
using NationalInstruments.DAQmx;
using Sunny.UI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZedGraph;

namespace MTEmbTest
{
    public partial class FrmDAQCalibrate: Form
    {
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        private bool IsCalcZero = false;
        private int ZeroCounter = 0;

     
        private List<double> CurrentZeroList = new List<double>();

      
        private List<double> TorqueZeroList = new List<double>();

     
        private List<double> PressureZeroList = new List<double>();

   
        private List<double> DistanceZeroList = new List<double>();


        private LineItem curveDaqCurrent;
        private PointPairList listDaqCurrent;

        private LineItem curveDaqTorque;
        private PointPairList listDaqTorque;

        private LineItem curveDaqPressure;
        private PointPairList listDaqPressure;

        private LineItem curveDaqDistance;
        private PointPairList listDaqDistance;



        private double AiMaxVoltage = 10.0;
        private double AiMinVoltage = -10.0;


        private double DaqDeltTime = 0.01;
        private double DaqTimeSpanMilSeconds = 10.0;

        private bool IsRunning = false;
        private double DaqCurrentTimeOffset = 0.0;

        private ConcurrentDictionary<string, double> ParaNameToScale = new ConcurrentDictionary<string, double>();
        private ConcurrentDictionary<string, double> ParaNameToOffset = new ConcurrentDictionary<string, double>();
        private ConcurrentDictionary<string, double> ParaNameToZeroValue = new ConcurrentDictionary<string, double>();

        private ConcurrentDictionary<string, int> ParaNameToActChannel = new ConcurrentDictionary<string, int>();
        private ConcurrentDictionary<string, string> PhyChannelToParaName = new ConcurrentDictionary<string, string>();


        private readonly Dictionary<string, LineItem> curveDictionary = new Dictionary<string, LineItem>();

        private static string[] Dev1UsedDaqAIChannels;
        private NationalInstruments.DAQmx.Task Dev1analogTask;
        private AnalogMultiChannelReader Dev1analogReader;
        private AsyncCallback Dev1analogCallback;
        private NationalInstruments.DAQmx.Task Dev1runningAnalogTask;


        private ConcurrentQueue<double[]> DaqAiCurrentDispData = new ConcurrentQueue<double[]>();
        private ConcurrentQueue<double[]> DaqAiTorqueDispData = new ConcurrentQueue<double[]>();
        private ConcurrentQueue<double[]> DaqAiPressureDispData = new ConcurrentQueue<double[]>();
        private ConcurrentQueue<double[]> DaqAiDistanceDispData = new ConcurrentQueue<double[]>();

        


        private const int DaqAiDispDataLens = 10;

        


        private readonly object graphLock = new object();   //曲线更新锁
        private readonly object currentDevLock = new object();
        private string _currentDev = "EMB1"; // 添加私有字段

        public string CurrentDev
        {
            get
            {
                lock (currentDevLock)
                {
                    return _currentDev;
                }
            }
            set
            {
                lock (currentDevLock)
                {
                    _currentDev = value;
                }
            }
        }

        private void MakeCurveMapping()
        {
            curveDictionary.Clear();
          
            curveDictionary.Add("DAQ_Current", curveDaqCurrent);
            curveDictionary.Add("DAQ_Torque", curveDaqTorque);
            curveDictionary.Add("DAQ_Pressure", curveDaqPressure);
            curveDictionary.Add("DAQ_Distance", curveDaqDistance);
        }


        public FrmDAQCalibrate()
        {
            InitializeComponent();
            // 创建自定义标题栏
            Panel titleBar = new Panel
            {
                Height = 30,
                Dock = DockStyle.Top,
                BackColor = Color.AliceBlue
            };

            // 添加自定义按钮
            Button btnClose = new Button
            {
                Text = "X",
                Size = new Size(30, 30),
                Dock = DockStyle.Right
            };
            btnClose.Click += (s, e) => this.Close();

            titleBar.Controls.Add(btnClose);
            this.Controls.Add(titleBar);

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

        private void InitializeCurve()
        {
            try
            {
                int fontSize = 10;



                // 保留原有初始化代码
                GraphPane pane = zedGraphDAQCalibrate.GraphPane;
                // 设置 X 轴和 Y 轴以及刻度线为灰色


                pane.XAxis.Color = Color.Gray;
                pane.XAxis.MajorTic.Color = Color.Gray;
                pane.XAxis.MinorTic.Size = 0.0f;

                pane.YAxis.Color = Color.Gray;
                pane.YAxis.MajorTic.Color = Color.Gray;
                pane.YAxis.MinorTic.Size = 0.0f;



                pane.Title.IsVisible = false;
                pane.XAxis.Title.Text = "Time";
                pane.YAxis.Title.IsVisible = false;
                pane.XAxis.Title.IsVisible = false;


               

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



                zedGraphDAQCalibrate.GraphPane.XAxis.Scale.Max = ClsGlobal.XDuration;
                zedGraphDAQCalibrate.GraphPane.XAxis.Scale.Min = 0.0;


                zedGraphDAQCalibrate.GraphPane.XAxis.Scale.MagAuto = false;
                zedGraphDAQCalibrate.GraphPane.XAxis.Scale.FormatAuto = false;


                zedGraphDAQCalibrate.GraphPane.YAxis.Scale.MagAuto = false;
                zedGraphDAQCalibrate.GraphPane.YAxis.Scale.FormatAuto = false;



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


                pane.Y2Axis.IsVisible = true;

                pane.Y2Axis.Title.FontSpec.FontColor = Color.Lime;
                pane.Y2Axis.Scale.FontSpec.FontColor = Color.Lime;
                pane.Y2Axis.Title.FontSpec.Size = fontSize;
                pane.Y2Axis.Scale.FontSpec.Size = fontSize;
                pane.Y2Axis.MajorGrid.IsVisible = false;
                pane.Y2Axis.MajorGrid.Color = Color.LimeGreen;
                pane.Y2Axis.MajorGrid.DashOn = float.MaxValue;
                pane.Y2Axis.MajorGrid.DashOff = 0;

                pane.Y2Axis.Color = Color.Gray;
                pane.Y2Axis.MajorTic.Color = Color.Gray;
                pane.Y2Axis.MinorTic.Size = 0.0f;



                listDaqPressure = new PointPairList();
                curveDaqPressure = pane.AddCurve("DAQ_Pressure(Bar)", listDaqPressure, Color.FromArgb(80, 160, 255), SymbolType.None);
                curveDaqPressure.Line.Width = 2;

              

                listDaqCurrent = new PointPairList();
                curveDaqCurrent = pane.AddCurve("DAQ_Current(A)", listDaqCurrent, Color.Lime, SymbolType.None);
                curveDaqCurrent.Line.Width = 2;
                curveDaqCurrent.IsY2Axis = true;


                listDaqTorque = new PointPairList();
                curveDaqTorque = pane.AddCurve("DAQ_Torque(Nm)", listDaqTorque, Color.Orange, SymbolType.None);
                curveDaqTorque.Line.Width = 2;
                curveDaqTorque.IsY2Axis = true;

                // 创建新的Y轴并添加到图表中
                // var torqueYAxis = new YAxis("Torque (Nm)");
                var torqueYAxis = new YAxis("");
                pane.YAxisList.Add(torqueYAxis);

               

                //distanceYAxis.Cross = pane.XAxis.Scale.Max;       // 关键设置：将轴定位在X轴最大值处（右侧）
                //distanceYAxis.CrossAuto = false;           // 禁用自动计算交叉点
                //distanceYAxis.Scale.Align = AlignP.Outside; // 刻度标签朝内显示
                //distanceYAxis.MinSpace = 30;



                // 为曲线指定新创建的Y轴
                curveDaqTorque.YAxisIndex = pane.YAxisList.Count - 1;
                curveDaqTorque.IsY2Axis = false; // 明确使用新添加的Y轴而非Y2Axis

              

                torqueYAxis.Title.FontSpec.FontColor = Color.Orange;
                torqueYAxis.Scale.FontSpec.FontColor = Color.Orange;
                torqueYAxis.Title.FontSpec.Size = fontSize;
                torqueYAxis.Scale.FontSpec.Size = fontSize;



                torqueYAxis.MajorGrid.IsVisible = true;
                torqueYAxis.Color = Color.Orange;




                listDaqDistance = new PointPairList();
                curveDaqDistance = pane.AddCurve("DAQ_Distance(mm)", listDaqDistance, Color.Cyan, SymbolType.None);
                curveDaqDistance.Line.Width = 2;




                var distanceYAxis = new Y2Axis("");
                pane.Y2AxisList.Add(distanceYAxis);


                distanceYAxis.Title.FontSpec.FontColor = Color.Cyan;
                distanceYAxis.Scale.FontSpec.FontColor = Color.Cyan;
                distanceYAxis.Title.FontSpec.Size = fontSize;
                distanceYAxis.Scale.FontSpec.Size = fontSize;
                distanceYAxis.IsVisible = true;


                distanceYAxis.MajorGrid.IsVisible = true;
                distanceYAxis.Color = Color.Cyan;


                curveDaqDistance.IsY2Axis = true;

                curveDaqDistance.YAxisIndex = pane.Y2AxisList.Count - 1;

                zedGraphDAQCalibrate.AxisChange();

                zedGraphDAQCalibrate.Invalidate();

                zedGraphDAQCalibrate.Refresh();
            }

            catch (Exception ex)
            {
                SafeLogError($"初始化曲线显示失败: {ex.Message}");
            }
        }

        private void FrmDAQCalibrate_Load(object sender, EventArgs e)
        {

           string ReadMsg = ClsXmlOperation.GetDaqAIUsedChannels(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out Dev1UsedDaqAIChannels);
            if (ReadMsg.IndexOf("OK") < 0)
            {
                MessageBox.Show(ReadMsg);
                return;
            }

            if (Dev1UsedDaqAIChannels.Length < 1)
            {
                MessageBox.Show("未读取到 Dev1 DAQ AI 相关信息！");
                return;
            }


            ReadMsg = ClsXmlOperation.GetDaqPhyChanelToNameMapping(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out PhyChannelToParaName);
            if (ReadMsg.IndexOf("OK") < 0)
            {
                MessageBox.Show(ReadMsg);
                return;
            }


            for (int i = 0; i < Dev1UsedDaqAIChannels.Length; i++)
            {
                ParaNameToActChannel.TryAdd(PhyChannelToParaName[Dev1UsedDaqAIChannels[i]], i);
            }

          


            ReadMsg = ClsXmlOperation.GetDaqScaleMapping(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToScale);
            if (ReadMsg.IndexOf("OK") < 0)
            {
                MessageBox.Show(ReadMsg);
                return;
            }

            ReadMsg = ClsXmlOperation.GetDaqOffsetMapping(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToOffset);
            if (ReadMsg.IndexOf("OK") < 0)
            {
                MessageBox.Show(ReadMsg);
                return;
            }

            ReadMsg = ClsXmlOperation.GetDaqZeroValueMapping(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToZeroValue);
            if (ReadMsg.IndexOf("OK") < 0)
            {
                MessageBox.Show(ReadMsg);
                return;
            }


            InitializeCurve();
            MakeCurveMapping();
        }

        private bool Dev1StartDaqAITask()
        {
            try
            {
                Dev1analogTask = new NationalInstruments.DAQmx.Task("Dev1AITask");
                int Dev1AiChannelCounts = Dev1UsedDaqAIChannels.Length;

                for (int i = 0; i < Dev1AiChannelCounts; i++)
                {
                    Dev1analogTask.AIChannels.CreateVoltageChannel(Dev1UsedDaqAIChannels[i],
                    "",
                    AITerminalConfiguration.Differential,
                    AiMinVoltage,
                    AiMaxVoltage,
                    AIVoltageUnits.Volts);
                }
                Dev1analogTask.Timing.ConfigureSampleClock("",
                           ClsGlobal.DaqFrequency,
                           SampleClockActiveEdge.Rising,
                           SampleQuantityMode.ContinuousSamples,
                           ClsGlobal.SamplesPerChannel);

                // Verify the tasks
                Dev1analogTask.Control(TaskAction.Verify);




                Dev1StartTask();


                Dev1analogTask.Start();


                // Start reading as well
                Dev1analogCallback = new AsyncCallback(Dev1AnalogRead);
                Dev1analogReader = new AnalogMultiChannelReader(Dev1analogTask.Stream);



               
                Dev1analogReader.SynchronizeCallbacks = true;


                Dev1analogReader.BeginReadMultiSample(ClsGlobal.SamplesPerChannel, Dev1analogCallback, Dev1analogTask);



                return true;
            }
            catch (Exception ex)
            {
                Dev1StopTask();
                SafeLogError($"启动采集卡失败: {ex.Message}");
                return false;
            }
        }


        private void Dev1StartTask()
        {
            try
            {
                if (Dev1runningAnalogTask == null)
                {
                    // Change state
                    Dev1runningAnalogTask = Dev1analogTask;
                }
            }
            catch (Exception ex)
            {
                // MessageBox.Show("启动数据采集卡错误： " + ex.Message);
                SafeLogError($"启动数据采集卡错误: {ex.Message}");
            }
        }

        private void Dev1StopTask()
        {
            try
            {
                // Change state
                Dev1runningAnalogTask = null;
                // Stop tasks
                Dev1analogTask.Stop();
                Dev1analogTask.Dispose();

            }

            catch (Exception ex)
            {
                //  MessageBox.Show("停止数据采集卡错误： " + ex.Message);
                SafeLogError($"停止数据采集卡错误: {ex.Message}");
            }
        }


       


        private void Dev1AnalogRead(IAsyncResult ar)
        {
            try
            {
                if (!IsRunning)
                {
                    return;
                }

                DateTime RecvTime = DateTime.Now;

                if (Dev1runningAnalogTask != null && Dev1runningAnalogTask == ar.AsyncState)
                {

                    double[,] data = Dev1analogReader.EndReadMultiSample(ar);
                    if (data == null)
                    {
                        SafeLogError($"DAQ Dev1 未读取到数据 ");
                        return;
                    }

                    if (IsRunning)
                    {
                        int DaqDispCurrentNo = ParaNameToActChannel["EMB1_current"]; 
                        double[] DispCurrentData = new double[ClsGlobal.SamplesPerChannel];
                        Buffer.BlockCopy(data, DaqDispCurrentNo * 8 * ClsGlobal.SamplesPerChannel, DispCurrentData, 0, 8 * ClsGlobal.SamplesPerChannel);

                        int DaqDispTorqueNo = ParaNameToActChannel["EMB1_torque"];
                        double[] DispTorqueData = new double[ClsGlobal.SamplesPerChannel];
                        Buffer.BlockCopy(data, DaqDispTorqueNo * 8 * ClsGlobal.SamplesPerChannel, DispTorqueData, 0, 8 * ClsGlobal.SamplesPerChannel);

                        int DaqDispPressureNo = ParaNameToActChannel["EMB1_valveBar"];
                        double[] DispPressureData = new double[ClsGlobal.SamplesPerChannel];
                        Buffer.BlockCopy(data, DaqDispPressureNo * 8 * ClsGlobal.SamplesPerChannel, DispPressureData, 0, 8 * ClsGlobal.SamplesPerChannel);

                        int DaqDispDistanceNo = ParaNameToActChannel["EMB1_distance"];
                        double[] DispDistanceData = new double[ClsGlobal.SamplesPerChannel];
                        Buffer.BlockCopy(data, DaqDispDistanceNo * 8 * ClsGlobal.SamplesPerChannel, DispDistanceData, 0, 8 * ClsGlobal.SamplesPerChannel);


                        AddToDaqAiDispCache(DaqAiDispDataLens, DispCurrentData, ref DaqAiCurrentDispData);
                        AddToDaqAiDispCache(DaqAiDispDataLens, DispTorqueData, ref DaqAiTorqueDispData);
                        AddToDaqAiDispCache(DaqAiDispDataLens, DispPressureData, ref DaqAiPressureDispData);
                        AddToDaqAiDispCache(DaqAiDispDataLens, DispDistanceData, ref DaqAiDistanceDispData);




                    }






                    Dev1analogReader.BeginReadMultiSample(ClsGlobal.SamplesPerChannel, Dev1analogCallback, Dev1analogTask);
                }


            }
            catch (Exception ex)
            {
                Dev1StopTask();
                SafeLogError($"DAQ Dev1 读取数据出错: {ex.Message}");

                System.Threading.Tasks.Task.Run(() =>
                {
                    const int maxRetries = 30;
                    int retryCount = 0;
                    bool success = false;

                    while (retryCount < maxRetries && !success)
                    {
                        retryCount++;
                        try
                        {
                            if (this.InvokeRequired)
                            {
                                this.Invoke(new Action(() =>
                                {
                                    try
                                    {
                                        success = Dev1StartDaqAITask();
                                        if (success)
                                        {
                                            SafeLogError($"第 {retryCount} 次重连成功");
                                        }
                                    }
                                    catch (Exception invokeEx)
                                    {
                                        SafeLogError($"第 {retryCount} 次重试失败: {invokeEx.Message}");
                                    }
                                }));
                            }

                            if (success) break;
                            Thread.Sleep(1000);
                        }
                        catch (Exception retryEx)
                        {
                            SafeLogError($"重试过程异常: {retryEx.Message}");
                        }
                    }

                    if (!success)
                    {
                        SafeLogError($"采集卡重连失败（共尝试 {maxRetries} 次），请检查硬件连接");
                    }
                });
            }
        }

        private void AddToDaqAiDispCache(int maxLens, double[] Data, ref ConcurrentQueue<double[]> daqAiDispData)
        {
            try
            {
                daqAiDispData.Enqueue(Data);
                if (daqAiDispData.Count > maxLens)
                {
                    double[] removedData;
                    daqAiDispData.TryDequeue(out removedData);
                }
            }
            catch (Exception ex)
            {
               

                SafeLogError($"缓存 Dev1 显示数据出错:  {ex.Message} ");

            }
        }


        private void SafeLogError(string message)
        {
            var formattedMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  > {message}";

            if (RtbInfo.InvokeRequired)
            {
                //RtbInfo.Invoke(new Action(() =>

                //RtbInfo.AppendText(formattedMessage + Environment.NewLine)


                //));

                RtbInfo.BeginInvoke(new Action(() =>
                {
                    RtbInfo.AppendText(formattedMessage + Environment.NewLine);
                    RtbInfo.ScrollToCaret();
                }));

            }
            else
            {
                RtbInfo.AppendText(formattedMessage + Environment.NewLine);
                RtbInfo.ScrollToCaret();
            }
        }

       

        private double[] ProcessDaqCurrentData()
        {
            int RecCount = DaqAiCurrentDispData.Count;
            int totalCount = DaqAiCurrentDispData.Take(RecCount).Sum(arr => arr.Length);


            double[] result = new double[totalCount];
            int index = 0;
            int RecCounter = 0;
            while (DaqAiCurrentDispData.TryDequeue(out var arr) && RecCounter < RecCount)
            {
                Array.Copy(arr, 0, result, index, arr.Length);
                index += arr.Length;
                RecCounter++;
            }
            return result;
        }

        private double[] ProcessDaqTorqueData()
        {
            int RecCount = DaqAiTorqueDispData.Count;
            int totalCount = DaqAiTorqueDispData.Take(RecCount).Sum(arr => arr.Length);
            double[] result = new double[totalCount];
            int index = 0;
            int RecCounter = 0;
            while (DaqAiTorqueDispData.TryDequeue(out var arr) && RecCounter < RecCount)
            {
                Array.Copy(arr, 0, result, index, arr.Length);
                index += arr.Length;
            }
            return result;
        }


        private double[] ProcessDaqPressureData()
        {
            int RecCount = DaqAiPressureDispData.Count;
            int totalCount = DaqAiPressureDispData.Take(RecCount).Sum(arr => arr.Length);
            double[] result = new double[totalCount];
            int index = 0;
            int RecCounter = 0;
            while (DaqAiPressureDispData.TryDequeue(out var arr) && RecCounter < RecCount)
            {
                Array.Copy(arr, 0, result, index, arr.Length);
                index += arr.Length;
            }
            return result;
        }


        private double[] ProcessDaqDistanceData()
        {
            int RecCount = DaqAiDistanceDispData.Count;
            int totalCount = DaqAiDistanceDispData.Take(RecCount).Sum(arr => arr.Length);
            double[] result = new double[totalCount];
            int index = 0;
            int RecCounter = 0;
            while (DaqAiDistanceDispData.TryDequeue(out var arr) && RecCounter < RecCount)
            {
                Array.Copy(arr, 0, result, index, arr.Length);
                index += arr.Length;
            }
            return result;
        }


        private void TimerCalibrate_Tick(object sender, EventArgs e)
        {
            try
            {

                double[] DaqCurrentData = ProcessDaqCurrentData();
                double[] DaqTorqueData = ProcessDaqTorqueData();
                double[] DaqPressureData = ProcessDaqPressureData();
                double[] DaqDistanceData = ProcessDaqDistanceData();

                int DaqLens = DaqCurrentData.Length;


                double[] PhyCurrent = new double[DaqLens];
                double[] PhyTorque = new double[DaqLens];
                double[] PhyPressure = new double[DaqLens];
                double[] PhyDistance = new double[DaqLens];

                for (int i = 0; i < DaqLens; i++)
                {
                   
                    PhyCurrent[i] = (DaqCurrentData[i] - ParaNameToZeroValue["EMB1_current"]) * ParaNameToScale["EMB1_current"] + ParaNameToOffset["EMB1_current"];
                    PhyTorque[i] = (DaqTorqueData[i] - ParaNameToZeroValue["EMB1_torque"]) * ParaNameToScale["EMB1_torque"] + ParaNameToOffset["EMB1_torque"];
                    PhyPressure[i] = (DaqPressureData[i] - ParaNameToZeroValue["EMB1_torque"]) * ParaNameToScale["EMB1_valveBar"] + ParaNameToOffset["EMB1_valveBar"];
                    PhyDistance[i] = (DaqDistanceData[i] - ParaNameToZeroValue["EMB1_distance"]) * ParaNameToScale["EMB1_distance"] + ParaNameToOffset["EMB1_distance"];
                }



                double[] filterCurrent = ClsDataFilter.MakeMedianFilterReducePoint(ref PhyCurrent, ClsGlobal.MedianLens);
                double[] filterTorque = ClsDataFilter.MakeMedianFilterReducePoint(ref PhyTorque, ClsGlobal.MedianLens);
                double[] filterPressure = ClsDataFilter.MakeMedianFilterReducePoint(ref PhyPressure, ClsGlobal.MedianLens);
                double[] filterDistance = ClsDataFilter.MakeMedianFilterReducePoint(ref PhyDistance, ClsGlobal.MedianLens);



                //double[] filterCurrent = ClsDataFilter.MakeMedianFilterKeepPoint_V2(ref PhyCurrent, 11);
                //double[] filterTorque = ClsDataFilter.MakeMedianFilterKeepPoint_V2(ref PhyTorque, 11);
                //double[] filterPressure = ClsDataFilter.MakeMedianFilterKeepPoint_V2(ref PhyPressure, 11);
                //double[] filterDistance = ClsDataFilter.MakeMedianFilterKeepPoint_V2(ref PhyDistance, 11);







                int filterLens = filterCurrent.Length;


                for (int i = 0; i < filterLens; i++)
                {
                    listDaqCurrent.Add(DaqCurrentTimeOffset, filterCurrent[i]);
                    listDaqTorque.Add(DaqCurrentTimeOffset, filterTorque[i]);
                    listDaqPressure.Add(DaqCurrentTimeOffset, filterPressure[i]);
                    listDaqDistance.Add(DaqCurrentTimeOffset, filterDistance[i]);
                    DaqCurrentTimeOffset += DaqDeltTime;
                }

                if (listDaqCurrent != null && listDaqCurrent.Count > 0 && (listDaqCurrent[listDaqCurrent.Count - 1].X - listDaqCurrent[0].X) > ClsGlobal.XDuration)
                {
                    // 移除最旧的一半数据点

                    double MidTime = (listDaqCurrent[0].X + listDaqCurrent[listDaqCurrent.Count - 1].X) / 2.0;

                    listDaqCurrent.RemoveAll(p => p.X < MidTime);
                    listDaqTorque.RemoveAll(p => p.X < MidTime);
                    listDaqPressure.RemoveAll(p => p.X < MidTime);
                    listDaqDistance.RemoveAll(p => p.X < MidTime);

                    lock (graphLock)
                    {
                        zedGraphDAQCalibrate.GraphPane.XAxis.Scale.Max = listDaqCurrent[0].X + ClsGlobal.XDuration;
                        zedGraphDAQCalibrate.GraphPane.XAxis.Scale.Min = listDaqCurrent[0].X;
                    }
                  


                }


                lock (graphLock)
                {
                    // 刷新图表
                    zedGraphDAQCalibrate.AxisChange();
                    zedGraphDAQCalibrate.Invalidate();
                }


                UpdateDataGridView(filterCurrent, filterTorque, filterPressure, filterDistance);


             //   SafeLogError(filterCurrent.Length.ToString() + "," + PhyCurrent.Length.ToString() + "," + DaqCurrentData.Length.ToString());

                if (IsCalcZero&& ZeroCounter<16)
                {
                    ZeroCounter++;
                    if (ZeroCounter > 1)  //跳过最初的一秒
                    {
                        CurrentZeroList.Add(DaqCurrentData.Average());
                        TorqueZeroList.Add(DaqTorqueData.Average());
                        PressureZeroList.Add(DaqPressureData.Average());
                        DistanceZeroList.Add(DaqDistanceData.Average());

                        SafeLogError("零位计算中...  "+ ZeroCounter.ToString());
                    }

                    if (ZeroCounter == 15)
                    {
                        ParaNameToZeroValue["EMB1_current"] = CurrentZeroList.Average();
                        ParaNameToZeroValue["EMB1_torque"] = TorqueZeroList.Average();
                        ParaNameToZeroValue["EMB1_valveBar"] = PressureZeroList.Average();
                        ParaNameToZeroValue["EMB1_distance"] = DistanceZeroList.Average();
                        SafeLogError("零位计算完成！" );

                        string SaveMsg = ClsXmlOperation.UpdateZeroDriftInXml(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", ParaNameToZeroValue);

                        if (SaveMsg.IndexOf("OK") < 0)
                        {
                            // MessageBox.Show(SaveMsg);
                            // return;

                            SafeLogError("保存零位计算结果出错："+SaveMsg);

                        }





                    }

                }

            }
            catch (Exception ex)
            {

                SafeLogError($"更新曲线显示出错:  {ex.Message} ");

                // ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "更新曲线显示出错: " + ex.Message, "曲线显示");
                // 记录异常
            }
        }


        public void UpdateDataGridView(double[] DaqCurrentData, double[] DaqTorqueData, double[] DaqPressureData,double[] DaqDistanceData)
        {

            if (dgvRealData == null) return;
            if (dgvRealData.InvokeRequired)
            {
                dgvRealData.Invoke(new Action<double[], double[], double[], double[]>(UpdateDataGridView), DaqCurrentData, DaqPressureData, DaqDistanceData);
                return;
            }

            try
            {
                // 计算最大值
                double maxCurrentDaq = DaqCurrentData.Length > 0 ? DaqCurrentData.Max() : 0.0;
                double minCurrentDaq = DaqCurrentData.Length > 0 ? DaqCurrentData.Min() : 0.0;

                double maxTorqueDaq = DaqTorqueData.Length > 0 ? DaqTorqueData.Max() : 0.0;
                double minTorqueDaq = DaqTorqueData.Length > 0 ? DaqTorqueData.Min() : 0.0;

                double maxPressureDaq = DaqPressureData.Length > 0 ? DaqPressureData.Max() : 0.0;
                double minPressureDaq = DaqPressureData.Length > 0 ? DaqPressureData.Min() : 0.0;

                double maxDistanceDaq = DaqDistanceData.Length > 0 ? DaqDistanceData.Max() : 0.0;
                double minDistanceDaq = DaqDistanceData.Length > 0 ? DaqDistanceData.Min() : 0.0;



                // 初始化列（首次运行时）
                if (dgvRealData.Columns.Count == 0)
                {
                    dgvRealData.Columns.Add(new DataGridViewCheckBoxColumn()
                    {
                        Name = "colCheckBox",
                        HeaderText = "选择",
                        Width = 150  // CheckBox列稍窄
                    });

                    var paramColumn = new DataGridViewTextBoxColumn()
                    {
                        Name = "colParameter",
                        HeaderText = "参数",
                        Width = 200
                    };

                    var maxValueColumn = new DataGridViewTextBoxColumn()
                    {
                        Name = "colMaxValue",
                        HeaderText = "最大值",
                        Width = 200,
                        DefaultCellStyle = new DataGridViewCellStyle()
                        {
                            Format = "F3"  // 统一数字格式
                        }
                    };

                    var minValueColumn = new DataGridViewTextBoxColumn()
                    {
                        Name = "colMinValue",
                        HeaderText = "最小值",
                        Width = 200,
                        DefaultCellStyle = new DataGridViewCellStyle()
                        {
                            Format = "F3"  // 统一数字格式
                        }
                    };


                    dgvRealData.Columns.AddRange(paramColumn, maxValueColumn, minValueColumn);
                }
                // 更新或添加行
                UpdateDataRow("DAQ_Current", maxCurrentDaq, minCurrentDaq);
                UpdateDataRow("DAQ_Torque", maxTorqueDaq, minTorqueDaq);
                UpdateDataRow("DAQ_Pressure", maxPressureDaq, minPressureDaq);
                UpdateDataRow("DAQ_Distance", maxDistanceDaq, minDistanceDaq);
            }
            catch (Exception ex)
            {
            
                SafeLogError($"更新数据表格出错:  {ex.Message} ");

            }

        }
        private void UpdateDataRow(string parameter, object maxValue, object minValue)
        {
            // 查找现有行
            foreach (DataGridViewRow row in dgvRealData.Rows)
            {
                if (row.Cells["colParameter"].Value?.ToString() == parameter)
                {
                    row.Cells["colMaxValue"].Value = maxValue;
                    row.Cells["colMinValue"].Value = minValue;
                    return;
                }
            }
            // 添加新行
            int idx = dgvRealData.Rows.Add(
                true,       // CheckBox初始状态
                parameter,   // 参数列
                maxValue,        // 值列
                minValue
            );

        }



        private void ToggleCurveVisibility(string paramName, bool isVisible)
        {
            if (zedGraphDAQCalibrate.InvokeRequired)
            {
                zedGraphDAQCalibrate.Invoke(new Action<string, bool>(ToggleCurveVisibility), paramName, isVisible);
                return;
            }

            lock (graphLock)
            {
                try
                {
                    var pane = zedGraphDAQCalibrate.GraphPane;
                    if (curveDictionary.TryGetValue(paramName, out var curve))
                    {
                        curve.IsVisible = isVisible;
                    }


                    zedGraphDAQCalibrate.AxisChange();
                    zedGraphDAQCalibrate.Invalidate();
                }
                catch (Exception ex)
                {
                    SafeLogError($"选择参数更新曲线出错:  {ex.Message} ");
                    
                }
            }
        }


        private void BtnZeroCalibrate_Click(object sender, EventArgs e)
        {
            if (IsRunning)
            {
                return;
            }
           IsCalcZero = true;            
            ZeroCounter = 0;

            DaqDeltTime = 1.0 / ClsGlobal.DaqFrequency* (double)ClsGlobal.MedianLens;

            ClsGlobal.SamplesPerChannel = (int)(ClsGlobal.DaqFrequency / 1000.0 * TimerCalibrate.Interval);

            CurrentZeroList.Clear();
            TorqueZeroList.Clear();
            PressureZeroList.Clear();
            DistanceZeroList.Clear();

            listDaqPressure.Clear();
            listDaqCurrent.Clear();
            listDaqTorque.Clear();


            ParaNameToZeroValue["EMB1_current"] = 0.0;
            ParaNameToZeroValue["EMB1_torque"] = 0.0;
            ParaNameToZeroValue["EMB1_valveBar"] = 0.0;
            ParaNameToZeroValue["EMB1_distance"] = 0.0;

            zedGraphDAQCalibrate.GraphPane.XAxis.Scale.Max =  ClsGlobal.XDuration;
            zedGraphDAQCalibrate.GraphPane.XAxis.Scale.Min = 0;


            zedGraphDAQCalibrate.AxisChange();
            zedGraphDAQCalibrate.Invalidate();


            TimerCalibrate.Enabled = true;
            IsRunning = true;
            DaqCurrentTimeOffset = 0.0;

            DaqAiCurrentDispData.Clear();
            DaqAiTorqueDispData.Clear();
            DaqAiPressureDispData.Clear();
            DaqAiDistanceDispData.Clear();

            Dev1StartDaqAITask();

            SafeLogError("开始采集数据！");
        }



        private void BtnStopCalibrate_Click(object sender, EventArgs e)
        {
            TimerCalibrate.Enabled = false;
            IsRunning = false;
         
            Dev1StopTask();
            SafeLogError("停止采集数据！");

          

        }

        private void dgvRealData_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex == 0 && e.RowIndex >= 0) // CheckBox列为第一列
            {
                var grid = (DataGridView)sender;
                var cell = (DataGridViewCheckBoxCell)grid.Rows[e.RowIndex].Cells[0];
                cell.Value = cell.Value == null || !(bool)cell.Value; // 切换状态

                var paramName = grid.Rows[e.RowIndex].Cells["colParameter"].Value.ToString();
                ToggleCurveVisibility(paramName, (bool)cell.Value);

                grid.EndEdit(); // 立即提交更改
            }
        }

        private void FrmDAQCalibrate_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (IsRunning)
            {
                MessageBox.Show("请停止校准！");
                e.Cancel = true;
            }
        }
    }
}
