using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
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
        private readonly SemaphoreSlim rawFileLock = new SemaphoreSlim(1);

        private readonly ConcurrentQueue<DaqAIData> DaqStatData = new ConcurrentQueue<DaqAIData>();
        private readonly SemaphoreSlim statFileLock = new SemaphoreSlim(1);



        private DateTime _lastFlushTime = DateTime.MinValue;
        private string currentRawFileName;
        private string currentStatFileName;
        private int MaxLens;
        private double StoreTimeMinutes;
        private double DaqSpanMillSec1;
        private int SamplesPerChannel;
        private int Channels;
        private string StorePath;
        private int SaveRawCounter;
        private int SaveStatCounter;
        private int FileCounter = 0;
       

        public SortedDictionary<string, int> eMBToDaqCurrentChannel = new SortedDictionary<string, int>();
        public ConcurrentDictionary<string, double> paraNameToScale = new ConcurrentDictionary<string, double>();
        public ConcurrentDictionary<string, double> paraNameToOffset = new ConcurrentDictionary<string, double>();
        public ConcurrentDictionary<string, double> paraNameToZeroValue = new ConcurrentDictionary<string, double>();
        public int medianLens;



        public DaqAIContext(string cardName, int maxLens,double storeTimeMinutes, double daqSpanMillSec,int channels,int samplesPerChannel, string storePath)
        {
            DaqCardName = cardName;
          
            MaxLens = maxLens;
            StoreTimeMinutes = storeTimeMinutes;
            DaqSpanMillSec1 = daqSpanMillSec;
            SamplesPerChannel = samplesPerChannel;
            Channels = channels;
            StorePath = storePath;
            SaveRawCounter = 0;
            SaveStatCounter=0;
            FileCounter = 0;
            _lastFlushTime = DateTime.Now;

            currentRawFileName = GenerateRawFileName();
            currentStatFileName = GenerateStatFileName();


            int Lens = DaqRawData.Count;

            for (int i = 0; i < Lens; i++)
            {
                DaqRawData.TryDequeue(out DaqAIData daqRawData);
            }


            Lens = DaqStatData.Count;

            for (int i = 0; i < Lens; i++)
            {
                DaqStatData.TryDequeue(out DaqAIData daqStatData);
            }




        }

        private string GenerateRawFileName()
        {
            FileCounter++;
          // return  $"{StorePath}\\DAQ_{DaqCardName}_{DateTime.Now:yyyyMMdd}_"+ FileCounter.ToString()+".bin";
            return $"{StorePath}\\DAQ_{DaqCardName}_Raw_" + FileCounter.ToString() + ".bin";
        }
            

        private string GenerateStatFileName() =>
             $"{StorePath}\\DAQ_{DaqCardName}_Stat.bin";

        public void EnqueueStatData(double[,] data, DateTime recvTime)
        {
            var daqData = new DaqAIData
            {
                Data = data,
                RecvTime = recvTime,
            };

            DaqStatData.Enqueue(daqData);
            if (DaqStatData.Count > MaxLens)
            {
                DaqAIData removedData;
                DaqStatData.TryDequeue(out removedData);
            }
        }



        public void EnqueueRawData(double[,] data, DateTime recvTime,DateTime lastTime)
        {
            var daqData = new DaqAIData
            {
                Data = data,
                RecvTime = recvTime,
                LastRecvTime = lastTime,
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
            
            await rawFileLock.WaitAsync();
            int Lens = DaqRawData.Count;
       
            byte[] buffer = new byte[Lens * 8 * Channels * SamplesPerChannel + 12 * SamplesPerChannel * Lens];
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
                    currentRawFileName = GenerateRawFileName();
                    _lastFlushTime = DateTime.Now;
                }

                    SaveRawCounter++;
                    int offset = 0;

                if (SaveRawCounter <= 1)
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
                          
                            var CounterBytes = BitConverter.GetBytes(SaveRawCounter-1);
                            Buffer.BlockCopy(CounterBytes, 0, buffer, offset, 4);
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
              
                string logFilePath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), $"DAQ_{DaqCardName}WriteDiskErrorLog.txt");
                string errorMessage = $"[{DateTime.Now}] DAQ_{DaqCardName} flush error: {ex.Message}";
              
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
            int Lens = DaqStatData.Count;
            FileStream fs = null;


            try
            {

                if (Lens < 1)
                {
                    return;    //finally 还是要先执行，然后才真正的return
                }


                SaveStatCounter++;

                if (SaveStatCounter <= 1)
                {
                    for (int i = 0; i < Lens; i++)
                    {
                        DaqStatData.TryDequeue(out DaqAIData daqData);
                    }
                    return;
                }

                double[][] StatData = new double[Channels][];
                int[] index = new int[Channels];
                DateTime[] RecvTime = new DateTime[Lens];

                int totalCount = DaqStatData.Take(Lens).Sum(arr => arr.Data.GetLength(1));
                for (int i = 0; i < Channels; i++)
                {
                    StatData[i] = new double[totalCount];
                    index[i] = 0;
                }
                for (int i = 0; i < Lens; i++)
                {
                    if (DaqStatData.TryDequeue(out DaqAIData daqData))
                    {
                        RecvTime[i] = daqData.RecvTime;
                        int j = -1;
                        foreach (var channel in eMBToDaqCurrentChannel)
                        {
                            j++;
                            int ChannelNo = channel.Value;
                            double[] ChannelData = new double[SamplesPerChannel];
                            Buffer.BlockCopy(daqData.Data, ChannelNo * 8 * SamplesPerChannel, ChannelData, 0, 8 * SamplesPerChannel);
                            Array.Copy(ChannelData, 0, StatData[j], index[j], ChannelData.Length);
                            index[j] += ChannelData.Length;
                        }
                    }
                }


                byte[] buffer = new byte[108];
                int offset = 0;

                var CounterBytes = BitConverter.GetBytes(SaveStatCounter-1);
                Buffer.BlockCopy(CounterBytes, 0, buffer, offset, 4);
                offset += 4;
               
                var timeBytes = BitConverter.GetBytes(RecvTime[0].ToFileTime());  //取第一个时间作为最值出现的时间
                Buffer.BlockCopy(timeBytes, 0, buffer, offset, 8);
                offset += 8;

                foreach (var channel in eMBToDaqCurrentChannel)
                {
                    int ChannelNo = channel.Value;
                    string EmbName = channel.Key;


                    double[] result = new double[totalCount];
                    for(int i=0;i< totalCount;i++)
                    {
                        result[i] = (StatData[ChannelNo][i] - paraNameToZeroValue[EmbName]) * paraNameToScale[EmbName] + paraNameToOffset[EmbName];
                    }

                    double[] filterCurrent = ClsDataFilter.MakeMedianFilterReducePoint(ref result, medianLens);

                    double max = filterCurrent.Max();
                    double min = filterCurrent.Min();

                    var maxBytes = BitConverter.GetBytes(max);
                    Buffer.BlockCopy(maxBytes, 0, buffer, offset, 8);
                    offset += 8;

                    var minBytes = BitConverter.GetBytes(min);
                    Buffer.BlockCopy(minBytes, 0, buffer, offset, 8);
                    offset += 8;

                }

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

                string logFilePath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), $"DAQ_{DaqCardName}WriteDiskErrorLog.txt");
                string errorMessage = $"[{DateTime.Now}] DAQ_{DaqCardName} flush error: {ex.Message}";

                System.IO.File.AppendAllText(logFilePath, errorMessage + Environment.NewLine);


            }
            finally
            {
                statFileLock.Release();
              
                if (fs != null)
                {
                    fs.Dispose();
                }
            }
        }


    }


}
