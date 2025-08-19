using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataOperation
{
    public class ClsBitFieldParser
    {
        public static string ParseData(byte[] data,
                                out int force,
                                out int faultflg,
                                out int current,
                                out int torque)
        {
            //if (data == null || data.Length != 24)
            //    throw new ArgumentException("Invalid byte array, must be 24 bytes");
            try
            {
                force = GetSignedValue(data, startBit: 0, bitLength: 15, isLittleEndian: true);
                faultflg = GetUnsignedValue(data, startBit: 15, bitLength: 1);
                current = GetUnsignedValue(data, startBit: 80, bitLength: 8);
                torque = GetSignedValue(data, startBit: 48, bitLength: 9, isLittleEndian: true);
                return "OK";
            }
            catch (Exception ex)
            {
                force = 0;
                faultflg = 0;
                current = 0;
                torque = 0;
                return ex.Message;
            }
        }


        public static string ParseForce(byte[] data, out int force)
        {
            try
            {
                force = GetSignedValue(data, startBit: 0, bitLength: 15, isLittleEndian: true);
                return "OK";
            }
            catch (Exception ex)
            {
                force = 0;
                return ex.Message;
            }
        }

        public static string ParseTorue(byte[] data, out int torue)
        {
            try
            {
                torue = GetSignedValue(data, startBit: 48, bitLength: 9, isLittleEndian: true);
                return "OK";
            }
            catch (Exception ex)
            {
                torue = 0;
                return ex.Message;
            }
        }

        public static string ParseCurrent(byte[] data, out int current)
        {
            try
            {
                current = GetUnsignedValue(data, startBit: 80, bitLength: 8);
                return "OK";
            }
            catch (Exception ex)
            {
                current = 0;
                return ex.Message;
            }
        }


        public static string ParseFaultFlg(byte[] data, out int faultFlg)
        {
            try
            {
                faultFlg = GetUnsignedValue(data, startBit: 15, bitLength: 1);
                return "OK";
            }
            catch (Exception ex)
            {
                faultFlg = 0;
                return ex.Message;
            }
        }



        public static double GetClampForce(byte[] data, double forceScale)
        {

            byte[] ForceBytes = new byte[2];
            ForceBytes[0] = data[0];
            ForceBytes[1] = (byte)(data[1] & 0x7f);    // 0~14

            return BitConverter.ToInt16(ForceBytes, 0) * forceScale;
        }

        public static byte GetFaultFlg(byte[] data)
        {
            return (byte)((data[1] & 0x80) / 0x80);   //15    高位在先;
        }



        public static string ParseClampData(byte[] data, double forceScale, double torqueScale, double currentScale,
                                                                  out double force,
                                                                  out byte faultFlg,
                                                                  out double torque,
                                                                  out double current)
        {
            try
            {
                byte[] ForceBytes = new byte[2];
                ForceBytes[0] = data[0];
                ForceBytes[1] = (byte)(data[1] & 0x7f);    // 0~14

                force = BitConverter.ToInt16(ForceBytes, 0) * forceScale;
                faultFlg = (byte)((data[1] & 0x80) / 0x80);   //15    高位在先

                byte[] TorqueBytes = new byte[2];
                TorqueBytes[0] = data[6];
                TorqueBytes[1] = (byte)(data[7] & 0x01);    // 48~54
                torque = BitConverter.ToInt16(TorqueBytes, 0) * torqueScale;

                current = data[10] * currentScale;     //80~87

                return "OK";
            }
            catch (Exception ex)
            {
                force = 0;
                torque = 0;
                faultFlg = 0;
                current = 0;
                return ex.Message;
            }

        }






        private static int GetSignedValue(byte[] data, int startBit, int bitLength, bool isLittleEndian = false)
        {
            ulong bits = ExtractBits(data, startBit, bitLength, isLittleEndian);
            if ((bits & (1UL << (bitLength - 1))) != 0)
            {
                bits |= (~0UL) << bitLength;
            }
            return (int)(long)bits;
        }



        private static int GetUnsignedValue(byte[] data, int startBit, int bitLength)
        {
            return (int)ExtractBits(data, startBit, bitLength);
        }

        // 修改后的位提取逻辑，支持小端序跨字节处理
        private static ulong ExtractBits(byte[] data, int startBit, int bitLength, bool isLittleEndian = false)
        {
            ulong result = 0;
            int currentBit = 0;

            // 计算涉及的字节范围
            int startByte = startBit / 8;
            int endByte = (startBit + bitLength - 1) / 8;

            // 小端序调整字节处理顺序
            if (isLittleEndian && (endByte > startByte))
            {
                // 反向遍历字节
                for (int byteIdx = endByte; byteIdx >= startByte; byteIdx--)
                {
                    int bitsInThisByte = Math.Min(8, bitLength - currentBit);
                    for (int bit = 0; bit < bitsInThisByte; bit++)
                    {
                        int globalBit = startBit + currentBit;
                        int byteIndex = globalBit / 8;
                        int bitIndexInByte = 7 - (globalBit % 8);

                        byte currentByte = data[byteIndex];
                        int bitValue = (currentByte >> bitIndexInByte) & 0x01;

                        result = (result << 1) | (ulong)bitValue;
                        currentBit++;
                    }
                }
            }
            else
            {
                // 原始处理逻辑（大端序）
                for (int i = 0; i < bitLength; i++)
                {
                    int globalBitPos = startBit + i;
                    int byteIndex = globalBitPos / 8;
                    int bitIndexInByte = 7 - (globalBitPos % 8);

                    byte currentByte = data[byteIndex];
                    int bitValue = (currentByte >> bitIndexInByte) & 0x01;

                    result = (result << 1) | (ulong)bitValue;
                }
            }

            return result;
        }




    }
}
