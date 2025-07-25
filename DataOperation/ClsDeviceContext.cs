using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
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
        private readonly ConcurrentQueue<CanData> CanRawData = new ConcurrentQueue<CanData>();
  

        private readonly SemaphoreSlim RawFileLock = new SemaphoreSlim(1);
        private readonly SemaphoreSlim StatFileLock = new SemaphoreSlim(1);
        private DateTime _lastFlushTime = DateTime.MinValue;
        private string _currentFileName;
  
        private int  MaxLens;
      
        private double MaxStoreMins;
        private string StorePath;
        private int RecvDataLens;
        private int SaveCounter = 0;
        private int FileCounter = 0;

        public ConcurrentDictionary<int, double> eMBHandlerToRecvCanForceScale = new ConcurrentDictionary<int, double>();
        public ConcurrentDictionary<int, double> eMBHandlerToRecvCanForceOffset = new ConcurrentDictionary<int, double>();

        public ConcurrentDictionary<int, double> eMBHandlerToRecvCanCurrentScale = new ConcurrentDictionary<int, double>();
        public ConcurrentDictionary<int, double> eMBHandlerToRecvCanCurrentOffset = new ConcurrentDictionary<int, double>();

        public ConcurrentDictionary<int, double> eMBHandlerToRecvCanTorqueScale = new ConcurrentDictionary<int, double>();
        public ConcurrentDictionary<int, double> eMBHandlerToRecvCanTorqueOffset = new ConcurrentDictionary<int, double>();


        public DeviceContext(int deviceId,int maxLens,double daqTimeSpanMillSec,double maxStoreMins,string storePath,int recvDataLens)
        {
            DeviceId = deviceId;
            MaxLens = maxLens;
            MaxStoreMins = maxStoreMins;
            StorePath = storePath;
            RecvDataLens = recvDataLens;
          //  CanStatResult = new ConcurrentQueue<byte[]>();
            FileCounter = 0;
            _lastFlushTime = DateTime.Now;
            _currentFileName = GenerateFileName();

            int Lens = CanRawData.Count;

                 for (int i = 0; i < Lens; i++)
            {
                CanRawData.TryDequeue(out CanData canData);
            }


        }



        private string GenerateFileName()
        {
            FileCounter++;
            // return  $"{StorePath}\\EMB_CAN{DeviceId + 1}_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
            return $"{StorePath}\\EMB_CAN{DeviceId + 1}_Raw_" + FileCounter.ToString() + ".bin";
        }
           


        public void EnqueueRawData(byte[] data,DateTime recvTime)
        {
            var canData = new CanData
            {
                Data = data,
                RecvTime = recvTime,
            };

            CanRawData.Enqueue(canData);
            if (CanRawData.Count > MaxLens)
            {
                CanData removedData;
                CanRawData.TryDequeue(out removedData);
            }
        }

        
        public async Task FlushRawToDiskAsync()
        {
            await RawFileLock.WaitAsync();
            int Lens = CanRawData.Count;
            byte[] buffer = new byte[Lens * (12+RecvDataLens)];
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
                    _currentFileName = GenerateFileName();
                    _lastFlushTime = DateTime.Now;
                }

               


                int offset = 0;
                SaveCounter++;

                if (SaveCounter <= 1)
                {
                    for (int i = 0; i < Lens; i++)
                    {
                        CanRawData.TryDequeue(out CanData canData);
                    }
                            return;
                }
                //跳过最初一次，数据可能会乱或者不完整


                for (int i=0;i<Lens;i++)
                    {
                        if(CanRawData.TryDequeue(out CanData canData))
                        {

                        var BrakeNoBytes = BitConverter.GetBytes(SaveCounter-1);
                        Buffer.BlockCopy(BrakeNoBytes, 0, buffer, offset, 4);
                        offset += 4;
                        var timeBytes = BitConverter.GetBytes(canData.RecvTime.ToFileTime());
                        Buffer.BlockCopy(timeBytes, 0, buffer, offset, 8);
                        offset += 8;
                        Buffer.BlockCopy(canData.Data, 0, buffer, offset, RecvDataLens);
                        offset += RecvDataLens;
                        }
                    }

                
                    // Step 4: 异步批量写入
               fs = new FileStream(_currentFileName,
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

                string logFilePath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), $"\\Can{DeviceId + 1}WriteDiskErrorLog.txt");
                string errorMessage = $"[{DateTime.Now}] EMB_CAN_{DeviceId + 1} flush error: {ex.Message}";

                System.IO.File.AppendAllText(logFilePath, errorMessage + Environment.NewLine);


            }
            finally
            {
                RawFileLock.Release();
                buffer = null;
                if (fs != null)
                {
                    fs.Dispose();
                }
            }
        }

    }


}
