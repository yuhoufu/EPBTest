using DataOperation;
using MtEmbTest;
using NationalInstruments.DAQmx;
using NationalInstruments.DataInfrastructure;
using Sunny.UI.Win32;
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
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using System.Xml.Serialization;
using ZedGraph;

namespace MTEmbTest
{
    public partial class FrmRawPlayBack: Form
    {
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();



        private string selectedPath = "";
        private FileInfo[] allFiles;

        private double XAxisMin = 0.0;
        private double XAxisMax = 0.0;
        private TestConfig testConfig;
        private const int StatLogRecordLens = 76;

        private LineItem curveForce;
        private PointPairList listForce;
        private LineItem curveCanCurrent;
        private PointPairList listCanCurrent;

        private LineItem curveDaqCurrent;
        private PointPairList listDaqCurrent;

        private LineItem curveDaqTorque;
        private PointPairList listDaqTorque;

        private string ExportFile = "";
        BackgroundWorker bgwA;
      
      
        private double[] CanForce;
        private double[] CanCurrent;
        private double[] CanRelTime;
        private DateTime[] CanTime;
        private int[] BrakeNo;


        private double[] DaqCurrent;
        private double[] DaqTorque;
        private DateTime[] DaqSourceTime;
        private int[] DaqBrakeNo;

        private double[] filterCurrent;
        private double[] filterTorque;
        private int[] filterDaqBrakeNo;
        private double[] filterDaqRelTime;
        private DateTime[] filterDaqTime;

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanForceScale = new ConcurrentDictionary<int, double>();
        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanForceOffset = new ConcurrentDictionary<int, double>();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanCurrentScale = new ConcurrentDictionary<int, double>();
        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanCurrentOffset = new ConcurrentDictionary<int, double>();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanTorqueScale = new ConcurrentDictionary<int, double>();
        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanTorqueOffset = new ConcurrentDictionary<int, double>();


        private static ConcurrentDictionary<string, double> EMBNameToRecvCanForceScale = new ConcurrentDictionary<string, double>();
        private static ConcurrentDictionary<string, double> EMBNameToRecvCanForceOffset = new ConcurrentDictionary<string, double>();

        private static ConcurrentDictionary<string, double> EMBNameToRecvCanCurrentScale = new ConcurrentDictionary<string, double>();
        private static ConcurrentDictionary<string, double> EMBNameToRecvCanCurrentOffset = new ConcurrentDictionary<string, double>();

        private static ConcurrentDictionary<string, double> EMBNameToRecvCanTorqueScale = new ConcurrentDictionary<string, double>();
        private static ConcurrentDictionary<string, double> EMBNameToRecvCanTorqueOffset = new ConcurrentDictionary<string, double>();


        private ConcurrentDictionary<string, double> ParaNameToScale = new ConcurrentDictionary<string, double>();
        private ConcurrentDictionary<string, double> ParaNameToOffset = new ConcurrentDictionary<string, double>();
        private ConcurrentDictionary<string, double> ParaNameToZeroValue = new ConcurrentDictionary<string, double>();

     
        private static ConcurrentDictionary<string, uint> DirectionToRecvFrame = new ConcurrentDictionary<string, uint>();
        private static ConcurrentDictionary<string, string> EMBToDirection = new ConcurrentDictionary<string, string>();
        private static ConcurrentDictionary<int, uint> EMBHandlerToRecvFrame = new ConcurrentDictionary<int, uint>();
















        public FrmRawPlayBack()
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
                int fontSize = 8;

                // 保留原有初始化代码
                GraphPane pane = zedGraphControlHistory.GraphPane;
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


                var torqueYAxis = new YAxis("");
                pane.YAxisList.Add(torqueYAxis);
                torqueYAxis.IsVisible = true;
                torqueYAxis.Title.FontSpec.FontColor = Color.Orange;
                torqueYAxis.Color = Color.Orange;
                torqueYAxis.Scale.FontSpec.FontColor = Color.Orange;
                torqueYAxis.Title.FontSpec.Size = fontSize;
                torqueYAxis.Scale.FontSpec.Size = fontSize;
                torqueYAxis.MajorGrid.IsVisible = false;
                torqueYAxis.MajorGrid.IsZeroLine = false;


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


