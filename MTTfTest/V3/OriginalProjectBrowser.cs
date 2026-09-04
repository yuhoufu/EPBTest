using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    // Read-only browsing. The EngineHost revalidates the complete XML and both
    // file hashes at preparation; browsing neither selects a project nor writes it.
    internal static class OriginalProjectBrowser
    {
        private static int _reading;
        internal const int MaximumProjectBytes = 1024 * 1024;

        internal static Task<string[]> ListAsync(string root, CancellationToken token) => ReadAsync(cancel =>
        {
            root = Path.GetFullPath(root);
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("仅支持本机项目目录。");
            var result = new List<string>(); var count = 0;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                cancel.ThrowIfCancellationRequested();
                if (++count > 512) throw new InvalidDataException("目录超过 512 项，请选择更小的项目存储目录。");
                if (Path.GetFileName(directory).StartsWith(".v3-create-", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(directory).StartsWith(".v3-reset-", StringComparison.OrdinalIgnoreCase)) continue;
                var path = Path.Combine(directory, "Config", "TestConfig.xml");
                if (ProjectSwitchPlan.IsProjectConfigurationPath(path) && File.Exists(path)) result.Add(path);
            }
            return result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }, token);

        internal static Task<ProjectSwitchRequest> PrepareRequestAsync(EngineUiSnapshot source, string targetPath, CancellationToken token)
        {
            if (source?.IsStructurallyValid() != true || source.ProjectSelection?.IsStructurallyValid() != true ||
                source.TestConfiguration == null || !ProjectSwitchPlan.IsProjectConfigurationPath(targetPath) ||
                string.Equals(source.ProjectSelection.ConfigurationPath, targetPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("项目选择或源配置无效，请重新读取后台配置。");
            var request = new ProjectSwitchRequest
            {
                EngineInstanceId = source.Engine.EngineInstanceId, BaseSelectionRevision = source.ProjectSelection.Revision,
                BaseConfigurationRevision = source.ConfigurationRevision, BaseConfigurationSha256 = source.ConfigurationSha256,
                SourceProjectFileSha256 = source.ProjectSelection.ProjectFileSha256, TargetConfigurationPath = targetPath
            };
            return ReadAsync(cancel =>
            {
                using (var file = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (file.Length <= 0 || file.Length > MaximumProjectBytes) throw new InvalidDataException("项目配置为空或超过 1 MB。");
                    var bytes = new byte[(int)file.Length]; var read = 0;
                    while (read < bytes.Length)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var count = file.Read(bytes, read, Math.Min(16384, bytes.Length - read));
                        if (count == 0) throw new EndOfStreamException("项目配置读取中断。");
                        read += count;
                    }
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        request.TargetProjectFileSha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                }
                cancel.ThrowIfCancellationRequested();
                return request;
            }, token);
        }

        internal static Task<ProjectSwitchRequest> PrepareCreationRequestAsync(EngineUiSnapshot source,
            EngineTestConfiguration configuration, CancellationToken token)
        {
            if (source?.IsStructurallyValid() != true || source.ProjectSelection?.IsStructurallyValid() != true ||
                source.TestConfiguration == null || configuration?.IsStructurallyValid() != true)
                throw new InvalidOperationException("新项目或源配置无效，请重新读取后台配置。");
            var request = new ProjectSwitchRequest
            {
                EngineInstanceId = source.Engine.EngineInstanceId, BaseSelectionRevision = source.ProjectSelection.Revision,
                BaseConfigurationRevision = source.ConfigurationRevision, BaseConfigurationSha256 = source.ConfigurationSha256,
                SourceProjectFileSha256 = source.ProjectSelection.ProjectFileSha256,
                TargetConfigurationPath = Path.Combine(configuration.StoreDir, configuration.TestName, "Config", "TestConfig.xml"),
                Creation = new ProjectCreationRequest { Configuration = configuration.Clone() }
            };
            if (!request.IsStructurallyValid()) throw new InvalidOperationException("新项目名称或路径无效，禁止覆盖已有项目。");
            return ReadAsync(cancel =>
            {
                cancel.ThrowIfCancellationRequested();
                if (!Directory.Exists(request.Creation.Configuration.StoreDir))
                    throw new DirectoryNotFoundException("请选择已存在的本机项目存储路径。");
                var directory = Path.GetDirectoryName(Path.GetDirectoryName(request.TargetConfigurationPath));
                if (Directory.Exists(directory) || File.Exists(directory))
                    throw new IOException("同名项目目录已存在，不得新建覆盖。请从列表选择已有项目，或填写不同名称。");
                cancel.ThrowIfCancellationRequested();
                return request; // Read-only preflight; EngineHost checks again at publication.
            }, token);
        }

        internal static ProjectSwitchRequest PrepareResetRequest(EngineUiSnapshot source, EngineTestConfiguration configuration)
        {
            if (source?.IsStructurallyValid() != true || source.ProjectSelection?.IsStructurallyValid() != true ||
                source.TestConfiguration == null || configuration?.IsStructurallyValid() != true ||
                configuration.TestName != source.TestConfiguration.TestName || configuration.StoreDir != source.TestConfiguration.StoreDir)
                throw new InvalidOperationException("只能清零当前项目；请重新读取后台配置。");
            var request = new ProjectSwitchRequest
            {
                EngineInstanceId = source.Engine.EngineInstanceId, BaseSelectionRevision = source.ProjectSelection.Revision,
                BaseConfigurationRevision = source.ConfigurationRevision, BaseConfigurationSha256 = source.ConfigurationSha256,
                SourceProjectFileSha256 = source.ProjectSelection.ProjectFileSha256,
                TargetConfigurationPath = source.ProjectSelection.ConfigurationPath, TargetProjectFileSha256 = source.ProjectSelection.ProjectFileSha256,
                Reset = new ProjectResetRequest { Configuration = configuration.Clone() }
            };
            if (!request.IsStructurallyValid()) throw new InvalidOperationException("清零配置身份无效，未提交事务。");
            return request;
        }

        private static async Task<T> ReadAsync<T>(Func<CancellationToken, T> read, CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) throw new IOException("上一项目目录读取尚未结束，请稍后重试。");
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var readToken = cancel.Token;
                var work = Task.Run(() =>
                {
                    try { readToken.ThrowIfCancellationRequested(); return read(readToken); }
                    finally { Interlocked.Exchange(ref _reading, 0); }
                });
                if (await Task.WhenAny(work, Task.Delay(3000, token)).ConfigureAwait(false) != work)
                {
                    cancel.Cancel();
                    _ = work.ContinueWith(task => { var ignored = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("项目目录读取超时；未发送切换命令。");
                }
                return await work.ConfigureAwait(false);
            }
        }
    }
}
