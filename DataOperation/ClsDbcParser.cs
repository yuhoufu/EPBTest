using System;
using System.Data;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
namespace DataOperation
{
    public class DbcParser
    {
        public static string ParseDbcFile(string filePath, out DataTable resultTable)
        {
            try
            {
                // 创建结果DataTable
                resultTable = new DataTable();
                resultTable.Columns.Add("MessageID", typeof(string));
                resultTable.Columns.Add("MessageName", typeof(string));
                resultTable.Columns.Add("DLC", typeof(int));
                resultTable.Columns.Add("Transmitter", typeof(string));
                resultTable.Columns.Add("SignalName", typeof(string));
                resultTable.Columns.Add("StartBit", typeof(int));
                resultTable.Columns.Add("Length", typeof(int));
                resultTable.Columns.Add("ByteOrder", typeof(string));
                resultTable.Columns.Add("ValueType", typeof(string));
                resultTable.Columns.Add("Factor", typeof(double));
                resultTable.Columns.Add("Offset", typeof(double));
                resultTable.Columns.Add("Min", typeof(double));
                resultTable.Columns.Add("Max", typeof(double));
                resultTable.Columns.Add("Unit", typeof(string));
                resultTable.Columns.Add("Receiver", typeof(string));

                if (!File.Exists(filePath))
                {
                    resultTable = null;
                    return filePath + " DBC file not found";
                }

                string[] lines = File.ReadAllLines(filePath);
                string currentMessageId = "";
                string currentMessageName = "";
                int currentDlc = 0;
                string currentTransmitter = "";

                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();

                    // 解析消息定义 (BO_)
                    if (trimmedLine.StartsWith("BO_"))
                    {
                        // 示例: BO_ 1552 WIU_TestBench_Mot2ReqCmdRR_Debug: 16 WIURR
                        var match = Regex.Match(trimmedLine, @"BO_\s+(\d+)\s+(\w+)\s*:\s*(\d+)\s+(\w+)");
                        if (match.Success)
                        {
                            currentMessageId = match.Groups[1].Value;
                            currentMessageName = match.Groups[2].Value;
                            currentDlc = int.Parse(match.Groups[3].Value);
                            currentTransmitter = match.Groups[4].Value;
                        }
                    }
                    // 解析信号定义 (SG_)
                    else if (trimmedLine.StartsWith("SG_"))
                    {
                        // 示例: SG_ wiu_testBench2_setPoint_dcVolCmd : 0|10@1- (0.1,0) [-51.2|51.1] "" Vector__XXX
                        var match = Regex.Match(trimmedLine,
                            @"SG_\s+(\w+)\s*:\s*(\d+)\|(\d+)@(\d+)([+-])\s+\(([^,]+),([^)]+)\)\s+\[([^|]+)\|([^\]]+)\]\s+""([^""]*)""\s+(\w+)");

                        if (match.Success)
                        {
                            DataRow row = resultTable.NewRow();
                            row["MessageID"] = currentMessageId;
                            row["MessageName"] = currentMessageName;
                            row["DLC"] = currentDlc;
                            row["Transmitter"] = currentTransmitter;
                            row["SignalName"] = match.Groups[1].Value;
                            row["StartBit"] = int.Parse(match.Groups[2].Value);
                            row["Length"] = int.Parse(match.Groups[3].Value);

                            // 处理字节序和值类型
                            int byteOrder = int.Parse(match.Groups[4].Value);
                            string valueSign = match.Groups[5].Value;
                            row["ByteOrder"] = byteOrder == 0 ? "Motorola" : "Intel";
                            row["ValueType"] = valueSign == "+" ? "Unsigned" : "Signed";

                            // 处理因子和偏移量
                            row["Factor"] = double.Parse(match.Groups[6].Value);
                            row["Offset"] = double.Parse(match.Groups[7].Value);

                            // 处理最小最大值
                            row["Min"] = double.Parse(match.Groups[8].Value);
                            row["Max"] = double.Parse(match.Groups[9].Value);

                            // 处理单位和接收节点
                            row["Unit"] = match.Groups[10].Value;
                            row["Receiver"] = match.Groups[11].Value;

                            resultTable.Rows.Add(row);
                        }
                        else
                        {
                            // 尝试匹配简化的信号格式（没有最小最大值和单位）
                            match = Regex.Match(trimmedLine,
                                @"SG_\s+(\w+)\s*:\s*(\d+)\|(\d+)@(\d+)([+-])\s+\(([^,]+),([^)]+)\)\s+""([^""]*)""\s+(\w+)");

                            if (match.Success)
                            {
                                DataRow row = resultTable.NewRow();
                                row["MessageID"] = currentMessageId;
                                row["MessageName"] = currentMessageName;
                                row["DLC"] = currentDlc;
                                row["Transmitter"] = currentTransmitter;
                                row["SignalName"] = match.Groups[1].Value;
                                row["StartBit"] = int.Parse(match.Groups[2].Value);
                                row["Length"] = int.Parse(match.Groups[3].Value);

                                int byteOrder = int.Parse(match.Groups[4].Value);
                                string valueSign = match.Groups[5].Value;
                                row["ByteOrder"] = byteOrder == 0 ? "Motorola" : "Intel";
                                row["ValueType"] = valueSign == "+" ? "Unsigned" : "Signed";

                                row["Factor"] = double.Parse(match.Groups[6].Value);
                                row["Offset"] = double.Parse(match.Groups[7].Value);

                                // 如果没有提供最小最大值，则根据信号类型和长度计算
                                CalculateMinMax(row, int.Parse(match.Groups[3].Value), valueSign == "-");

                                row["Unit"] = match.Groups[8].Value;
                                row["Receiver"] = match.Groups[9].Value;

                                resultTable.Rows.Add(row);
                            }
                        }
                    }
                }

                return "OK";
            }
            catch (Exception ex)
            {
                resultTable = null;
                return ex.Message;
            }
        }

        private static void CalculateMinMax(DataRow row, int bitLength, bool isSigned)
        {
            double factor = Convert.ToDouble(row["Factor"]);
            double offset = Convert.ToDouble(row["Offset"]);

            if (isSigned)
            {
                long minRaw = -(1L << (bitLength - 1));
                long maxRaw = (1L << (bitLength - 1)) - 1;

                row["Min"] = minRaw * factor + offset;
                row["Max"] = maxRaw * factor + offset;
            }
            else
            {
                ulong maxRaw = (1UL << bitLength) - 1;

                row["Min"] = 0 * factor + offset;
                row["Max"] = maxRaw * factor + offset;
            }
        }

        public static string TryGetFactorOffset(DataTable signalTable, uint messageId, string signalNamePattern,
        out double factor, out double offset)
        {
            factor = 0;
            offset = 0;

            try
            {
                // 筛选匹配MessageID的行
                var rows = signalTable.AsEnumerable()
                    .Where(row => int.Parse(row.Field<string>("MessageID")) == (int)messageId);

                // 如果没有找到匹配MessageID的行，返回false
                if (!rows.Any())
                    return "Not Find ID";

                // 尝试模糊匹配SignalName
                var matchedRow = rows.FirstOrDefault(row =>
                    row.Field<string>("SignalName").Contains(signalNamePattern));

                // 如果没有找到匹配SignalName的行，返回false
                if (matchedRow == null)
                    return "Not Find SignalName";

                // 获取Factor和Offset
                factor = matchedRow.Field<double>("Factor");
                offset = matchedRow.Field<double>("Offset");

                return "OK";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }


    }
}