                listDaqTorque = new PointPairList();
                curveDaqTorque = pane.AddCurve("DAQ_Torque(Nm)", listDaqTorque, Color.Orange, SymbolType.None);
                curveDaqTorque.Line.Width = 2;
                curveDaqTorque.YAxisIndex = pane.YAxisList.Count - 1;
                curveDaqTorque.IsY2Axis = false; // 


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




















                zedGraphControlHistory.GraphPane.XAxis.Scale.Max = ClsGlobal.XDuration;
                zedGraphControlHistory.GraphPane.XAxis.Scale.Min = 0.0;


                zedGraphControlHistory.GraphPane.XAxis.Scale.MagAuto = false;
                zedGraphControlHistory.GraphPane.XAxis.Scale.FormatAuto = false;
                zedGraphControlHistory.GraphPane.YAxis.Scale.MagAuto = false;
                zedGraphControlHistory.GraphPane.YAxis.Scale.FormatAuto = false;

                zedGraphControlHistory.GraphPane.Y2Axis.Scale.MagAuto = false;
                zedGraphControlHistory.GraphPane.Y2Axis.Scale.FormatAuto = false;

                torqueYAxis.Scale.MagAuto = false;
                torqueYAxis.Scale.FormatAuto = false;

                CanCurrentYAxis.Scale.MagAuto = false;
                CanCurrentYAxis.Scale.FormatAuto = false;

                zedGraphControlHistory.AxisChange();
                zedGraphControlHistory.Invalidate();
                zedGraphControlHistory.Refresh();
            }

            catch (Exception ex)
            {
                MessageBox.Show("初始化曲线显示失败！" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
               // ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "初始化曲线显示失败！" + ex.Message, "初始化");

            }
        }



        public void LoadCanDbc()
        {

            uint MsgID = EMBHandlerToRecvFrame[0];
            

                double SendFactor1 = 0.0;
                double SendOffset1 = 0.0;
                string DbcMsg = DbcParser.TryGetFactorOffset(ClsGlobal.Dbc, MsgID, "actClampForce", out SendFactor1, out SendOffset1);
                if (DbcMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(DbcMsg);
                    return;
                }
                EMBHandlerToRecvCanForceScale[0] = SendFactor1;
                EMBHandlerToRecvCanForceOffset[0] = SendOffset1;

                string EmbName = "EMB1";

                EMBNameToRecvCanForceScale[EmbName] = SendFactor1;
                EMBNameToRecvCanForceOffset[EmbName] = SendOffset1;
          


           
                double SendFactor2 = 0.0;
                double SendOffset2 = 0.0;
                DbcMsg = DbcParser.TryGetFactorOffset(ClsGlobal.Dbc, MsgID, "dcCurrent", out SendFactor2, out SendOffset2);
                if (DbcMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(DbcMsg);
                    return;
                }
                EMBHandlerToRecvCanCurrentScale[0] = SendFactor2;
                EMBHandlerToRecvCanCurrentOffset[0] = SendOffset2;

               

                EMBNameToRecvCanCurrentScale[EmbName] = SendFactor2;
                EMBNameToRecvCanCurrentOffset[EmbName] = SendOffset2;


            

           
                double SendFactor3 = 0.0;
                double SendOffset3 = 0.0;
                DbcMsg = DbcParser.TryGetFactorOffset(ClsGlobal.Dbc, MsgID, "actTorque", out SendFactor3, out SendOffset3);
                if (DbcMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(DbcMsg);
                    return;
                }
                EMBHandlerToRecvCanTorqueScale[0] = SendFactor3;
                EMBHandlerToRecvCanTorqueOffset[0] = SendOffset3;

              

                EMBNameToRecvCanTorqueScale[EmbName] = SendFactor3;
                EMBNameToRecvCanTorqueOffset[EmbName] = SendOffset3;

            


        }


        private void FrmPlayBack_Load(object sender, EventArgs e)
        {
            InitializeCurve();

            MakeDirectionMapping();
            bgwA = new BackgroundWorker();
            bgwA.WorkerReportsProgress = true;
            bgwA.DoWork += bgwA_DoWork;

            bgwA.RunWorkerCompleted += bgwA_Completed;


        }


