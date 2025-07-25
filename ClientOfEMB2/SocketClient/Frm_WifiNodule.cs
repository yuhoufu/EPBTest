
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Net.Sockets;
using System.Threading;
using System.Net;
using CustomTcpClient;
using System.Configuration;

namespace SocketClient
{
    public partial class Frm_WifiNodule : Form
    {
        private int counter1 = 0;
        private int counter2 = 0;
        private int counter3 = 0;
        private int counter4 = 0;
        private int counter5 = 0;

        private string ServerIpAdress;
        private string ServerPort;
      

        private double lng = 12119.2968533;
        private double lat = 3107.8067498;
        private double high = 59.4151;
        private double speed = 0.00;
     //   private int speedDirct = 1;


        private AsyncTcpClient[] Gprmc_Client =new AsyncTcpClient[10];

        private float currentValue = 0;
        private int phase = 0; // 0:递增, 1:递减
        private static readonly byte[] OriginalByteSend = new byte[] { 
    0xEB, 0x90, 0x03, 0x28, 0x02, 0x54, 0x94, 0xD0, 
    0x02, 0x51, 0x4F, 0xF8, 0x02, 0x58, 0x2C, 0x1C, 0x9C 
};


       
        public Frm_WifiNodule()
        {
            InitializeComponent();
          
        }

        private static IPAddress GetIP(string import)
        {
            IPHostEntry IPHost = Dns.GetHostEntry(import);
            return IPHost.AddressList[0];
        }




