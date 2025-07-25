using DataOperation;
using MtEmbTest;
using NationalInstruments.DataInfrastructure;
using System;
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
using System.Xml.Serialization;
using ZedGraph;

namespace MTEmbTest
{
    public partial class FrmPlayBack: Form
    {
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        private string selectedPath = "";
        private FileInfo[] allFiles;

        private const int StatLogRecordLens = 77;
        private TestConfig testConfig;

        private LineItem curveForce;
        private PointPairList listForce;

        private LineItem curveDaqCurrent;
        private PointPairList listDaqCurrent;

        private LineItem curveDaqTorque;
        private PointPairList listDaqTorque;

        private LineItem curveCanCurrent;
        private PointPairList listCanCurrent;

        private string ExportFile = "";
        BackgroundWorker bgwA;
      
     
        private double[] CanCurrent;
        private double[] CanForce;
        private double[] DaqCurrent;
        private double[] DaqTorque;
        private int[] BrakeNo;
        private DateTime[] SourceTime;
        private double[] RelTime;


        public FrmPlayBack()
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
                pane.XAxis.Title.Text = "BrakeNo";
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


              //  zedGraphControlHistory.GraphPane.XAxis.Scale.Max = ClsGlobal.XDuration;
              //  zedGraphControlHistory.GraphPane.XAxis.Scale.Min = 0.0;


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
               
            }
        }

        private void FrmPlayBack_Load(object sender, EventArgs e)
        {
            InitializeCurve();

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

                for (int i = 0; i < CanForce.Length; i++)
                {
                    listForce.Add(BrakeNo[i], CanForce[i]);
                    listCanCurrent.Add(BrakeNo[i], CanCurrent[i]);
                    listDaqTorque.Add(BrakeNo[i], DaqTorque[i]);
                    listDaqCurrent.Add(BrakeNo[i], DaqCurrent[i]);

                    RelTime[i] = SourceTime[i].Subtract(SourceTime[0]).TotalSeconds;



                }

                zedGraphControlHistory.AxisChange();
                zedGraphControlHistory.Invalidate();


                RtbTestInfo.Clear();
                RtbTestInfo.AppendText("试验名称: " + testConfig.TestName + "\n");
                RtbTestInfo.AppendText("试验阶段: " + testConfig.TestEnvir + "\n");
                RtbTestInfo.AppendText("试验周期: " + testConfig.TestSpan.ToString("f2") + "S\n");
                RtbTestInfo.AppendText("试验次数: " + testConfig.TestTarget + "\n");
       
                RtbTestInfo.AppendText("当前范围: <" + BrakeNo[0].ToString() + "," + BrakeNo[BrakeNo.Length - 1].ToString() + ">\n");

                ProgressShow.Visible = false;
                Application.DoEvents();



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
                    SourceTime = new DateTime[Frames];
                    BrakeNo = new int[Frames];
                    CanCurrent = new double[Frames];
                    DaqTorque = new double[Frames];
                    DaqCurrent = new double[Frames];
                    RelTime = new double[Frames];

                    for (int i = 0; i < Frames; i++)   //测试了一整，还是这个最快
                    {
                        BrakeNo[i] = sr.ReadInt32();
                        SourceTime[i] = DateTime.FromFileTime(sr.ReadInt64());
                        CanForce[i] = sr.ReadDouble();
                        CanCurrent[i] = sr.ReadDouble();
                        DaqTorque[i] = sr.ReadDouble();
                        DaqCurrent[i] = sr.ReadDouble();

                        sr.ReadBytes(StatLogRecordLens - 44);     //都剩余的字节

                       
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



        private void BtnFindFile_Click(object sender, EventArgs e)
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
                        FileInfo[] SelectPart = allFiles.Where(file => FilterCondition(file, "Stat", "bin"))
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



                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"错误: {ex.Message}");
                    }
                }
            }

        }


        private void ShowOrHideCurve()
        {

            curveForce.IsVisible = ChkForce.Checked;
            zedGraphControlHistory.GraphPane.YAxis.IsVisible = ChkForce.Checked;

            curveDaqCurrent.IsVisible = ChkDaqCurrent.Checked;
            zedGraphControlHistory.GraphPane.Y2Axis.IsVisible = ChkDaqCurrent.Checked;

            curveDaqTorque.IsVisible = ChkDaqTorque.Checked;
            zedGraphControlHistory.GraphPane.YAxisList[1].IsVisible = ChkDaqTorque.Checked;


            curveCanCurrent.IsVisible = ChkCurrent.Checked;
            zedGraphControlHistory.GraphPane.Y2AxisList[1].IsVisible = ChkCurrent.Checked;

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
            if (SourceTime == null)
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


                    ExportData(SourceTime, RelTime, BrakeNo, CanForce, CanCurrent, DaqCurrent, DaqTorque, saveFileDialog.FileName);

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

        private void LbFileList_DoubleClick(object sender, EventArgs e)
        {
            try
            {
                string SafeFile = LbFileList.SelectedItem.ToString();
                string CurFileName = selectedPath + "\\" + SafeFile;
                ExportFile = CurFileName.Replace(".bin", ".csv");

                CanForce = null;
                CanCurrent = null;
                DaqTorque = null;
                DaqCurrent = null;
                SourceTime = null;
                BrakeNo = null;
                RelTime = null;






                listForce.Clear();
                listDaqCurrent.Clear();
                listDaqTorque.Clear();
                listCanCurrent.Clear();
                zedGraphControlHistory.AxisChange();
                zedGraphControlHistory.Invalidate();

                bgwA.RunWorkerAsync(CurFileName);
            }
            catch(Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

   private void ExportData(
   DateTime[] DaqTime,
   double[] RelTime,
   int[] BrakeNo,
   double[] CanForce,
   double[] CanCurrent,
   double[] DAQCurrent,
   double[] DAQTorque,
   string ExportFileName)
        {

            int Len = BrakeNo.Length;
            // 写入CSV文件
            using (StreamWriter writer = new StreamWriter(ExportFileName, false, Encoding.UTF8))
            {
                // 写入标题行
                writer.WriteLine("TimeStamp,RelTime,BrakeNo,CanForce,CanCurrent,DAQCurrent,DAQTorque");

                // 写入数据行
                for (int i = 0; i < Len; i++)
                {
                    writer.WriteLine(
                        $"{DaqTime[i]:yyyy-MM-dd HH:mm:ss.fff}," +
                        $"{RelTime[i]:0.000}," +
                        $"{BrakeNo[i]}," +
                        $"{CanForce[i]:0.000}," +
                        $"{CanCurrent[i]:0.000}," +
                        $"{DAQCurrent[i]:0.000}," +
                        $"{DAQTorque[i]:0.000}");
                }
            }
        }




    }
}
