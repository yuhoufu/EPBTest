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
    public class DaqAIData
    {
        public double[,] Data { get; set; }
        public DateTime RecvTime { get; set; }
        public DateTime LastRecvTime { get; set; }

    }


    public class DaqAIContext
    {
        public string DaqCardName { get; }
        private readonly ConcurrentQueue<DaqAIData> DaqRawData = new ConcurrentQueue<DaqAIData>();
        private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1);
       

   





        private DateTime _lastFlushTime = DateTime.MinValue;
        private string _currentFileName;
        private int MaxLens;
        private double StoreTimeMinutes;
        private int SamplesPerChannel1;
        private int Channels;
        private string StorePath;
        private int SaveCounter = 0;
        private int FileCounter = 0;

        public DaqAIContext(string cardName, int maxLens,double storeTimeMinutes, double daqSpanMillSec,int channels,int samplesPerChannel, string storePath)
        {
            DaqCardName = cardName;
            MaxLens = maxLens;
            StoreTimeMinutes = storeTimeMinutes;
            SamplesPerChannel1 = samplesPerChannel;
            Channels = channels;
            StorePath = storePath;
            FileCounter = 0;
            _lastFlushTime = DateTime.Now;
            _currentFileName = GenerateFileName();

            int Lens = DaqRawData.Count;

            for (int i = 0; i < Lens; i++)
            {
                DaqRawData.TryDequeue(out DaqAIData daqRawData);
            }



        }

        private string GenerateFileName()
        {
            FileCounter++;
            // return $"{StorePath}\\DAQ_{DaqCardName}_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
            return $"{StorePath}\\DAQ_{DaqCardName}_Raw_" + FileCounter.ToString() + ".bin";
        }



        public void EnqueueData(double[,] data, DateTime recvTime,DateTime LastRecv)
        {
            var daqData = new DaqAIData
            {
                Data = data,
                RecvTime = recvTime,
                LastRecvTime = LastRecv
            };

            DaqRawData.Enqueue(daqData);
            if (DaqRawData.Count > MaxLens)
            {
                DaqAIData removedData;
                DaqRawData.TryDequeue(out removedData);
            }
        }


        




        // 定时触发的批量写入
        public async Task FlushRawToDiskAsync()
        {

            await _fileLock.WaitAsync();
            int Lens = DaqRawData.Count;
            byte[] buffer = new byte[Lens * 8 * Channels * SamplesPerChannel1 + 12 * SamplesPerChannel1 * Lens];
            FileStream fs = null;


            try
            {
                
                if (Lens < 1)
                {
                    return;    //finally 还是要先执行，然后才真正的return
                }

                // Step 1: 检查是否需要切换文件
                if ((DateTime.Now - _lastFlushTime).TotalMinutes >= StoreTimeMinutes)
                {
                    _currentFileName = GenerateFileName();
                    _lastFlushTime = DateTime.Now;
                }


              
                int offset = 0;

                SaveCounter++;

                if(SaveCounter<=1)
                {
                    for (int i = 0; i < Lens; i++)
                    {
                        DaqRawData.TryDequeue(out DaqAIData daqData);
                    }
                       
                      return;
                }
                //跳过最初一次，数据可能会乱或者不完整

                for (int i = 0; i < Lens; i++)
                    {
                        if (DaqRawData.TryDequeue(out DaqAIData daqData))
                        {
                        int RecvSamples = daqData.Data.GetLength(1);
                       double DaqSpanMillSec = daqData.RecvTime.Subtract(daqData.LastRecvTime).TotalMilliseconds / (double)RecvSamples;


                       
                        for (int j = 0; j < RecvSamples; j++)
                            {
                               var BrakeNoBytes = BitConverter.GetBytes(SaveCounter-1);
                               Buffer.BlockCopy(BrakeNoBytes, 0, buffer, offset, 4);
                               offset += 4;

                                 DateTime DaqTime = daqData.LastRecvTime.AddMilliseconds(DaqSpanMillSec * (double)j);
                                var timeBytes = BitConverter.GetBytes(DaqTime.ToFileTime());
                                Buffer.BlockCopy(timeBytes, 0, buffer, offset, 8);
                                offset += 8;

                                double[] rowData = new double[Channels];

                                for (int k = 0; k < Channels; k++)
                                {
                                    rowData[k] = daqData.Data[k, j];
                                }

                                Buffer.BlockCopy(rowData, 0, buffer, offset, 8 * Channels);
                                offset += 8 * Channels;

                            }
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
              
                string logFilePath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), $"DAQ_{DaqCardName}WriteDiskErrorLog.txt");
                string errorMessage = $"[{DateTime.Now}] DAQ_{DaqCardName} flush error: {ex.Message}";
                System.IO.File.AppendAllText(logFilePath, errorMessage + Environment.NewLine);



            }
            finally
            {
                _fileLock.Release();
                buffer = null;
                if (fs != null)
                {
                    fs.Dispose();
                }
            }
        }


       


    }


}