        private void MakeDirectionMapping()
        {
            DirectionToRecvFrame.Clear();
            DirectionToRecvFrame["FL"] = (uint)System.Convert.ToInt32(ClsGlobal.FL_Recv, 16);
            DirectionToRecvFrame["FR"] = (uint)System.Convert.ToInt32(ClsGlobal.FR_Recv, 16);
            DirectionToRecvFrame["RL"] = (uint)System.Convert.ToInt32(ClsGlobal.RL_Recv, 16);
            DirectionToRecvFrame["RR"] = (uint)System.Convert.ToInt32(ClsGlobal.RR_Recv, 16);
        }

        public void LoadEMBHandlerAndFrameNo(string xmlPath)
        {
            try
            {
                // 创建DataTable结构
                DataTable dt = new DataTable();
                dt.Columns.Add("名称", typeof(string));
                dt.Columns.Add("型号", typeof(string));
                dt.Columns.Add("产品编号", typeof(string));
                dt.Columns.Add("方向", typeof(string));

                // 加载XML文件

                XDocument xdoc = XDocument.Load(xmlPath);

                // 解析XML数据
                foreach (XElement emb in xdoc.Descendants("EMB"))
                {
                    dt.Rows.Add(
                        (string)emb.Element("名称"),
                        (string)emb.Element("型号"),
                        (string)emb.Element("产品编号"),
                        (string)emb.Element("方向")
                    );
                }

                for (int i = 0; i < dt.Rows.Count; i++)
                {
                    EMBToDirection[dt.Rows[i]["名称"].ToString()] = dt.Rows[i]["方向"].ToString();
                }

                var sortedKeys = EMBToDirection.Keys.OrderBy(key => key).ToList();

                int handleNo = -1;

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



        private void bgwA_DoWork(object sender, DoWorkEventArgs e)
        {

            try
            {
                var bgworker = sender as BackgroundWorker;
                string FileName = e.Argument.ToString();
                ReadData(FileName);
            }

            catch (Exception ex)
            {

            }

        }


        private void bgwA_Completed(object sender, RunWorkerCompletedEventArgs e)
        {
            if (CanForce != null)
            {

                CanRelTime = new double[CanForce.Length];

                 for (int i = 0; i < CanForce.Length; i++)
                {
                    CanRelTime[i] = CanTime[i].Subtract(CanTime[0]).TotalSeconds;
                    listForce.Add(CanRelTime[i], CanForce[i]);
                    listCanCurrent.Add(CanRelTime[i], CanCurrent[i]);
                }


                filterCurrent = ClsDataFilter.MakeMedianFilterReducePoint(ref DaqCurrent, ClsGlobal.MedianLens);
                filterTorque = ClsDataFilter.MakeMedianFilterReducePoint(ref DaqTorque, ClsGlobal.MedianLens);


                int DaqDataLens = DaqCurrent.Length / ClsGlobal.MedianLens;
                filterDaqRelTime = new double[DaqDataLens];
                filterDaqBrakeNo = new int[DaqDataLens];
                filterDaqTime = new DateTime[DaqDataLens];

                for (int i = 0; i < DaqDataLens; i++)
                {
                    filterDaqTime[i] = DaqSourceTime[i * ClsGlobal.MedianLens];
                    filterDaqRelTime[i] = DaqSourceTime[i * ClsGlobal.MedianLens].Subtract(DaqSourceTime[0]).TotalSeconds;
                    filterDaqBrakeNo[i] = DaqBrakeNo[i * ClsGlobal.MedianLens];
                }

                //double[] daqreltime = new double[DaqSourceTime.Length];

                //for (int i = 0; i < DaqSourceTime.Length; i++)
                //{
                //    daqreltime[i]= DaqSourceTime[i].Subtract(DaqSourceTime[0]).TotalSeconds;
                //}

                    for (int i = 0; i < filterCurrent.Length; i++)
                {
                    listDaqCurrent.Add(filterDaqRelTime[i], filterCurrent[i]);
                    listDaqTorque.Add(filterDaqRelTime[i], filterTorque[i]);
                }


                int maxLens = (CanForce.Length > filterCurrent.Length) ? CanForce.Length : filterCurrent.Length;


                zedGraphControlHistory.GraphPane.XAxis.Scale.Max = 0.01*(double)maxLens;
                zedGraphControlHistory.GraphPane.XAxis.Scale.Min = 0.0;

                XAxisMin = 0.0;
                XAxisMax = 0.01 * (double)maxLens;


                RtbTestInfo.Clear();
                RtbTestInfo.AppendText("试验名称: " + testConfig.TestName + "\n");
                RtbTestInfo.AppendText("试验阶段: " + testConfig.TestEnvir + "\n");
                RtbTestInfo.AppendText("试验周期: " + testConfig.TestSpan.ToString("f2") + "S\n");
                RtbTestInfo.AppendText("试验次数: " + testConfig.TestTarget + "\n");
              //  RtbTestInfo.AppendText("当前范围: <" + DaqBrakeNo[0].ToString() + "," + DaqBrakeNo[DaqBrakeNo.Length - 1].ToString() + ">\n");
              RtbTestInfo.AppendText("当前范围: <" + BrakeNo[0].ToString() + "," + BrakeNo[BrakeNo.Length - 1].ToString() + ">\n");

                ProgressShow.Visible = false;
                Application.DoEvents();





                zedGraphControlHistory.AxisChange();
                zedGraphControlHistory.Invalidate();

            }

            else
            {
                MessageBox.Show("记录数据为空！");
                return;
            }






        }
        private void ReadData(string FileName)
        {
            try
            {
                using (FileStream fs = new FileStream(FileName, FileMode.Open))
                {
                    BinaryReader sr = new BinaryReader(fs);
                    int FileLens = (int)fs.Length;
                    int Frames = FileLens / StatLogRecordLens;

                    CanForce = new double[Frames];
                 
                    CanTime=new DateTime[Frames];
                    BrakeNo = new int[Frames];
                    CanCurrent = new double[Frames];
                 

                    for (int i = 0; i < Frames; i++)   //测试了一整，还是这个最快
                    {
                        BrakeNo[i] = sr.ReadInt32();
                        CanTime[i] = DateTime.FromFileTime(sr.ReadInt64());

                      byte[] Data= sr.ReadBytes(64);


                        double forceValue = 0;
                        double currentValue = 0;
                        byte faultflg = 0;
                        double torque = 0;
                        ClsBitFieldParser.ParseClampData(Data,
                        EMBHandlerToRecvCanForceScale[0],
                        EMBHandlerToRecvCanTorqueScale[0],
                        EMBHandlerToRecvCanCurrentScale[0],
                        out forceValue, out faultflg, out torque, out currentValue);

                          CanForce[i] = forceValue;
                          CanCurrent[i] = currentValue;
                    }

                    sr.Close();
                    fs.Close();
                }


                string daqFile = FileName.Replace("EMB_CAN1", "DAQ_Dev1");

                using (FileStream fs = new FileStream(daqFile, FileMode.Open))
                {
                    BinaryReader sr = new BinaryReader(fs);
                    int FileLens = (int)fs.Length;
                    int Frames = FileLens / 44;



                    DaqCurrent = new double[Frames];
                    DaqSourceTime = new DateTime[Frames];
                    DaqBrakeNo = new int[Frames];
                    DaqTorque = new double[Frames];


                    for (int i = 0; i < Frames; i++)   //测试了一整，还是这个最快
                    {
                        DaqBrakeNo[i] = sr.ReadInt32();
                        DaqSourceTime[i] = DateTime.FromFileTime(sr.ReadInt64());
                    

                        DaqCurrent[i] = (sr.ReadDouble() - ParaNameToZeroValue["EMB1_current"]) * ParaNameToScale["EMB1_current"] + ParaNameToOffset["EMB1_current"];
                        DaqTorque[i] = (sr.ReadDouble() - ParaNameToZeroValue["EMB1_torque"]) * ParaNameToScale["EMB1_torque"] + ParaNameToOffset["EMB1_torque"];




                        sr.ReadDouble();
                        sr.ReadDouble();
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
            zedGraphControlHistory.GraphPane.YAxisList[0].IsVisible = ChkForce.Checked;

            curveCanCurrent.IsVisible = ChkCurrent.Checked;
            zedGraphControlHistory.GraphPane.Y2AxisList[1].IsVisible = ChkCurrent.Checked;


            curveDaqCurrent.IsVisible = ChkDaqCurrent.Checked;
            zedGraphControlHistory.GraphPane.Y2AxisList[0].IsVisible = ChkDaqCurrent.Checked;


            curveDaqTorque.IsVisible = ChkDaqTorque.Checked;
            zedGraphControlHistory.GraphPane.YAxisList[1].IsVisible = ChkDaqTorque.Checked;



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

        private void ChkDaqCurrent_CheckedChanged_1(object sender, EventArgs e)
        {
            ShowOrHideCurve();
        }

        private void ChkDaqTorque_CheckedChanged_1(object sender, EventArgs e)
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
            if (filterDaqTime == null)
            {
                MessageBox.Show("数据集为空，无法导出！");
                return;
            }



            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
            saveFileDialog.Title = "Export to CSV";
         
            saveFileDialog.FileName = ExportFile;

            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    ProgressShow.Visible = true;
                    ProgressShow.BringToFront();  // 确保在最上层
                    Application.DoEvents();


                    ExportData(filterDaqTime, filterDaqRelTime, filterDaqBrakeNo, BrakeNo, CanForce, CanCurrent, filterCurrent, filterTorque,saveFileDialog.FileName);

                    ProgressShow.Visible = false;

                    Application.DoEvents();

                    MessageBox.Show("导出完成！");

                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }


   private void ExportData(
   DateTime[] FilterDaqTime,
   double[] FilterDaqRelTime,
   int[] FilterDaqBrakeNo,
   int[] CanBrakeNo,
   double[] CanForce,
   double[] CanCurrent,
   double[] filterCurrent,
   double[] filterTorque,
   string ExportFileName)
        {
            // 验证数组长度一致性
            int baseLength = FilterDaqBrakeNo.Length;
            if (FilterDaqTime.Length != baseLength ||
                FilterDaqRelTime.Length != baseLength)
            //||
            //filterCurrent.Length != baseLength)
            {
                throw new ArgumentException("FilterDaqTime, FilterDaqRelTime, FilterDaqBrakeNo and filterCurrent arrays must have the same length");
            }

            int canLength = CanBrakeNo.Length;
            if (CanForce.Length != canLength || CanCurrent.Length != canLength)
            {
                throw new ArgumentException("CanBrakeNo, CanForce and CanCurrent arrays must have the same length");
            }

            // 创建调整后的列表
            var adjustedTime = new List<DateTime>();
            var adjustedRelTime = new List<double>();
            var adjustedDaqBrake = new List<int>();
            var adjustedCanBrake = new List<int>();
            var adjustedCanForce = new List<double>();
            var adjustedCanCurrent = new List<double>();
            var adjustedFilterCurrent = new List<double>();
            var adjustedFilterTorque = new List<double>();

            int j = 0; // CAN数据的索引
            double lastCanForce = 0.0;
            double lastCanCurrent = 0.0;
            bool hasPreviousCanValue = false;

            // 处理每一行数据
            for (int i = 0; i < baseLength; i++)
            {
                // 检查是否需要插入行
                if (j < canLength && FilterDaqBrakeNo[i] < CanBrakeNo[j])
                {
                    // 插入新行（使用上一个CAN值）
                    adjustedTime.Add(FilterDaqTime[i]);
                    adjustedRelTime.Add(FilterDaqRelTime[i]);
                    adjustedDaqBrake.Add(FilterDaqBrakeNo[i]);
                    adjustedCanBrake.Add(FilterDaqBrakeNo[i]); // 对齐到DAQ刹车号
                    adjustedFilterCurrent.Add(filterCurrent[i]);
                    adjustedFilterTorque.Add(filterTorque[i]);

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
                    continue;
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
                    adjustedFilterTorque.Add(filterTorque[i]);

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
                    adjustedFilterTorque.Add(filterTorque[i]);

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
            }

            // 写入CSV文件
            using (StreamWriter writer = new StreamWriter(ExportFileName, false, Encoding.UTF8))
            {
                // 写入标题行
                writer.WriteLine("TimeStamp,RelTime,DAQBrakeNo,CanBrakeNo,CanForce,CanCurrent,DAQCurrent,DAQTorque");

                // 写入数据行
                for (int i = 0; i < adjustedTime.Count; i++)
                {
                    writer.WriteLine(
                        $"{adjustedTime[i]:yyyy-MM-dd HH:mm:ss.fff}," +
                        $"{adjustedRelTime[i]:0.000}," +
                        $"{adjustedDaqBrake[i]}," +
                        $"{adjustedCanBrake[i]}," +
                        $"{adjustedCanForce[i]:0.000}," +
                        $"{adjustedCanCurrent[i]:0.000}," +
                        $"{adjustedFilterCurrent[i]:0.000}," +
                        $"{adjustedFilterTorque[i]:0.000}");
                }
            }
        }


        /// <summary>
        /// 沿 X 轴平移图表
        /// </summary>
        /// <param name="shift">平移量（正=右移，负=左移）</param>
        private void AbsPanXAxis(double shift)
        {
            if (zedGraphControlHistory.GraphPane == null) return;


            

            GraphPane pane = zedGraphControlHistory.GraphPane;

            // 计算新范围
            double newMin = pane.XAxis.Scale.Min + shift;
            double newMax = pane.XAxis.Scale.Max + shift;

            // 可选：检查范围是否超出数据边界
            if (newMin < XAxisMin)
            {
                newMin = XAxisMin;
            }

            if (newMax > XAxisMax)
            {
                newMax = XAxisMax;
            }

            // 应用新范围
            pane.XAxis.Scale.Min = newMin;
            pane.XAxis.Scale.Max = newMax;

            // 刷新图表
            zedGraphControlHistory.AxisChange();
            zedGraphControlHistory.Invalidate();
        }

        private void PercentPanXAxis(double shift)
        {
            if (zedGraphControlHistory.GraphPane == null) return;
            GraphPane pane = zedGraphControlHistory.GraphPane;

            double AbsValue = (pane.XAxis.Scale.Max - pane.XAxis.Scale.Min) * shift;


            // 计算新范围
            double newMin = pane.XAxis.Scale.Min + AbsValue;
            double newMax = pane.XAxis.Scale.Max + AbsValue;

            // 可选：检查范围是否超出数据边界
            if (newMin < XAxisMin)
            {
                newMin = XAxisMin;
            }

            if (newMax > XAxisMax)
            {
                newMax = XAxisMax;
            }

            // 应用新范围
            pane.XAxis.Scale.Min = newMin;
            pane.XAxis.Scale.Max = newMax;

            // 刷新图表
            zedGraphControlHistory.AxisChange();
            zedGraphControlHistory.Invalidate();
        }


        public void AppendDoublesToFileV2(
        double value1,
        double value2,
        double value3,
        double value4,
        double value5,
        string fileName,
        string delimiter = ",",
        string format = "F3")
        {
            try
            {
                // 参数验证
                if (string.IsNullOrWhiteSpace(fileName))
                    throw new ArgumentException("文件名不能为空", nameof(fileName));

                if (string.IsNullOrEmpty(delimiter))
                    throw new ArgumentException("分隔符不能为空", nameof(delimiter));

                // 格式化数值
                string line = string.Format("{0}{1}{2}{3}{4}{5}{6}{7}{8}{9}",
                 
                    value1.ToString(format),
                    delimiter,
                    value2.ToString(format),
                     delimiter,
                    value3.ToString(format),
                     delimiter,
                    value4.ToString(format),
                     delimiter,
                    value5.ToString(format),
                    Environment.NewLine);

                // 追加写入文件（使用UTF-8编码）
                File.AppendAllText(fileName, line, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }

        }







        public void AppendDoublesToFile(
        int brakeNo,
         double value1,
         double value2,
         double value3,
         double value4,
         double value5,
         string fileName,
         string delimiter = ",",
         string format = "F3")
        {
            try
            {
                // 参数验证
                if (string.IsNullOrWhiteSpace(fileName))
                    throw new ArgumentException("文件名不能为空", nameof(fileName));

                if (string.IsNullOrEmpty(delimiter))
                    throw new ArgumentException("分隔符不能为空", nameof(delimiter));

                // 格式化数值
                string line = string.Format("{0}{1}{2}{3}{4}{5}{6}{7}{8}{9}{10}{11}",
                    brakeNo.ToString(),
                     delimiter,
                    value1.ToString(format),
                    delimiter,
                    value2.ToString(format),
                     delimiter,
                    value3.ToString(format),
                     delimiter,
                    value4.ToString(format),
                     delimiter,
                    value5.ToString(format),
                    Environment.NewLine);

                // 追加写入文件（使用UTF-8编码）
                File.AppendAllText(fileName, line, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
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


        private bool FilterCondition(FileInfo file, string nameFilter, string extensionFilter)
        {
            // 1. 文件名过滤
            bool nameValid = string.IsNullOrWhiteSpace(nameFilter) ||
                            file.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0;

            // 2. 扩展名过滤
            bool extensionValid = string.IsNullOrWhiteSpace(extensionFilter) ||
                                 extensionFilter.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                                     .Any(ext =>
                                         file.Extension.Equals(
                                             ext.StartsWith(".") ? ext : "." + ext,
                                             StringComparison.OrdinalIgnoreCase));

            // 3. 组合条件
            return nameValid && extensionValid;
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
                RtbTestInfo.AppendText("试验阶段: " + testConfig.TestEnvir + "\n");
                RtbTestInfo.AppendText("试验周期: " + testConfig.TestSpan.ToString("f2") + "S\n");
                RtbTestInfo.AppendText("试验次数: " + testConfig.TestTarget + "\n");

            }

            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }

        }
        private void BtnChoiseFolder_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "选择文件夹";
                folderDialog.ShowNewFolderButton = false;

                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    selectedPath = folderDialog.SelectedPath;

                    try
                    {
                       



                        // 获取文件夹中所有文件
                        allFiles = new DirectoryInfo(selectedPath).GetFiles("*.*", SearchOption.TopDirectoryOnly);
                        FileInfo[] SelectPart = allFiles.Where(file => FilterCondition(file, "CAN" , "bin"))
                            .OrderBy(file => file.CreationTime) // 按创建时间升序
                            .ToArray();

                        // 提取纯文件名（不含路径）
                        string[] fileNames = SelectPart
                            .Select(file => file.Name)
                            .ToArray();

                        LbFileList.Items.Clear();
                        LbFileList.Items.AddRange(fileNames);

                        string xmlPath = Path.Combine(selectedPath, @"TestConfig.xml");
                        LoadTestConfigFromXml(xmlPath);

                        xmlPath = Path.Combine(selectedPath, @"EMBControl.XML");
                        LoadEMBHandlerAndFrameNo(xmlPath);


                        LoadCanDbc();


                        string ReadMsg = ClsXmlOperation.GetDaqScaleMapping(selectedPath + @"\AIConfig.xml", "Dev1", out ParaNameToScale);
                        if (ReadMsg.IndexOf("OK") < 0)
                        {
                            MessageBox.Show(ReadMsg);
                            return;
                        }

                        ReadMsg = ClsXmlOperation.GetDaqOffsetMapping(selectedPath + @"\AIConfig.xml", "Dev1", out ParaNameToOffset);
                        if (ReadMsg.IndexOf("OK") < 0)
                        {
                            MessageBox.Show(ReadMsg);
                            return;
                        }

                        ReadMsg = ClsXmlOperation.GetDaqZeroValueMapping(selectedPath + @"\AIConfig.xml", "Dev1", out ParaNameToZeroValue);
                        if (ReadMsg.IndexOf("OK") < 0)
                        {
                            MessageBox.Show(ReadMsg);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"错误: {ex.Message}");
                    }
                }
            }
        }

        private void LbFileList_DoubleClick(object sender, EventArgs e)
        {
            try
            {

                string  SafeFile = LbFileList.SelectedItem.ToString();
                string CurFileName = selectedPath + "\\" + SafeFile;
                ExportFile = CurFileName.Replace(".bin", ".csv");

                CanForce = null;
                CanCurrent = null;
                CanRelTime = null;
                BrakeNo = null;

                DaqCurrent = null;
                DaqTorque = null;
                DaqSourceTime = null;
                DaqBrakeNo = null;

                XAxisMin = 0.0;
                XAxisMax = 0.0;

             //   testConfig = new TestConfig();

                listDaqCurrent.Clear();
                listDaqTorque.Clear();
                listForce.Clear();
                listCanCurrent.Clear();
                zedGraphControlHistory.AxisChange();
                zedGraphControlHistory.Invalidate();


                ProgressShow.Location = new Point(
           zedGraphControlHistory.Left + (zedGraphControlHistory.Width - ProgressShow.Width) / 2,
           zedGraphControlHistory.Top + (zedGraphControlHistory.Height - ProgressShow.Height) / 2
       );

                ProgressShow.Visible = true;
                ProgressShow.BringToFront();  // 确保在最上层
                Application.DoEvents();




                bgwA.RunWorkerAsync(CurFileName);
            }

            catch(Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

       
    }
}
