using NationalInstruments.DAQmx;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MtEmbTest
{
    //全局共享类
    //全局共享类
    public static class ClsGlobal
    {
        public static int Voltage;
        public static int MaxCurrent;
        public static int MinCurrent;
        public static int MaxPower;
        public static int MinPower;

        public static int[] PowerStatus = new int[1] { 0 };
        public static int ClampCount = 0;
        public static int ReleaseCount = 0;

        public static int ClampSpan = 0;
        public static int ReleaseSpan = 0;

        public static int ReleaseWaitSpan = 0;

        public static int IsPushFirst = 0;
        public static int IsLiner = 0;

        public static int PushSpan = 0;
        public static int PushCount = 0;


        public static DataTable Dbc;
        public static string DIChannel;
        public static string DoChannel;
        public static string AOChannel;
        public static int StartPosDINo;
        public static int EndPosDINo;

        public static int LimitStartPosDINo;
        public static int LimitEndPosDINo;
        public static int AdjustDoNo;

        public static int VPPMD3No;
        public static int VPPMD2No;
        public static int VPPMD1No;
        public static double ClampAoVol;
        public static double ReleaseAoVol;


        public static short ClampPosition;
        public static short ClampSpeed;
        public static byte ClampModReq;
        public static short ClampTorque;
        public static byte ClampNormalMode;
        public static short ClampForce;
        public static byte ClampEnable;
        public static ushort ClampForceReq;
        public static short ReleasePosition;
        public static short ReleaseSpeed;
        public static byte ReleaseModeReq;
        public static short ReleaseTorque;
        public static byte ReleaseNormalMode;
        public static short ReleaseForce;
        public static byte ReleaseEnable;
        public static ushort ReleaseForceReq;

        public static int DRate;
        public static int ARate;
        public static int CardNo;

        public static int MsgInterval;
        public static int ResistorEnabel;
        public static int Protocol;
        public static int FrameType;

        public static int FrameSendType;
        public static int FrameExpType;
        public static int FrameTimerNo;

        public static short SendForceScale;
        public static double RecvForceScale;
        public static double RecvForceJudgeDelt;
        public static double RecvMsgInterval; //单位毫秒


        public static string SerialPort;
        public static int Baud;
        public static int Parity;
        public static int DataBits;
        public static int StopBit;

        public static string VppmWorkMode;
        public static string FL_Send;
        public static string FL_Recv;
        public static string FR_Send;
        public static string FR_Recv;
        public static string RL_Send;
        public static string RL_Recv;
        public static string RR_Send;
        public static string RR_Recv;
        public static double XDuration;
        public static double FileChangeMinutes;
        public static double DaqFrequency;
        public static int SamplesPerChannel;
        public static double CanRecvTimeSpanMillSecs;

        public static int DirectionValveChannel;
        public static int PowerChannel;
        public static int AlertChannel;
        public static int SerialPortRetrys;
        public static int MedianLens;
        public static int RecvCanLens;
        public static int ValveMode;

        public static double DaqTimeBias;

        //换向阀工作模式 0 串口  1 数采卡
        public static int WaitValveGoBack;
        public static int WaitValveChangeFinishSpan;
        public static int WaitSpanBeforePush;
        public static int SerialSendIntervalSpan;
        public static int DevResetWaitSpan;

        public static string[] PowerServerAdr = new string[1] { "" };
        public static string[] PowerServerPort = new string[1] { "" };


        static ClsGlobal()
        {
            Voltage = 0;
            MaxCurrent = 0;
            MinCurrent = 0;
            MaxPower = 0;
            MinPower = 0;


            ClampCount = 0;
            ReleaseCount = 0;
            ClampSpan = 0;
            ReleaseSpan = 0;
            ReleaseWaitSpan = 0;
            IsPushFirst = 0;
            IsLiner = 0;
            PushSpan = 0;
            PushCount = 0;


            Dbc = new DataTable();
            FL_Send = "";
            FL_Recv = "";
            FR_Send = "";
            FR_Recv = "";
            RL_Send = "";
            RL_Recv = "";
            RR_Send = "";
            RR_Recv = "";
            CanRecvTimeSpanMillSecs = 0.0;
            XDuration = 0.0;
            FileChangeMinutes = 0.0;
            DaqFrequency = 0.0;
            SamplesPerChannel = 0;

            SerialPort = "";
            Baud = 0;
            Parity = 0;
            DataBits = 0;
            StopBit = 0;


            ClampPosition = 0;
            ClampSpeed = 0;
            ClampModReq = 0;
            ClampTorque = 0;
            ClampNormalMode = 0;
            ClampForce = 0;
            ClampEnable = 0;
            ClampForceReq = 0;
            ReleasePosition = 0;
            ReleaseSpeed = 0;
            ReleaseModeReq = 0;
            ReleaseTorque = 0;
            ReleaseNormalMode = 0;
            ReleaseForce = 0;
            ReleaseEnable = 0;
            ReleaseForceReq = 0;

            DRate = 0;
            ARate = 0;
            CardNo = 0;

            MsgInterval = 0;
            ResistorEnabel = 0;
            Protocol = 0;
            FrameType = 0;
            FrameSendType = 0;
            FrameExpType = 0;
            FrameTimerNo = 0;

            SendForceScale = 1;
            RecvForceScale = 1.0;
            RecvForceJudgeDelt = 0.0;

            DoChannel = "";
            DIChannel = "";
            AOChannel = "";
            StartPosDINo = 0;
            EndPosDINo = 0;
            ClampAoVol = 0.0;
            ReleaseAoVol = 0.0;

            DirectionValveChannel = 0;
            PowerChannel = 0;
            AlertChannel = 0;
            VPPMD1No = 0;
            VPPMD2No = 0;
            VPPMD3No = 0;
            VppmWorkMode = "";
            SerialPortRetrys = 0;
            MedianLens = 0;
            RecvCanLens = 0;

            LimitStartPosDINo = 0;
            LimitEndPosDINo = 0;
            AdjustDoNo = 0;
            ValveMode = -1;

            WaitValveChangeFinishSpan = 0;
            WaitSpanBeforePush = 0;
            SerialSendIntervalSpan = 0;
            DevResetWaitSpan = 0;
            DaqTimeBias = 0.0;
        }
    }
}