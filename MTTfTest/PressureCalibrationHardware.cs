using System;
using Config;
using Controller;
using IO.NI;
using AppLogger = Config.IAppLogger;

namespace MtEmbTest
{
    /// <summary>设置窗口压力校正页使用的 NI 硬件会话。</summary>
    internal sealed class PressureCalibrationHardware : IPressureCalibrationHardware
    {
        private readonly DoController _do;
        private readonly AoController _ao;
        private readonly TwoDeviceAiAcquirer _acquirer;
        private bool _disposed;

        public PressureCalibrationHardware(
            GlobalConfig config,
            AppLogger logger,
            string applicationDirectory,
            DaqRuntimeSettings runtimeSettings = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (config.DO == null) throw new InvalidOperationException("DO 配置为空，无法开始压力校正。");
            if (config.AO == null) throw new InvalidOperationException("AO 配置为空，无法开始压力校正。");
            runtimeSettings = runtimeSettings ?? DaqRuntimeSettings.Load(
                System.Configuration.ConfigurationManager.AppSettings);

            DoController doController = null;
            AoController aoController = null;
            TwoDeviceAiAcquirer acquirer = null;
            try
            {
                doController = new DoController(config.DO, logger);
                if (!doController.Initialize())
                    throw new InvalidOperationException("压力 DO 初始化失败。");

                aoController = new AoController(config.AO, logger);
                if (!doController.AllOff())
                    throw new InvalidOperationException("无法确认全部 DO 已关闭，拒绝开始压力校正。");
                aoController.ResetAll();
                var aiPath = System.IO.Path.Combine(
                    string.IsNullOrWhiteSpace(applicationDirectory)
                        ? Environment.CurrentDirectory
                        : applicationDirectory,
                    "Config",
                    "AIConfig.xml");
                var aiConfig = AiConfigLoader.Load(aiPath);
                acquirer = new TwoDeviceAiAcquirer(
                    aiConfig,
                    runtimeSettings.SampleRateHz,
                    runtimeSettings.SamplesPerChannel,
                    10,
                    logger);
                acquirer.Start();

                _do = doController;
                _ao = aoController;
                _acquirer = acquirer;
            }
            catch
            {
                try { acquirer?.Dispose(); } catch { }
                try { doController?.AllOff(); } catch { }
                try { aoController?.ResetAll(); } catch { }
                try { doController?.Dispose(); } catch { }
                try { aoController?.Dispose(); } catch { }
                throw;
            }
        }

        public bool SetPressureDo(int hydraulicId, bool enabled)
        {
            ThrowIfDisposed();
            return _do.SetPressure(hydraulicId, enabled);
        }

        public AoWriteResult WritePressure(string deviceName, double pressureBar)
        {
            ThrowIfDisposed();
            return _ao.WritePressureDetailed(deviceName, pressureBar);
        }

        public PressureSample ReadPressureSample(int hydraulicId)
        {
            ThrowIfDisposed();
            return _acquirer.ReadPressureSample(hydraulicId);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _do?.AllOff(); } catch { }
            try { _ao?.ResetAll(); } catch { }
            try { _acquirer?.Dispose(); } catch { }
            try { _do?.Dispose(); } catch { }
            try { _ao?.Dispose(); } catch { }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PressureCalibrationHardware));
        }
    }
}
