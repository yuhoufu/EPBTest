using System;
using System.IO;
using System.Reflection;

namespace AdaptiveControlTests
{
    /// <summary>
    /// 部署换代回归：解除授权路径不得凭空写出带默认 SchemaVersion=1 的空壳检查点，
    /// 也不得把旧版本号原样带回盘，否则换代校验会把空壳当不支持的旧格式拒绝安装。
    /// </summary>
    internal static class CheckpointDisarmInvariantsTests
    {
        private const string StoreTypeName = "MTEmbTest.UnattendedRunCheckpointStore";
        private const BindingFlags StoreFlags = BindingFlags.NonPublic | BindingFlags.Static;

        internal static int RunAll()
        {
            DisarmWithoutExistingCheckpointWritesNothing();
            DisarmUpgradesLegacySchemaBeforeSaving();
            Console.WriteLine("PASS 检查点解除授权 2/2 空态不落盘与Schema单调");
            return 2;
        }

        private static void DisarmWithoutExistingCheckpointWritesNothing()
        {
            using (var scope = RedirectStorePaths())
            {
                InvokeDisarm("MonitorClosing");
                Assert(!File.Exists(scope.CheckpointPath), "无检查点时Disarm凭空写出检查点文件");
                Assert(!File.Exists(scope.CheckpointPath + ".bak"), "无检查点时Disarm凭空写出备份");
                Assert(!File.Exists(scope.AuditPath), "无检查点时Disarm凭空写出审计文件");
            }
        }

        private static void DisarmUpgradesLegacySchemaBeforeSaving()
        {
            using (var scope = RedirectStorePaths())
            {
                File.WriteAllText(
                    scope.CheckpointPath,
                    "{\"SchemaVersion\":1,\"Revision\":3,\"Armed\":true,\"RunId\":\"legacyrunid\"," +
                    "\"LastReason\":\"LegacyV1\",\"UpdatedUtc\":\"2026-09-05T08:00:00.0000000Z\"}");
                InvokeDisarm("MonitorClosing");
                var saved = File.ReadAllText(scope.CheckpointPath);
                Assert(saved.Contains("\"SchemaVersion\":6"), "解除授权落盘未盖当前Schema版本：" + saved);
                Assert(saved.Contains("\"Armed\":false"), "解除授权后检查点仍处于授权状态");
            }
        }

        private static void InvokeDisarm(string reason)
        {
            var store = typeof(MTEmbTest.FrmEpbMainMonitor).Assembly.GetType(StoreTypeName, true);
            store.GetMethod("Disarm", StoreFlags)
                .Invoke(null, new object[] { reason });
        }

        private static StorePathScope RedirectStorePaths()
        {
            var store = typeof(MTEmbTest.FrmEpbMainMonitor).Assembly.GetType(StoreTypeName, true);
            return new StorePathScope(
                store.GetField("CheckpointPath", StoreFlags),
                store.GetField("CheckpointBackupPath", StoreFlags),
                store.GetField("CheckpointAuditPath", StoreFlags));
        }

        private sealed class StorePathScope : IDisposable
        {
            internal string CheckpointPath { get; }
            internal string AuditPath { get; }

            private readonly FieldInfo _checkpoint;
            private readonly FieldInfo _backup;
            private readonly FieldInfo _audit;
            private readonly string _originalCheckpoint;
            private readonly string _originalBackup;
            private readonly string _originalAudit;
            private readonly string _root;

            internal StorePathScope(FieldInfo checkpoint, FieldInfo backup, FieldInfo audit)
            {
                _checkpoint = checkpoint;
                _backup = backup;
                _audit = audit;
                _originalCheckpoint = (string)checkpoint.GetValue(null);
                _originalBackup = (string)backup.GetValue(null);
                _originalAudit = (string)audit.GetValue(null);

                _root = Path.Combine(Path.GetTempPath(), "EPB-Disarm-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_root);
                CheckpointPath = Path.Combine(_root, "unattended-run-checkpoint.json");
                AuditPath = Path.Combine(_root, "unattended-run-checkpoint.audit.jsonl");
                checkpoint.SetValue(null, CheckpointPath);
                backup.SetValue(null, CheckpointPath + ".bak");
                audit.SetValue(null, AuditPath);
                // 重定向必须生效，否则后续断言会污染真实用户状态。
                Assert((string)checkpoint.GetValue(null) == CheckpointPath, "检查点路径重定向未生效");
            }

            public void Dispose()
            {
                try
                {
                    _checkpoint.SetValue(null, _originalCheckpoint);
                    _backup.SetValue(null, _originalBackup);
                    _audit.SetValue(null, _originalAudit);
                    if (Directory.Exists(_root)) Directory.Delete(_root, true);
                }
                catch { }
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("检查点解除授权: " + message);
        }
    }
}
