using System;
using System.Collections.Generic;
using System.Data.Odbc;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;

namespace DataOperation
{
    public class ClsSerialCommandMaker
    {

        // 反转 8 位无符号整数的位顺序
        private static byte InvertUint8(byte src)
        {
            byte temp = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((src & (1 << i)) != 0)
                {
                    temp |= (byte)(1 << (7 - i));
                }
            }
            return temp;
        }

        // 反转 16 位无符号整数的位顺序
        private static ushort InvertUint16(ushort src)
        {
            ushort temp = 0;
            for (int i = 0; i < 16; i++)
            {
                if ((src & (1 << i)) != 0)
                {
                    temp |= (ushort)(1 << (15 - i));
                }
            }
            return temp;
        }


        public static ushort CRC16_X25(byte[] puchMsg, uint usDataLen)
        {
            ushort wCRCin = 0xFFFF;
            ushort wCPoly = 0x1021;
            //ushort wCPoly = 0x8005;
            byte wChar = 0;

            for (uint i = 0; i < usDataLen; i++)
            {
                wChar = puchMsg[i];
                wChar = InvertUint8(wChar);
                wCRCin ^= (ushort)(wChar << 8);

                for (int j = 0; j < 8; j++)
                {
                    if ((wCRCin & 0x8000) != 0)
                    {
                        wCRCin = (ushort)((wCRCin << 1) ^ wCPoly);
                    }
                    else
                    {
                        wCRCin = (ushort)(wCRCin << 1);
                    }
                }
            }
            wCRCin = InvertUint16(wCRCin);
            return (ushort)(wCRCin ^ 0xFFFF);
        }

        private static ushort CalculateModbusCrc(byte[] data)
        {
            ushort crc = 0xFFFF;  // Modbus CRC初始值
            const ushort polynomial = 0xA001;  // 多项式反转形式

            foreach (byte b in data)
            {
                crc ^= b;  // 异或当前字节
                for (int i = 0; i < 8; i++)
                {
                    bool lsb = (crc & 0x0001) != 0;  // 检查最低位
                    crc >>= 1;
                    if (lsb) crc ^= polynomial;      // 若最低位为1则异或多项式
                }
            }


            return crc;
        }









        public static byte[] GenerateDoCommand(byte Addr,byte FuncCode, byte[] DoNumber, byte[] DoStatus)
        {
            List<byte> frame = new List<byte>();
            frame.Add(Addr);
            frame.Add(FuncCode);

            for (int i = 0; i < DoNumber.Length; i++)
            {
                frame.Add(DoNumber[i]);
            }
            for (int i = 0; i < DoStatus.Length; i++)
            {
                frame.Add(DoStatus[i]);
            }

            byte[] beforeCrc = frame.ToArray();


            // 计算CRC
            ushort crc = CalculateModbusCrc(beforeCrc);
            frame.Add((byte)(crc & 0xFF));  // 低位在前
            frame.Add((byte)(crc >> 8));    // 高位在后

          
            return frame.ToArray();


        }




    }
}
