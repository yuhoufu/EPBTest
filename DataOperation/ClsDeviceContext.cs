using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataOperation
{
    public class CanData
    {
        public byte[] Data { get; set; }
        public DateTime RecvTime { get; set; }

    }


    public class DeviceContext
    {
        public int DeviceId { get; }
        private readonly ConcurrentQueue<CanData> rawData = new ConcurrentQueue<CanData>();
        private readonly ConcurrentQueue<CanData> statData = new ConcurrentQueue<CanData>();

        private readonly SemaphoreSlim rawFileLock = new SemaphoreSlim(1);
        private readonly SemaphoreSlim statFileLock = new SemaphoreSlim(1);

        private int RawSaveCounter = 0;
        private int StatSaveCounter = 0;

        private DateTime _lastFlushTime = DateTime.MinValue;
        private string currentRawFileName;
        private string currentStatFileName;
        private int  MaxLens;
        private double DaqTimeSpanMillSec;
        private double MaxStoreMins;
        private string StorePath;
        private int RecvDataLens;
        private int FileCounter = 0;


        public  ConcurrentDictionary<int, double> eMBHandlerToRecvCanForceScale = new ConcurrentDictionary<int, double>();
        public ConcurrentDictionary<int, double> eMBHandlerToRecvCanForceOffset = new ConcurrentDictionary<int, double>();

        public ConcurrentDictionary<int, double> eMBHandlerToRecvCanCurrentScale = new ConcurrentDictionary<int, double>();
        public ConcurrentDictionary<int, double> eMBHandlerToRecvCanCurrentOffset = new ConcurrentDictionary<int, double>();

        public ConcurrentDictionary<int, double> eMBHandlerToRecvCanTorqueScale = new ConcurrentDictionary<int, double>();
        public ConcurrentDictionary<int, double> eMBHandlerToRecvCanTorqueOffset = new ConcurrentDictionary<int, double>();









        public DeviceContext(int deviceId,int maxLens,double daqTimeSpanMillSec,double maxStoreMins,string storePath,int recvDataLens)
        {
            DeviceId = deviceId;
         
            MaxLens = maxLens;
            DaqTimeSpanMillSec = daqTimeSpanMillSec;
            MaxStoreMins = maxStoreMins;
            StorePath = storePath;
            RecvDataLens = recvDataLens;
            FileCounter = 0;
            _lastFlushTime = DateTime.Now;

            currentRawFileName = GenerateRawFileName();
            currentStatFileName = GenerateStatFileName();

            int Lens = rawData.Count;

            for (int i = 0; i < Lens; i++)
            {
                rawData.TryDequeue(out CanData CanrawData);
            }


            Lens = statData.Count;

            for (int i = 0; i < Lens; i++)
            {
                statData.TryDequeue(out CanData CanStatData);
            }



        }

        private string GenerateRawFileName()
        {
            FileCounter++;
           // return  $"{StorePath}\\EMB_CAN{DeviceId + 1}_{DateTime.Now:yyyyMMdd}_" + FileCounter.ToString() + ".bin";
            return $"{StorePath}\\EMB_CAN{DeviceId + 1}_Raw_" + FileCounter.ToString() + ".bin";

        }

        private string GenerateStatFileName() =>
           $"{StorePath}\\EMB_CAN{DeviceId + 1}_Stat.bin";


        public void EnqueueRawData(byte[] data,DateTime recvTime)
        {
            var canData = new CanData
            {
                Data = data,
                RecvTime = recvTime,
            };

            rawData.Enqueue(canData);
            if (rawData.Count > MaxLens)
            {
                CanData removedData;
                rawData.TryDequeue(out removedData);
            }
        }


        public void EnqueueStatData(byte[] data, DateTime recvTime)
        {
            var canData = new CanData
            {
                Data = data,
                RecvTime = recvTime,
            };

            statData.Enqueue(canData);
            if (statData.Count > MaxLens)
            {
                CanData removedData;
                statData.TryDequeue(out removedData);
            }
        }

        // 定时触发的批量写入
        public async Task FlushRawToDiskAsync()
        {

            await rawFileLock.WaitAsync();
            int Lens = rawData.Count;
            byte[] buffer = new byte[Lens * (12 + RecvDataLens)];
            FileStream fs = null;

            try
            {
               
                if (Lens < 1)
                {
                    return;    //finally 还是要先执行，然后才真正的return
                }

                // Step 1: 检查是否需要切换文件
                if ((DateTime.Now - _lastFlushTime).TotalMinutes >= MaxStoreMins)
                {
                    currentRawFileName = GenerateRawFileName();
                    _lastFlushTime = DateTime.Now;
                }

                    RawSaveCounter++;
                    int offset = 0;

               

                if (RawSaveCounter <= 1)
                {
                    for (int i = 0; i < Lens; i++)
                    {
                        rawData.TryDequeue(out CanData canData);
                    }
                    return;
                }
                //跳过最初一次，数据可能会乱或者不完整


                for (int i=0;i<Lens;i++)
                    {
                        if(rawData.TryDequeue(out CanData canData))
                        {
                        var noBytes = BitConverter.GetBytes(RawSaveCounter-1);
                        Buffer.BlockCopy(noBytes, 0, buffer, offset, 4);
                        offset += 4;
                        var timeBytes = BitConverter.GetBytes(canData.RecvTime.ToFileTime());
                        Buffer.BlockCopy(timeBytes, 0, buffer, offset, 8);
                        offset += 8;
                        Buffer.BlockCopy(canData.Data, 0, buffer, offset, RecvDataLens);
                        offset += RecvDataLens;


                        }
                    }

                
                    // Step 4: 异步批量写入
               fs = new FileStream(currentRawFileName,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                8192,
                FileOptions.WriteThrough | FileOptions.Asynchronous);

                    await fs.WriteAsync(buffer, 0, offset);
                    await fs.FlushAsync();
            }
            catch (Exception ex)
            {

                string logFilePath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), $"Can{DeviceId+1}WriteDiskErrorLog.txt");
                string errorMessage = $"[{DateTime.Now}] EMB_CAN_{DeviceId + 1} flush error: {ex.Message}";

                System.IO.File.AppendAllText(logFilePath, errorMessage + Environment.NewLine);


            }
            finally
            {
                rawFileLock.Release();
                buffer = null;
                if (fs != null)
                {
                    fs.Dispose();
                }
            }
        }


        public async Task FlushStatToDiskAsync()
        {

            await statFileLock.WaitAsync();
            int Lens = statData.Count;
            byte[] buffer = new byte[60];
            FileStream fs = null;

            try
            {

                if (Lens < 1)
                {
                    return;    //finally 还是要先执行，然后才真正的return
                }

                double[] Force = new double[Lens];
                double[] Current = new double[Lens];
                double[] Torque = new double[Lens];
                DateTime[] RecvTime=new DateTime[Lens];

                for (int i = 0; i < Lens; i++)
                {
                    if (statData.TryDequeue(out CanData canData))
                    {
                        RecvTime[i] = canData.RecvTime;
                        double forceValue = 0;
                        double currentValue = 0;
                        byte faultflg = 0;
                        double torque = 0;
                        ClsBitFieldParser.ParseClampData(canData.Data,
                        eMBHandlerToRecvCanForceScale[DeviceId],
                        eMBHandlerToRecvCanTorqueScale[DeviceId],
                        eMBHandlerToRecvCanCurrentScale[DeviceId],
                        out forceValue, out faultflg, out torque, out currentValue);

                        Force[i] = forceValue;
                        Current[i] = currentValue;
                        Torque[i] = torque;
                    }
                }


                StatSaveCounter++;
                int offset = 0;

                if (StatSaveCounter <= 1)
                {
                    return;
                }



                var noBytes = BitConverter.GetBytes(StatSaveCounter-1);
                Buffer.BlockCopy(noBytes, 0, buffer, offset, 4);
                offset += 4;
                var timeBytes = BitConverter.GetBytes(RecvTime[0].ToFileTime());
                Buffer.BlockCopy(timeBytes, 0, buffer, offset, 8);
                offset += 8;

                var maxForceBytes = BitConverter.GetBytes(Force.Max());
                Buffer.BlockCopy(maxForceBytes, 0, buffer, offset, 8);
                offset += 8;

                var minForceBytes = BitConverter.GetBytes(Force.Min());
                Buffer.BlockCopy(minForceBytes, 0, buffer, offset, 8);
                offset += 8;


                var maxCurrentBytes = BitConverter.GetBytes(Current.Max());
                Buffer.BlockCopy(maxCurrentBytes, 0, buffer, offset, 8);
                offset += 8;

                var minCurrentBytes = BitConverter.GetBytes(Current.Min());
                Buffer.BlockCopy(minCurrentBytes, 0, buffer, offset, 8);
                offset += 8;


                var maxTorqueBytes = BitConverter.GetBytes(Torque.Max());
                Buffer.BlockCopy(maxTorqueBytes, 0, buffer, offset, 8);
                offset += 8;

                var minTorqueBytes = BitConverter.GetBytes(Torque.Min());
                Buffer.BlockCopy(minTorqueBytes, 0, buffer, offset, 8);
                offset += 8;

                // Step 4: 异步批量写入
                fs = new FileStream(currentStatFileName,
                 FileMode.Append,
                 FileAccess.Write,
                 FileShare.Read,
                 8192,
                 FileOptions.WriteThrough | FileOptions.Asynchronous);

                await fs.WriteAsync(buffer, 0, offset);
                await fs.FlushAsync();
           
            }
            catch (Exception ex)
            {

                string logFilePath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), $"Can{DeviceId + 1}WriteDiskErrorLog.txt");
                string errorMessage = $"[{DateTime.Now}] EMB_CAN_{DeviceId + 1}Stat flush error: {ex.Message}";

                System.IO.File.AppendAllText(logFilePath, errorMessage + Environment.NewLine);


            }
            finally
            {
                statFileLock.Release();
                buffer = null;
                if (fs != null)
                {
                    fs.Dispose();
                }
            }
        }


    }


}
