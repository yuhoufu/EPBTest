using System.Collections.ObjectModel;
using System.Windows;
using PowerSupplyDebugger.Models;
using PowerSupplyDebugger.Services;

namespace PowerSupplyDebugger.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceConfigurationStore _configurationStore;
    private readonly ILogService _logService;
    private readonly IUserDialogService _dialogs;
    private readonly CancellationTokenSource _pollCancellation = new();
    private Task? _pollTask;
    private PowerSupplyDeviceViewModel? _selectedDevice;
    private bool _isInitialized;
    private bool _isGlobalBusy;
    private string _globalStatus = "正在加载配置…";

    public MainViewModel(
        DeviceConfigurationStore configurationStore,
        ILogService logService,
        IUserDialogService dialogs)
    {
        _configurationStore = configurationStore;
        _logService = logService;
        _dialogs = dialogs;
        _logService.EntryWritten += OnLogEntryWritten;

        ConnectAllCommand = new AsyncRelayCommand(ConnectAllAsync, () => IsInitialized && !IsGlobalBusy);
        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync, () => IsInitialized && !IsGlobalBusy);
        SaveConfigurationCommand = new AsyncRelayCommand(SaveConfigurationAsync, () => IsInitialized && !IsGlobalBusy);
        ClearLogCommand = new RelayCommand(LogEntries.Clear);
    }

    public ObservableCollection<PowerSupplyDeviceViewModel> Devices { get; } = [];
    public ObservableCollection<ScpiLogEntry> LogEntries { get; } = [];

    public PowerSupplyDeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set => SetProperty(ref _selectedDevice, value);
    }

    public bool IsInitialized
    {
        get => _isInitialized;
        private set
        {
            if (SetProperty(ref _isInitialized, value))
            {
                RaiseGlobalCommandStates();
            }
        }
    }

    public bool IsGlobalBusy
    {
        get => _isGlobalBusy;
        private set
        {
            if (SetProperty(ref _isGlobalBusy, value))
            {
                RaiseGlobalCommandStates();
            }
        }
    }

    public string GlobalStatus
    {
        get => _globalStatus;
        private set => SetProperty(ref _globalStatus, value);
    }

    public AsyncRelayCommand ConnectAllCommand { get; }
    public AsyncRelayCommand RefreshAllCommand { get; }
    public AsyncRelayCommand SaveConfigurationCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public async Task InitializeAsync()
    {
        if (IsInitialized)
        {
            return;
        }

        IReadOnlyList<PowerSupplyEndpoint> endpoints;
        try
        {
            endpoints = await _configurationStore.LoadAsync();
        }
        catch (Exception ex)
        {
            _dialogs.ShowWarning(
                "设备配置加载失败",
                $"{ex.Message}\n\n程序将使用四台现场设备的默认配置，保存后会覆盖无效配置。");
            endpoints = PowerSupplyEndpoint.CreateDefaults();
        }

        foreach (var endpoint in endpoints)
        {
            Devices.Add(new PowerSupplyDeviceViewModel(
                endpoint,
                configuredEndpoint => new PswTcpClient(configuredEndpoint, _logService),
                _dialogs));
        }

        SelectedDevice = Devices.FirstOrDefault();
        IsInitialized = true;
        GlobalStatus = "已加载四台设备；连接不会修改任何输出或设定。";
        _pollTask = PollAsync(_pollCancellation.Token);
    }

    private async Task ConnectAllAsync()
    {
        IsGlobalBusy = true;
        GlobalStatus = "正在并行连接四台设备…";
        try
        {
            await Task.WhenAll(Devices.Where(static device => !device.IsConnected)
                .Select(static device => device.ConnectAsync()));
            var online = Devices.Count(static device => device.IsConnected);
            var verified = Devices.Count(static device => device.IsVerifiedPsw);
            GlobalStatus = $"连接完成：在线 {online}/4，身份已验证 {verified}/4。";
        }
        finally
        {
            IsGlobalBusy = false;
        }
    }

    private async Task RefreshAllAsync()
    {
        IsGlobalBusy = true;
        GlobalStatus = "正在刷新全部在线设备…";
        try
        {
            await Task.WhenAll(Devices.Where(static device => device.IsConnected)
                .Select(static device => device.RefreshAsync(false)));
            GlobalStatus = $"刷新完成：{DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            IsGlobalBusy = false;
        }
    }

    private async Task SaveConfigurationAsync()
    {
        if (Devices.Any(static device => device.IsConnected))
        {
            _dialogs.ShowWarning("无法保存", "请先断开全部设备，再修改并保存 IP、端口或终止符。");
            return;
        }

        IsGlobalBusy = true;
        try
        {
            await _configurationStore.SaveAsync(Devices.Select(static device => device.ToEndpoint()));
            GlobalStatus = $"配置已保存：{_configurationStore.ConfigurationPath}";
            _dialogs.ShowInformation("配置已保存", _configurationStore.ConfigurationPath);
        }
        catch (Exception ex)
        {
            GlobalStatus = $"配置保存失败：{ex.Message}";
            _dialogs.ShowError("配置保存失败", ex.Message);
        }
        finally
        {
            IsGlobalBusy = false;
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var refreshTasks = Devices
                    .Where(static device => device.IsConnected && !device.IsBusy)
                    .Select(static device => device.RefreshAsync(true))
                    .ToArray();
                if (refreshTasks.Length > 0)
                {
                    await Task.WhenAll(refreshTasks);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    private void OnLogEntryWritten(object? sender, ScpiLogEntry entry)
    {
        void AddEntry()
        {
            LogEntries.Add(entry);
            while (LogEntries.Count > 2000)
            {
                LogEntries.RemoveAt(0);
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            AddEntry();
        }
        else
        {
            dispatcher.BeginInvoke(AddEntry);
        }
    }

    private void RaiseGlobalCommandStates()
    {
        ConnectAllCommand.RaiseCanExecuteChanged();
        RefreshAllCommand.RaiseCanExecuteChanged();
        SaveConfigurationCommand.RaiseCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        _pollCancellation.Cancel();
        if (_pollTask is not null)
        {
            try
            {
                await _pollTask;
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        await Task.WhenAll(Devices.Select(static device => device.DisposeAsync().AsTask()));
        _logService.EntryWritten -= OnLogEntryWritten;
        _pollCancellation.Dispose();
        if (_logService is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
