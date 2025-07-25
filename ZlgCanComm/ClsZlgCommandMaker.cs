using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ZlgCanComm
{
   public  class EmbControl
    {
        public short setPoint_torque;
        public short setPoint_speed;
        public short setPoint_position;
        public short setPoint_clampForce;
        public byte operationMod_Req;
        public byte normalMode;
        public ushort epbClampForceReq;
        public byte enable;
    }


    public class ClsZlgCommandMaker
    {
        public static byte[] GetEmbControlBytes(EmbControl control)
        {
            byte[] result = new byte[11];

            // 转换前 8 字节的 short 字段（小端模式）
            Buffer.BlockCopy(BitConverter.GetBytes(control.setPoint_torque), 0, result, 0, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(control.setPoint_speed), 0, result, 2, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(control.setPoint_position), 0, result, 4, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(control.setPoint_clampForce), 0, result, 6, 2);

            // 组合第 9 字节：前 4 位 operationMod_Req，后 4 位 normalMode
            //result[8] = (byte)(
            //    ((control.operationMod_Req & 0x0F) << 4) |  // 取低 4 位并左移
            //    (control.normalMode & 0x0F)                  // 取低 4 位

            result[8] = (byte)(
             ((control.normalMode & 0x0F) << 4) |  // 取低 4 位并左移
               (control.operationMod_Req & 0x0F)                  // 取

            );


           




            // 组合最后 2 字节：前 15 位 epbClampForceReq + 最后 1 位 enable
            ushort combined = (ushort)(
                ((control.epbClampForceReq & 0x7FFF) << 1) 
            );

            // 写入最后两个字节（小端模式）
            byte[] combinedBytes = BitConverter.GetBytes(combined);
            result[9] = combinedBytes[0];
            if(control.enable==1)
            {
                result[10] = (byte)(combinedBytes[1]+0x80);
            }
            
            // 0x80    10000000     76543210    高码位在前，低码位在后

            return result;
        }
    }
}
