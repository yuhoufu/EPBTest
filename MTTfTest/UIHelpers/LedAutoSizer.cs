using Sunny.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MTEmbTest.UIHelpers
{
    public static class LedAutoSizer
    {
        /// <summary>
        /// 让 UILedDisplay 的总宽度始终占父容器宽度的某个比例（默认 95%），
        /// 通过反解公式自动计算 IntervalOn；IntervalIn 默认固定为 1。
        /// </summary>
        /// <param name="led">UILedDisplay 控件</param>
        /// <param name="parent">父容器（通常是 led.Parent）</param>
        /// <param name="widthRatio">目标宽度比例，0~1</param>
        /// <param name="g">IntervalIn（亮块水平间距），默认 1</param>
        /// <param name="blocksPerChar">单字符水平方向的亮块列数，默认 5</param>
        public static void ResizeLedToParentWidth(UILedDisplay led, Control parent,
                                                  double widthRatio = 0.95,
                                                  int g = 1,
                                                  int blocksPerChar = 5)
        {
            if (led == null || parent == null || parent.ClientSize.Width <= 0) return;

            // 1) 目标宽度（向下取整，避免超界）
            int targetW = (int)Math.Floor(parent.ClientSize.Width * widthRatio);
            if (targetW <= 0) return;

            // 2) 根据目标宽度反解 s=IntervalOn
            int s = SolveIntervalOn(targetW, g, led.CharCount, blocksPerChar);

            // 3) 安全约束（最小 1 像素；可按需要给最大值）
            if (s < 1) s = 1;

            // 4) 回写属性：间距/亮块大小 + 实际宽度（按当前 s 重新正算）
            led.IntervalIn = g;
            led.IntervalOn = s;

            int actualW = RecalcWidth(g, s, led.CharCount, blocksPerChar);
            led.Width = actualW;

            // 5) 可选：让 LED 在父容器中居中（水平）
            int x = Math.Max(0, (parent.ClientSize.Width - led.Width) / 2);
            led.Left = x;

            // 也可选择使用 Anchor：led.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            // 如果使用 Anchor 拉伸，就不建议手动设置 Led.Width/Left，而是只更新 IntervalOn/IntervalIn。
        }

        /// <summary>反解 IntervalOn（s）。W = g*(1+K) + s*(2+K) + 4。</summary>
        private static int SolveIntervalOn(int targetWidth, int g, int C, int B)
        {
            if (C <= 0) C = 1;
            if (B <= 0) B = 5;

            int K = C * (B + 1) - 1; // K = C*(B+1)-1
                                     // s = (W - g*(1+K) - 4) / (2+K)
            double s = (targetWidth - g * (1 + K) - 4.0) / (2 + K);
            return (int)Math.Floor(s); // 取整保证不超宽（更稳妥）
        }

        /// <summary>正向计算给定 g/s/C/B 时的 LED 总宽度。</summary>
        private static int RecalcWidth(int g, int s, int C, int B)
        {
            int K = C * (B + 1) - 1;
            int W = g * (1 + K) + s * (2 + K) + 4;
            return W;
        }
    }
}