        //连接服务器端
        private void btnConn_Click(object sender, EventArgs e)
        {
            
            try
            {


                SaveOneItem("ConnIP", tbxIP.Text);
                SaveOneItem("ConnPort", tbxPort.Text);

                for (int i = 0; i < 1; i++)
                {
                    Gprmc_Client[i] = new AsyncTcpClient(i+ 1, "ComplexClient"+i.ToString(), 4096);

                    string ConnMsg=Gprmc_Client[i].ConnectToServer(tbxIP.Text, int.Parse(tbxPort.Text));

                    if (ConnMsg.IndexOf("OK") < 0)
                    {
                        RtbRunMsg.Invoke(new SetTextCallback(SetRunText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff>  ") + i.ToString() + " 连接服务器失败  " + ConnMsg);

                    }
                    else
                    {
                        RtbRunMsg.Invoke(new SetTextCallback(SetRunText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff>  ") + i.ToString() + " 连接服务器成功  ");
                    }


                    Gprmc_Client[i].DataReceived += delegate(object sender1, RecvEventArg e1)
                    {
                        //创建Cors连接的函数组
                        Cors_Client_DataReceivedByIndex(sender1, e1, i);
                    };






                }

              


              
               
            }
            catch (Exception ee)
            {
                
                RtbRunMsg.Invoke(new SetTextCallback(SetRunText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff>  ") + "连接服务器失败 " + ee.Message);


            }
            


        }
        //断开连接
        private void btnClose_Click(object sender, EventArgs e)
        {
            for (int i = 0; i <1; i++)
            {
                Gprmc_Client[i].CloseSocket();
            }
            btnConn.Enabled = true ;
        }
        //发送消息

        private void Cors_Client_DataReceivedByIndex(object sender, RecvEventArg e, int CorsIndex)
        {

            var whichSock = (AsyncTcpClient)sender;



            if (e.RecvBuffer != null)
            {

                byte[] SendToRover = new byte[e.RecvBuffer.Length];
                Array.Copy(e.RecvBuffer, SendToRover, e.RecvBuffer.Length);

                RtbRecvMsg.Invoke(new SetTextCallback(SetRecvText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff>  ") + "Client"+CorsIndex.ToString()+"    "+ByteToString(SendToRover));


            }
           

        }






        private static string ByteToString(byte[] inputBytes)
        {
            try
            {
                StringBuilder temp = new StringBuilder(2048);
                foreach (byte tempByte in inputBytes)
                {
                    temp.Append(tempByte > 15 ?
                    Convert.ToString(tempByte, 16) : '0' + Convert.ToString(tempByte, 16));
                    temp.Append(' ');
                }
                return temp.ToString().ToUpper();
            }
            catch (Exception ex)
            {
                return ("Error: " + ex.Message).Substring(0, 20);
            }
        }

      

        private string HexStrTobyte(string hexString, out byte[] returnBytes)
        {
            try
            {
                hexString = hexString.Replace(" ", "");
                if ((hexString.Length % 2) != 0)
                    hexString += " ";
                returnBytes = new byte[hexString.Length / 2];
                for (int i = 0; i < returnBytes.Length; i++)
                    returnBytes[i] = Convert.ToByte(hexString.Substring(i * 2, 2).Trim(), 16);
                return "OK";
            }


            catch (Exception ex)
            {
                returnBytes = BitConverter.GetBytes(0);
                return ex.Message.ToString();
            }
        }


        private void timer1_Tick(object sender, EventArgs e)
        {
            try
            {

                counter1++;
                string Header = "";

                // 计算当前数值（0-14000-0循环）
                const int maxValue = 14000;
                const float step = maxValue / 15f; // 每次变化量

                if (phase == 0)
                {
                    currentValue += step;
                    if (currentValue >= maxValue)
                    {
                        currentValue = maxValue;
                        phase = 1;
                    }
                }
                else
                {
                    currentValue -= step;
                    if (currentValue <= 0)
                    {
                        currentValue = 0;
                        phase = 0;
                    }
                }

                // 生成字节数组（大端序）
                short value = (short)Math.Round(currentValue);
                byte[] valueBytes = BitConverter.GetBytes(value);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(valueBytes);
                }

                // 克隆原始数据并修改序号字段
                byte[] byteSend = (byte[])OriginalByteSend.Clone();
              


                byteSend[2] = (byte)(valueBytes[1] - 0x12);
                byteSend[3] = (byte)(valueBytes[0] - 0x12);

                byteSend[4] = valueBytes[1]; // 第3个字节（索引2）
                byteSend[5] = valueBytes[0]; // 第4个字节（索引3）


                byteSend[6] = valueBytes[1]; // 第3个字节（索引2）
                byteSend[7] = valueBytes[0]; // 第4个字节（索引3）


                byteSend[8] = (byte)(valueBytes[1] - 0x02);
                byteSend[9] = (byte)(valueBytes[0] - 0x02); 



                
                
                
                
              //  counter1++;
             //   string Header = "";

             //   byte[] byteSend = { 0xEB, 0x90, 0x03, 0x28, 0x02, 0x54, 0x94, 0xD0, 0x02, 0x51, 0x4F, 0xF8, 0x02, 0x58, 0x2C, 0x1C, 0x9C };

                string SendMsg = Gprmc_Client[0].SendBinaryToServer(Header, byteSend);

                if (SendMsg.IndexOf("OK") < 0)
                {
                    btnConn.Enabled = true;
                    RtbSendMsg.Invoke(new SetTextCallback(SetSendText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff>  ") + counter1.ToString("D8") + "   " + Header + ByteToString(byteSend) + "  " + SendMsg);
                }
                else
                {
                    RtbSendMsg.Invoke(new SetTextCallback(SetSendText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff>  ") + counter1.ToString("D8") + "   " + Header + ByteToString(byteSend) + "  " + "  OK");
                    btnConn.Enabled = false;
                }
            }

            catch (Exception ex)
            {
                string msg=ex.Message;
            }
          
        }

      



        private void btnBegin_Click(object sender, EventArgs e)
        {
            timer1.Enabled = true;
       
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            timer1.Enabled = false;
          
        }


        delegate void SetTextCallback(string text);
       
        private void SetSendText(string text)
        {            
            RtbSendMsg.AppendText(text + "\n");
            RtbSendMsg.ScrollToCaret();
        }

        private void SetRunText(string text)
        {
            RtbRunMsg.AppendText(text + "\n");
            RtbRunMsg.ScrollToCaret();
        }

        private void SetRecvText(string text)
        {
            RtbRecvMsg.AppendText(text + "\n");
            RtbRecvMsg.ScrollToCaret();
        }
    

        private void timerClear_Tick(object sender, EventArgs e)
        {
            RtbSendMsg.Clear();
            RtbRunMsg.Clear();
            RtbRecvMsg.Clear();
        }


        public void addItem(string keyName, string keyValue)
        {
            //添加配置文件的项，键为keyName，值为keyValue
            Configuration config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);


            config.AppSettings.Settings.Add(keyName, keyValue);



            config.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("configuration");

        }
        //判断键为keyName的项是否存在：
        public bool existItem(string keyName)
        {
            //判断配置文件中是否存在键为keyName的项
            foreach (string key in ConfigurationManager.AppSettings)
            {
                if (key == keyName)
                {
                    //存在
                    return true;
                }
            }
            return false;
        }
        //获取键为keyName的项的值：
        public string valueItem(string keyName)
        {
            //返回配置文件中键为keyName的项的值
            return ConfigurationManager.AppSettings[keyName];
        }
        //修改键为keyName的项的值：
        public void modifyItem(string keyName, string newKeyValue)
        {
            //修改配置文件中键为keyName的项的值
            Configuration config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            config.AppSettings.Settings[keyName].Value = newKeyValue;
            config.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("appSettings");
        }
        //删除键为keyName的项：
        public void removeItem(string keyName)
        {
            //删除配置文件键为keyName的项
            Configuration config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            config.AppSettings.Settings.Remove(keyName);
            config.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("appSettings");
        }

        private string SetOneItem(string ItemName)
        {
            if (!existItem(ItemName))
            {
                MessageBox.Show(ItemName + " not exist！");
                return "999";
            }

            return valueItem(ItemName);
        }

        private void SaveOneItem(string ItemName, string ItemValue)
        {
            if (ItemValue.Length < 1)
            {
                MessageBox.Show("Please Input  Value  of " + ItemName + "!");
                return;
            }

            //如果输入的是空值就返回
            try
            {
                if (existItem(ItemName))
                {
                    modifyItem(ItemName, ItemValue);
                }
                else
                {
                    addItem(ItemName, ItemValue);
                }
            }
            catch (Exception ex)
            {
                ex.Message.ToString();
            }
        }

        private void Frm_Gprmc_Load(object sender, EventArgs e)
        {
          ServerIpAdress=SetOneItem("ConnIP");
          ServerPort = SetOneItem("ConnPort");

          tbxIP.Text = ServerIpAdress;
          tbxPort.Text = ServerPort;
        }


    }
}
