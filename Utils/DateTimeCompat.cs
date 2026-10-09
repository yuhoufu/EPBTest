using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Utils
{
    /// <summary>
    /// 兼容层：为 .NET Framework 等平台提供 UnixEpoch 与 Unix ms 的工具方法。
    /// 在 .NET Core / .NET5+ 上使用系统实现也同样工作；在旧框架上则由此类实现。
    /// </summary>
    public static class DateTimeCompat
    {
        /// <summary>
        /// Unix epoch (1970-01-01T00:00:00Z) 的 DateTime 表示（UTC）。
        /// </summary>
        public static DateTime UnixEpoch => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// 把 UTC DateTime 转为 Unix 毫秒（long）。
        /// </summary>
        public static long ToUnixTimeMilliseconds(DateTime utcDateTime)
        {
            if (utcDateTime.Kind != DateTimeKind.Utc) utcDateTime = utcDateTime.ToUniversalTime();
            return (long)(utcDateTime - UnixEpoch).TotalMilliseconds;
        }

        /// <summary>
        /// 从 Unix 毫秒恢复 UTC DateTime。
        /// </summary>
        public static DateTime FromUnixTimeMilliseconds(long ms) => UnixEpoch.AddMilliseconds(ms);

        /// <summary>
        /// 辅助：如果你想用更接近 DateTimeOffset 的调用样式：
        /// DateTimeCompat.ToUnixTimeMilliseconds(DateTime.UtcNow)
        /// </summary>
    }
}
