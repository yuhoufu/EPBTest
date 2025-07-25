using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.Net;

namespace CustomTcpClient
{

    public class RecvEventArg : EventArgs
    {
        private string message;
        public string Message
        {
            get { return message; }
            set { message = value; }
        }

        private byte[] recvBuffer;
        public byte[]  RecvBuffer
        {
            get { return recvBuffer; }
            set { recvBuffer = value; }
        }

        private DateTime recvTimeStamp;
        public DateTime RecvTimeStamp
        {
            get { return recvTimeStamp; }
            set { recvTimeStamp = value; }
        }

        private int clientNo;
        public int ClientNo
        {
            get { return clientNo; }
            set { clientNo = value; }
        }

        private string clientDesc;
        public string ClientDesc
        {
            get { return clientDesc; }
            set { clientDesc = value; }
        }

        private string sendHeader;
        public string SendHeader
        {
            get { return sendHeader; }
            set { sendHeader = value; }
        }

        private DateTime sendTimeStamp;
        public DateTime SendTimeStamp
        {
            get { return sendTimeStamp; }
            set { sendTimeStamp = value; }
        }

    }



    public class AsyncTcpClient
    {
        private Socket sock ;
        private byte[] buffer ;
        public int clientNo;
        public string clientDesc;
        private string sendHeader;
        private DateTime sendTimeStamp;

        public AsyncTcpClient(int ClientNo,string ClientDesc,int RecvBufferLens)
        {
            sock = null;
            buffer = new byte[RecvBufferLens];
            clientNo = ClientNo;
            clientDesc = ClientDesc;
        }

        public string CloseSocket()
        {
            try
            {
                sock.Close();
                sock = null;
                return "OK";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public string ConnectToServer(string SeverAddress,int ServerPort)
        {
            try
            {

                sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                IPAddress ip = IPAddress.Parse(SeverAddress);
                IPEndPoint point = new IPEndPoint(ip, ServerPort);
              
               // sock.BeginConnect(point, new AsyncCallback(ConnectServer), sock);
               //异步连接有时不成功，为什么
                sock.Connect(point);

                sock.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, new AsyncCallback(CallReveive), sock);
             
                return "OK";
            }
            catch(Exception ex)
            {
               return ex.Message;
            }

        }

        private void ConnectServer(IAsyncResult Iay)
        {
            Socket sock = Iay.AsyncState as Socket;
            if (sock != null)
            {
                sock.EndConnect(Iay);
            }

        }

        private void CallReveive(IAsyncResult Iay)
        {
            try
            {
                Socket sock = Iay.AsyncState as Socket;
                if (sock != null && IsSocketConnected(sock))
                {
                    int len = sock.EndReceive(Iay);
                    string RecvString = Encoding.Default.GetString(buffer, 0, len);

                    RecvEventArg Recv = new RecvEventArg();
                    Recv.Message = RecvString;
                    Recv.RecvBuffer = new byte[len];
                    Buffer.BlockCopy(buffer, 0, Recv.RecvBuffer, 0, len);
                    Recv.RecvTimeStamp = DateTime.Now;
                    Recv.SendHeader = sendHeader;
                    Recv.SendTimeStamp = sendTimeStamp;
                    Recv.ClientNo = clientNo;
                    Recv.ClientDesc = clientDesc;
                    RaiseDataReceived(Recv);

                    Recv = null;

                }

                if (sock != null && IsSocketConnected(sock))
                {
                    sock.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, new AsyncCallback(CallReveive), sock);
                }
            }
            catch (Exception e) 
            {
               // throw e;

                RecvEventArg Recv = new RecvEventArg();
                Recv.Message = e.Message;
                Recv.RecvTimeStamp = DateTime.Now;
                Recv.SendHeader = sendHeader;
                Recv.SendTimeStamp = sendTimeStamp;
                Recv.ClientNo = clientNo;
                Recv.ClientDesc = clientDesc;
                RaiseDataReceived(Recv);

            }
        }



        public bool IsSocketConnected(Socket s)
        {
            if (s == null)
                return false;
            return !((s.Poll(1000, SelectMode.SelectRead) && (s.Available == 0)) || !s.Connected);
        }


        public string SendTextToServer(string SendHeader, string SendMsg)
        {
            try
            {
                sendHeader = SendHeader;
                sendTimeStamp = DateTime.Now;
                if (sock != null && IsSocketConnected(sock))
                {
                    byte[] data = Encoding.Default.GetBytes(SendMsg);
                    sock.BeginSend(data, 0, data.Length, SocketFlags.None, new AsyncCallback(CallBackSend), sock);
                    return "OK";
                }
               
                 else if (!IsSocketConnected(sock))
                {
                    return "Connect Lose!";
                }
                else
                {
                    return "Unknown Error!";
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }


        public string SendTextToServer2(string SendHeader, string SendMsg)
        {
            try
            {
                sendHeader = SendHeader;
                sendTimeStamp = DateTime.Now;
                if (sock != null && IsSocketConnected(sock))
                {
                     byte[] data = Encoding.ASCII.GetBytes(SendMsg);
                    sock.SendBufferSize = 1024;
                    sock.Send(data);
                    return "OK";
                }

                else if (!IsSocketConnected(sock))
                {
                    return "Connect Lose!";
                }
                else
                {
                    return "Unknown Error!";
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }


        public string SendBinaryToServer(string SendHeader,byte[] SendMsg)
        {
            try
            {
                sendHeader = SendHeader;
                sendTimeStamp = DateTime.Now;
                if (sock != null && IsSocketConnected(sock))
                {
                    sock.BeginSend(SendMsg, 0, SendMsg.Length, SocketFlags.None, new AsyncCallback(CallBackSend), sock);
                    return "OK";
                }
                else if (!IsSocketConnected(sock))
                {
                    return "Connect Lose!";
                }
                else
                {
                    return "Unknown Error!";
                }
               
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }


        private void CallBackSend(IAsyncResult Iay)
        {
            Socket sock = Iay.AsyncState as Socket;
            if (sock != null)
            {
                int len = sock.EndSend(Iay);
            }
        }

        /// <summary>  
        /// 接收到数据事件  
        /// </summary>  
        public event EventHandler<RecvEventArg> DataReceived;

        private void RaiseDataReceived(RecvEventArg Recv)
        {
            if (DataReceived != null)
            {
                DataReceived(this, Recv);
            }
        }




    }
}
