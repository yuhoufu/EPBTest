using System;

namespace Config
{
    public enum StorageRetentionMode
    {
        Count,
        Unlimited
    }

    /// <summary>项目级数据副本保留策略。</summary>
    public sealed class DataStorageRetentionConfig
    {
        public LatestSnapshotRetentionConfig Latest { get; set; } = new LatestSnapshotRetentionConfig();
    }

    /// <summary>Latest 停止包保留策略；不改变每个停止包内部的圈数。</summary>
    public sealed class LatestSnapshotRetentionConfig
    {
        public const int DefaultRetainCount = 10;
        public const int MaximumRetainCount = 100000;

        public StorageRetentionMode RetentionMode { get; set; } = StorageRetentionMode.Count;
        public int RetainStopPackagesPerChannel { get; set; } = DefaultRetainCount;
    }
}
