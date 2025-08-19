using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataOperation
{
    public class ClsDataFilter
    {
        public static double[] MakeMedianFilterKeepPoint_V1(ref double[] inputArray, int MedianLens)
        {
            // 参数校验保持不变
            if (inputArray == null)
                throw new ArgumentNullException(nameof(inputArray));
            if (MedianLens <= 0 || MedianLens % 2 == 0)
                throw new ArgumentException("MedianLens must be a positive odd number.");
            if (inputArray.Length == 0)
                return new double[0];

            int length = inputArray.Length;
            double[] output = new double[length];
            int k = (MedianLens - 1) / 2;

            for (int i = 0; i < length; i++)
            {
                double[] window = new double[MedianLens];
                for (int j = 0; j < MedianLens; j++)
                {
                    // 修改点：用 Min/Max 代替 Clamp
                    int rawPos = i - k + j;
                    int clampedPos = Math.Max(Math.Min(rawPos, length - 1), 0); // 等价于 Clamp
                    window[j] = inputArray[clampedPos];
                }

                Array.Sort(window);
                output[i] = window[k];
            }

            return output;
        }


        public static double[] MakeMedianFilterKeepPoint_V2(ref double[] inputArray, int MedianLens)
        {
            if (inputArray == null)
                throw new ArgumentNullException(nameof(inputArray));
            if (MedianLens <= 0 || MedianLens % 2 == 0)
                throw new ArgumentException("MedianLens must be a positive odd number.");
            if (inputArray.Length == 0)
                return new double[0];

            int length = inputArray.Length;
            double[] output = new double[length];
            int k = (MedianLens - 1) / 2;

            for (int i = 0; i < length; i++)
            {
                double[] window = new double[MedianLens];

                // 计算有效数据区间
                int srcStart = Math.Max(i - k, 0);
                int srcEnd = Math.Min(i + k, length - 1);
                int copyLength = srcEnd - srcStart + 1;

                // 计算边界填充量
                int leftPad = k - (i - srcStart);
                int rightPad = k - (srcEnd - i);

                // 手动填充左侧 (代替Array.Fill)
                for (int p = 0; p < leftPad; p++)
                {
                    window[p] = inputArray[0];
                }

                // 复制核心数据
                if (copyLength > 0)
                {
                    Array.Copy(
                        sourceArray: inputArray,
                        sourceIndex: srcStart,
                        destinationArray: window,
                        destinationIndex: leftPad,
                        length: copyLength
                    );
                }

                // 手动填充右侧 (代替Array.Fill)
                int rightStart = leftPad + copyLength;
                for (int p = 0; p < rightPad; p++)
                {
                    window[rightStart + p] = inputArray[length - 1];
                }

                // 计算中值
                Array.Sort(window);
                output[i] = window[k];
            }

            return output;
        }


        public static double[] MakeMedianFilterReducePoint(ref double[] inputArray, int MedianLens)
        {
            // 参数校验
            if (inputArray == null)
                throw new ArgumentNullException(nameof(inputArray));
            //if (MedianLens <= 0 || MedianLens % 2 == 0)
            //    throw new ArgumentException("MedianLens must be a positive odd number.");
            if (inputArray.Length == 0)
                return new double[0];
            if (MedianLens > inputArray.Length)
                throw new ArgumentException("Window size cannot be larger than array length.");

            int length = inputArray.Length;
            int outputLength = (length + MedianLens - 1) / MedianLens; // 向上取整计算输出数组长度
            double[] output = new double[outputLength];
            //  int k = (MedianLens - 1) / 2; // 中值位置
            int k = MedianLens / 2; // 中值位置

            for (int i = 0; i < length; i += MedianLens) // 步长改为 MedianLens
            {
                double[] window = new double[MedianLens];
                int srcStart = i;
                int srcEnd = Math.Min(i + MedianLens - 1, length - 1);
                int copyLength = srcEnd - srcStart + 1;
                int rightPad = MedianLens - copyLength;

                // 复制有效数据到窗口
                Array.Copy(
                    inputArray,
                    srcStart,
                    window,
                    0,
                    copyLength
                );

                // 右侧越界时填充最后一个元素
                for (int p = 0; p < rightPad; p++)
                {
                    window[copyLength + p] = inputArray[length - 1];
                }

                // 计算中值
                Array.Sort(window);
                int outputIndex = i / MedianLens; // 输出数组索引
                output[outputIndex] = window[k];
            }

            return output;
        }

    }
}
