using System;

namespace Utils
{
    /// <summary>
    ///     数值工具方法集合。
    ///     提供 <c>Clamp</c> 方法，用于在没有 <see cref="Math.Clamp(double,double,double)" /> 的框架中使用。
    /// </summary>
    public static class NumericUtils
    {
        /// <summary>
        ///     将 <paramref name="value" /> 限制在 <paramref name="min" /> 与 <paramref name="max" /> 之间（泛型实现）。
        /// </summary>
        /// <typeparam name="T">实现了 <see cref="IComparable{T}" /> 的类型，例如 <see cref="int" />, <see cref="double" /> 等。</typeparam>
        /// <param name="value">要限制的值。</param>
        /// <param name="min">允许的最小值（含）。</param>
        /// <param name="max">允许的最大值（含）。</param>
        /// <returns>
        ///     如果 <paramref name="value" /> 小于 <paramref name="min" /> 则返回 <paramref name="min" />；大于 <paramref name="max" />
        ///     则返回 <paramref name="max" />；否则返回 <paramref name="value" /> 本身。
        /// </returns>
        /// <exception cref="ArgumentNullException">
        ///     当 <paramref name="value" />, <paramref name="min" /> 或 <paramref name="max" />
        ///     为 null（对引用类型）时抛出。
        /// </exception>
        public static T Clamp<T>(T value, T min, T max) where T : IComparable<T>
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (min == null) throw new ArgumentNullException(nameof(min));
            if (max == null) throw new ArgumentNullException(nameof(max));

            // 如果 min > max，交换它们（更健壮）
            if (min.CompareTo(max) > 0)
            {
                (min, max) = (max, min);
            }

            if (value.CompareTo(min) < 0) return min;
            if (value.CompareTo(max) > 0) return max;
            return value;
        }

        /// <summary>
        ///     将 <paramref name="value" /> 限制在 <paramref name="min" /> 与 <paramref name="max" /> 之间（double 专用）。
        ///     使用 Math.Min/Max 保持高效。
        /// </summary>
        /// <param name="value">要限制的 double 值。</param>
        /// <param name="min">最小值。</param>
        /// <param name="max">最大值。</param>
        /// <returns>被限制后的值。</returns>
        public static double Clamp(double value, double min, double max)
        {
            if (min > max) // 更健壮：若传参反了就交换
            {
                (min, max) = (max, min);
            }

            return Math.Max(min, Math.Min(max, value));
        }

        /// <summary>
        ///     将 <paramref name="value" /> 限制在 <paramref name="min" /> 与 <paramref name="max" /> 之间（int 专用）。
        /// </summary>
        /// <param name="value">要限制的 int 值。</param>
        /// <param name="min">最小值。</param>
        /// <param name="max">最大值。</param>
        /// <returns>被限制后的值。</returns>
        public static int Clamp(int value, int min, int max)
        {
            if (min > max)
            {
                (min, max) = (max, min);
            }

            return Math.Max(min, Math.Min(max, value));
        }
    }
}