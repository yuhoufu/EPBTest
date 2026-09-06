using System;
using System.IO;
using Config;
using MtEmbTest;

namespace AdaptiveControlTests
{
    internal static class FirstRunBootstrapTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            AssertEqual(
                FirstRunBootstrapAction.Continue,
                FirstRunBootstrap.Decide(false, false, false, false),
                "开发包不触发首次安装");
            passed++;
            AssertEqual(
                FirstRunBootstrapAction.InstallAndRelaunch,
                FirstRunBootstrap.Decide(true, false, false, false),
                "正式包外部 EXE 首次运行自动安装");
            passed++;
            AssertEqual(
                FirstRunBootstrapAction.ConfigureAndContinue,
                FirstRunBootstrap.Decide(true, false, false, true),
                "安装目录缺少配置时只修复运行环境");
            passed++;
            AssertEqual(
                FirstRunBootstrapAction.Continue,
                FirstRunBootstrap.Decide(true, true, false, true),
                "已配置安装目录直接运行");
            passed++;
            AssertEqual(
                FirstRunBootstrapAction.Continue,
                FirstRunBootstrap.Decide(true, false, true, false),
                "恢复子进程不得触发安装");
            passed++;
            Assert(
                FirstRunBootstrap.ShouldLaunchInstalledVersion(
                    new Version(2, 14, 2, 0),
                    new Version(2, 14, 2, 0),
                    installedHealthy: true),
                "同版本外部程序应转到健康的已安装版本");
            passed++;
            Assert(
                FirstRunBootstrap.ShouldLaunchInstalledVersion(
                    new Version(2, 14, 1, 0),
                    new Version(2, 14, 2, 0),
                    installedHealthy: true),
                "旧版本外部程序不应覆盖较新的已安装版本");
            passed++;
            Assert(
                !FirstRunBootstrap.ShouldLaunchInstalledVersion(
                    new Version(2, 14, 3, 0),
                    new Version(2, 14, 2, 0),
                    installedHealthy: true),
                "新版本外部程序应进入升级");
            passed++;
            Assert(
                !FirstRunBootstrap.ShouldLaunchInstalledVersion(
                    new Version(2, 14, 2, 0),
                    new Version(2, 14, 2, 0),
                    installedHealthy: false),
                "安装不健康时不得静默重定向");
            passed++;
            var application = Path.Combine("D:\\", "Apps", "MTTFTest", "Current");
            var common = Path.Combine("D:\\", "ProgramData-Test");
            Assert(
                RuntimeConfigPaths.ResolveDirectory(application, common, installed: true) ==
                Path.Combine(common, "MTTFTest", "Config"),
                "正式安装没有解析到ProgramData运行配置");
            Assert(
                RuntimeConfigPaths.ResolveDirectory(application, common, installed: false) ==
                Path.Combine(application, "Config"),
                "开发运行没有保留相邻Config目录");
            passed++;
            RuntimeConfigValidationAcceptsWritableCompleteDirectory();
            passed++;
            return passed;
        }

        private static void RuntimeConfigValidationAcceptsWritableCompleteDirectory()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest-RuntimeConfig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                foreach (var name in RuntimeConfigPaths.RequiredFiles)
                    File.WriteAllText(Path.Combine(root, name), "<Config />");
                Assert(RuntimeConfigPaths.ValidateDirectory(
                        root,
                        RuntimeConfigPaths.RequiredFiles,
                        verifyWritable: true,
                        out var error),
                    "完整可写的运行配置目录校验失败：" + error);
                File.Delete(Path.Combine(root, "TestConfig.xml"));
                Assert(!RuntimeConfigPaths.ValidateDirectory(
                        root,
                        RuntimeConfigPaths.RequiredFiles,
                        verifyWritable: false,
                        out _),
                    "缺少TestConfig.xml的运行配置错误通过校验");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertEqual(
            FirstRunBootstrapAction expected,
            FirstRunBootstrapAction actual,
            string message)
        {
            if (expected != actual)
                throw new InvalidOperationException(
                    message + $" Expected={expected};Actual={actual}");
        }
    }
}
