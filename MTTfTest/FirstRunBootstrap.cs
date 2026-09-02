using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Config;

namespace MtEmbTest
{
    internal enum FirstRunBootstrapAction
    {
        Continue,
        InstallAndRelaunch,
        ConfigureAndContinue
    }

    internal static class FirstRunBootstrap
    {
        internal const string FormalModeMarkerName = "MTTFTest.UnattendedMode.required";
        internal const string ConfiguredMarkerName = "MTTFTest.FirstRun.configured";
        private const string InstallerRelativePath = @"Deployment\Install-MTTFTest-Unattended.ps1";
        private const string ElevatedWorkerArgument = "--first-run-configure-worker";
        private static readonly string[] RequiredInstalledComponents =
        {
            "MTTFTest.exe",
            "MTTFTest.Watchdog.exe",
            "MTTFTest.SessionAgent.exe",
            "MTTFTest.SafetyAgent.exe",
            "MTTFTest.Watchdog.Protocol.dll"
        };

        internal static bool TryRunElevatedWorker(string[] args)
        {
            if (args == null || args.Length != 5 ||
                !string.Equals(args[0], ElevatedWorkerArgument, StringComparison.OrdinalIgnoreCase))
                return false;
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = BuildInstallerArguments(args[1], args[2], args[3], args[4]),
                    WorkingDirectory = args[3],
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Normal
                }))
                {
                    if (process == null)
                        throw new InvalidOperationException("首次运行配置脚本未能启动。");
                    process.WaitForExit();
                    Environment.ExitCode = process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "首次运行配置失败：\r\n" + ex.GetBaseException().Message,
                    "首次运行配置失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                Environment.ExitCode = 2;
            }
            return true;
        }

        internal static bool PrepareOrExit(string[] args)
        {
            var executable = Path.GetFullPath(
                Process.GetCurrentProcess().MainModule?.FileName ??
                typeof(FirstRunBootstrap).Assembly.Location);
            var directory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory;
            var installRoot = Path.Combine(ResolveProductProgramFiles(), "MTTFTest");
            var installedExecutable = Path.Combine(installRoot, "Current", "MTTFTest.exe");
            var recoveryProcess = HasRecoveryArguments(args);
            var runningFromInstalledDirectory = PathsEqual(executable, installedExecutable);
            var formalMode = runningFromInstalledDirectory ||
                             File.Exists(Path.Combine(directory, FormalModeMarkerName));
            if (formalMode && !recoveryProcess && !runningFromInstalledDirectory &&
                ShouldLaunchInstalledExecutable(executable, installedExecutable))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = installedExecutable,
                    WorkingDirectory = Path.GetDirectoryName(installedExecutable),
                    UseShellExecute = true
                });
                return false;
            }

            var configuredMarkerExists = File.Exists(
                Path.Combine(directory, ConfiguredMarkerName));
            if (runningFromInstalledDirectory && configuredMarkerExists &&
                !RuntimeConfigPaths.Validate(true, out _))
                configuredMarkerExists = false;
            var action = Decide(
                formalMode,
                configuredMarkerExists,
                recoveryProcess,
                runningFromInstalledDirectory);
            if (action == FirstRunBootstrapAction.Continue)
                return recoveryProcess || ValidateRuntimeConfigForStartup(formalMode);

            var installer = Path.Combine(directory, InstallerRelativePath);
            if (!File.Exists(installer))
            {
                MessageBox.Show(
                    "程序缺少首次运行配置组件：" + InstallerRelativePath +
                    "\r\n请重新解压完整程序包。",
                    "首次运行配置失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }

            var mode = action == FirstRunBootstrapAction.InstallAndRelaunch
                ? "Install"
                : "Configure";
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = ElevatedWorkerArgument + " " + Quote(installer) +
                                " " + Quote(mode) + " " + Quote(directory) +
                                " " + Quote(installRoot),
                    WorkingDirectory = directory,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                }))
                {
                    if (process == null)
                        throw new InvalidOperationException("管理员配置进程未能启动。");
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException(
                            "管理员配置未完成，退出代码：" + process.ExitCode + "。");
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                MessageBox.Show(
                    "已取消管理员权限，首次运行配置没有完成。\r\n程序本次不会启动。",
                    "首次运行已取消",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "首次运行配置失败：\r\n" + ex.GetBaseException().Message,
                    "首次运行配置失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }

            if (action == FirstRunBootstrapAction.ConfigureAndContinue)
                return ValidateRuntimeConfigForStartup(formalMode);
            if (!File.Exists(installedExecutable))
            {
                MessageBox.Show(
                    "首次运行配置完成，但没有找到已安装的主程序：\r\n" + installedExecutable,
                    "首次运行配置失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = installedExecutable,
                WorkingDirectory = Path.GetDirectoryName(installedExecutable),
                UseShellExecute = true
            });
            return false;
        }

        internal static FirstRunBootstrapAction Decide(
            bool formalModeMarkerExists,
            bool configuredMarkerExists,
            bool recoveryProcess,
            bool runningFromInstalledDirectory)
        {
            if (!formalModeMarkerExists || recoveryProcess)
                return FirstRunBootstrapAction.Continue;
            if (!runningFromInstalledDirectory)
                return FirstRunBootstrapAction.InstallAndRelaunch;
            return configuredMarkerExists
                ? FirstRunBootstrapAction.Continue
                : FirstRunBootstrapAction.ConfigureAndContinue;
        }

        internal static bool ShouldLaunchInstalledVersion(
            Version sourceVersion,
            Version installedVersion,
            bool installedHealthy)
        {
            return installedHealthy && sourceVersion != null && installedVersion != null &&
                   installedVersion >= sourceVersion;
        }

        private static bool ShouldLaunchInstalledExecutable(
            string sourceExecutable,
            string installedExecutable)
        {
            try
            {
                if (!File.Exists(installedExecutable)) return false;
                var installedDirectory = Path.GetDirectoryName(installedExecutable);
                if (string.IsNullOrWhiteSpace(installedDirectory) ||
                    !File.Exists(Path.Combine(installedDirectory, ConfiguredMarkerName)))
                    return false;
                if (RequiredInstalledComponents.Any(component =>
                        !File.Exists(Path.Combine(installedDirectory, component))))
                    return false;
                var runtimeDirectory = RuntimeConfigPaths.ResolveDirectory(
                    installedDirectory,
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    installed: true);
                if (!RuntimeConfigPaths.ValidateDirectory(
                        runtimeDirectory,
                        RuntimeConfigPaths.RequiredFiles,
                        verifyWritable: true,
                        out _))
                    return false;
                var sourceVersion = AssemblyName.GetAssemblyName(sourceExecutable).Version;
                var installedVersion = AssemblyName.GetAssemblyName(installedExecutable).Version;
                return ShouldLaunchInstalledVersion(
                    sourceVersion,
                    installedVersion,
                    installedHealthy: true);
            }
            catch
            {
                return false;
            }
        }

        private static bool ValidateRuntimeConfigForStartup(bool formalMode)
        {
            if (RuntimeConfigPaths.Validate(true, out var error)) return true;
            MessageBox.Show(
                "运行配置目录不可用，程序不会继续初始化监控窗口。\r\n\r\n" +
                "目录：" + RuntimeConfigPaths.Directory + "\r\n" +
                "原因：" + error +
                (formalMode
                    ? "\r\n\r\n请执行一次“一键修复”，修复过程需要管理员权限；之后仍以普通权限启动。"
                    : "\r\n\r\n开发目录运行时，请补齐相邻 Config 文件并确认该目录可写。"),
                "运行配置检查失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        private static bool HasRecoveryArguments(string[] args)
        {
            return (args ?? Array.Empty<string>()).Any(argument =>
                !string.IsNullOrWhiteSpace(argument) &&
                (argument.StartsWith("--watchdog-", StringComparison.OrdinalIgnoreCase) ||
                 argument.StartsWith("--recovery-", StringComparison.OrdinalIgnoreCase)));
        }

        private static string ResolveProductProgramFiles()
        {
            var directory = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86);
            if (string.IsNullOrWhiteSpace(directory))
                directory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return Path.GetFullPath(directory);
        }

        private static string BuildInstallerArguments(
            string installer,
            string mode,
            string sourceDirectory,
            string installRoot)
        {
            return "-NoProfile -ExecutionPolicy Bypass -File " + Quote(installer) +
                   " -Mode " + Quote(mode) +
                   " -SourceDirectory " + Quote(sourceDirectory) +
                   " -InstallRoot " + Quote(installRoot);
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left ?? string.Empty).TrimEnd('\\', '/'),
                Path.GetFullPath(right ?? string.Empty).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
