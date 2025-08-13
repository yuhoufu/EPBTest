using DataOperation;
using MtEmbTest;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor : Form
    {
        public FrmEpbMainMonitor()
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
                Text = @"×",
                Size = new Size(50, 50),
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
                    //ReleaseCapture();
                    //SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };
        }

        //
        // private void FrmEpbMainMonitor_Load(object sender, EventArgs e)
        // {
        //
        //     try
        //     {
        //         DaqTimeSpanMilSeconds = 1000.0 / ClsGlobal.DaqFrequency;
        //         
        //         activeWriteBuffer = bufferA;
        //         readyReadBuffer = bufferB;
        //
        //         string ReadMsg = ClsXmlOperation.ReadCanNameChannelToDictionary(System.Environment.CurrentDirectory + @"\Config\CanChannel.xml", out EmbToChannel);
        //         if (ReadMsg.IndexOf("OK") < 0)
        //         {
        //             MessageBox.Show(ReadMsg);
        //             return;
        //         }
        //
        //         if (EmbToChannel.Count < 1)
        //         {
        //             MessageBox.Show("未读取到EMB和CAN的关联关系！");
        //             return;
        //         }
        //
        //         ReadMsg = ClsXmlOperation.GetDaqAIUsedChannels(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out Dev1UsedDaqAIChannels);
        //         if (ReadMsg.IndexOf("OK") < 0)
        //         {
        //             MessageBox.Show(ReadMsg);
        //             return;
        //         }
        //
        //         if (Dev1UsedDaqAIChannels.Length < 1)
        //         {
        //             MessageBox.Show("未读取到 Dev1 DAQ AI 相关信息！");
        //             return;
        //         }
        //
        //
        //
        //         ReadMsg = ClsXmlOperation.GetDaqAIUsedChannels(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev2", out Dev2UsedDaqAIChannels);
        //         if (ReadMsg.IndexOf("OK") < 0)
        //         {
        //             MessageBox.Show(ReadMsg);
        //             return;
        //         }
        //
        //         if (Dev2UsedDaqAIChannels.Length < 1)
        //         {
        //             MessageBox.Show("未读取到 Dev2 DAQ AI 相关信息！");
        //             return;
        //         }
        //
        //
        //
        //         ReadMsg = ClsXmlOperation.GetDaqAIChannelMapping(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", Dev1UsedDaqAIChannels, out EMBToDaqCurrentChannel);
        //         if (ReadMsg.IndexOf("OK") < 0)
        //         {
        //             MessageBox.Show(ReadMsg);
        //             return;
        //         }
        //
        //         if (EMBToDaqCurrentChannel.Count < 1)
        //         {
        //             MessageBox.Show("未读取到DAQ电流和EMB控制器对应关系！");
        //             return;
        //         }
        //
        //
        //
        //
        //         ReadMsg = ClsXmlOperation.GetDaqScaleMapping(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToScale);
        //         if (ReadMsg.IndexOf("OK") < 0)
        //         {
        //             MessageBox.Show(ReadMsg);
        //             return;
        //         }
        //
        //         ReadMsg = ClsXmlOperation.GetDaqOffsetMapping(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToOffset);
        //         if (ReadMsg.IndexOf("OK") < 0)
        //         {
        //             MessageBox.Show(ReadMsg);
        //             return;
        //         }
        //
        //         ReadMsg = ClsXmlOperation.GetDaqZeroValueMapping(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToZeroValue);
        //         if (ReadMsg.IndexOf("OK") < 0)
        //         {
        //             MessageBox.Show(ReadMsg);
        //             return;
        //         }
        //
        //
        //
        //
        //
        //
        //
        //         int handleNo = -1;
        //
        //         // EmbToChannel 是无序的，要排序后再对应，此处应该有捂脸的表情包
        //
        //
        //         var sortedKeys = EmbToChannel.Keys.OrderBy(key => key).ToList();
        //
        //         foreach (var key in sortedKeys)
        //         {
        //             handleNo++;
        //             EmbNoToChannel[handleNo] = EmbToChannel[key];         //处理顺序和波道对应
        //             EmbNoToName[handleNo] = key;
        //         }
        //
        //         //给处理序号和通道号字典赋值
        //
        //
        //
        //         LoadEmbControler();
        //
        //
        //         InitializeCurve();
        //         StartListen();
        //         MakeCurveMapping();
        //         MakeDirectionMapping();
        //         LoadTestConfigFromXml();
        //         LoadEMBHandlerAndFrameNo();
        //
        //         RtbInfo.Invoke(new SetTextCallback(SetInfoText), "1. 编辑试验信息并确认");
        //         //   RtbInfo.Invoke(new SetTextCallback(SetInfoText), "2. CAN卡初始化");
        //         //   RtbInfo.Invoke(new SetTextCallback(SetInfoText), "3. 打开各个电源开关");
        //         RtbInfo.Invoke(new SetTextCallback(SetInfoText), "2. 自学习/开始试验");
        //
        //
        //         ClsDiskProc.MakeSubDir(testConfig.StoreDir);
        //
        //         string MainDrive = testConfig.StoreDir.Trim().Substring(0, 2);
        //
        //         long LastSpace = ClsDiskProc.GetHardDiskSpace(MainDrive);
        //         if (LastSpace == 0)
        //         {
        //             MessageBox.Show("指定磁盘不存在！");
        //         }
        //         if (LastSpace < 50)
        //         {
        //             MessageBox.Show("剩余磁盘空间小于" + LastSpace.ToString() + "GB");
        //         }
        //
        //
        //
        //
        //
        //         ChkEmb3.Checked = false;
        //         ChkEmb4.Checked = false;
        //         ChkEmb5.Checked = false;
        //         ChkEmb6.Checked = false;
        //
        //
        //         LoadCanDbc();
        //
        //
        //
        //
        //
        //
        //
        //     }
        //
        //     catch (Exception ex)
        //     {
        //         MessageBox.Show("初始化错误 : " + ex.Message);
        //     }
        //
        //
        //
        // }
        //
        //


    }
}
