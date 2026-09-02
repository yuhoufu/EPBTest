using System;
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
            return passed;
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
