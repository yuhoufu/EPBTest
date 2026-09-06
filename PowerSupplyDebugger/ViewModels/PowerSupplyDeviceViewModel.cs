using System.Globalization;
using PowerSupplyDebugger.Models;
using PowerSupplyDebugger.Services;

namespace PowerSupplyDebugger.ViewModels;

public sealed class PowerSupplyDeviceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Func<PowerSupplyEndpoint, IPswClient> _clientFactory;
    private readonly IUserDialogService _dialogs;
    private IPswClient? _client;
    private string _displayName;
    private string _host;
    private string _portText;
    private string _terminator;
    private bool _isConnected;
    private bool _isBusy;
    private bool _isVerifiedPsw;
    private bool _outputEnabled;
    private bool _protectionTripped;
    private bool _isConstantVoltage;
    private bool _isConstantCurrent;
    private bool _isVoltageLimited;
    private bool _isCurrentLimited;
    private bool _isPowerLimited;
    private string _identity = "尚未连接";
    private string _model = "未识别";
    private string _connectionMessage = "离线";
    private string _lastUpdate = "-";
    private string _controlState = "未查询";
    private string _lastError = string.Empty;
    private double _setVoltage;
    private double _setCurrent;
    private double? _ovp;
    private double? _ocp;
    private double _measuredVoltage;
    private double _measuredCurrent;
    private double _measuredPower;
    private int _operationStatus;
    private int _questionableStatus;
    private double _maxVoltage;
    private double _maxCurrent;
    private double? _minOvp;
    private double? _maxOvp;
    private double? _minOcp;
    private double? _maxOcp;
    private bool _canWriteOvp;
    private bool _canWriteOcp;
    private string _voltageInput = "0";
    private string _currentInput = "0";
    private string _ovpInput = "0";
    private string _ocpInput = "0";
    private string _errorQueueText = "尚未读取。";
    private string _rawCommand = "*IDN?";
    private bool _rawExpectResponse = true;
    private string _rawResponse = string.Empty;

    public PowerSupplyDeviceViewModel(
        PowerSupplyEndpoint endpoint,
        Func<PowerSupplyEndpoint, IPswClient> clientFactory,
        IUserDialogService dialogs)
    {
        Id = endpoint.Id;
        _displayName = endpoint.DisplayName;
        _host = endpoint.Host;
        _portText = endpoint.Port.ToString(CultureInfo.InvariantCulture);
        _terminator = endpoint.Terminator;
        _clientFactory = clientFactory;
        _dialogs = dialogs;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected && !IsBusy);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected && !IsBusy);
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(false), () => IsConnected && !IsBusy);
        ApplyVoltageCommand = new AsyncRelayCommand(ApplyVoltageAsync, CanWriteSetpoints);
        ApplyCurrentCommand = new AsyncRelayCommand(ApplyCurrentAsync, CanWriteSetpoints);
        ApplyOvpCommand = new AsyncRelayCommand(ApplyOvpAsync, () => CanWrite && CanWriteOvp && !IsBusy);
        ApplyOcpCommand = new AsyncRelayCommand(ApplyOcpAsync, () => CanWrite && CanWriteOcp && !IsBusy);
        OutputOnCommand = new AsyncRelayCommand(() => SetOutputAsync(true), () => CanWrite && !OutputEnabled && !IsBusy);
        OutputOffCommand = new AsyncRelayCommand(() => SetOutputAsync(false), () => CanWrite && OutputEnabled && !IsBusy);
        ReadErrorsCommand = new AsyncRelayCommand(ReadErrorsAsync, () => IsConnected && !IsBusy);
        SendRawCommand = new AsyncRelayCommand(SendRawAsync, () => IsConnected && !IsBusy && !string.IsNullOrWhiteSpace(RawCommand));
    }

    public int Id { get; }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public string Host { get => _host; set => SetProperty(ref _host, value); }
    public string PortText { get => _portText; set => SetProperty(ref _portText, value); }
    public string Terminator { get => _terminator; set => SetProperty(ref _terminator, value); }
    public bool IsConnected { get => _isConnected; private set { if (SetProperty(ref _isConnected, value)) RaiseCommandStates(); } }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandStates(); } }
    public bool IsVerifiedPsw { get => _isVerifiedPsw; private set { if (SetProperty(ref _isVerifiedPsw, value)) RaiseCommandStates(); } }
    public bool CanWrite => IsConnected && IsVerifiedPsw;
    public bool OutputEnabled { get => _outputEnabled; private set { if (SetProperty(ref _outputEnabled, value)) { OnPropertyChanged(nameof(OutputStatusText)); RaiseCommandStates(); } } }
    public string OutputStatusText => OutputEnabled ? "输出已开启" : "输出已关闭";
    public bool ProtectionTripped { get => _protectionTripped; private set => SetProperty(ref _protectionTripped, value); }
    public bool IsConstantVoltage { get => _isConstantVoltage; private set => SetProperty(ref _isConstantVoltage, value); }
    public bool IsConstantCurrent { get => _isConstantCurrent; private set => SetProperty(ref _isConstantCurrent, value); }
    public bool IsVoltageLimited { get => _isVoltageLimited; private set => SetProperty(ref _isVoltageLimited, value); }
    public bool IsCurrentLimited { get => _isCurrentLimited; private set => SetProperty(ref _isCurrentLimited, value); }
    public bool IsPowerLimited { get => _isPowerLimited; private set => SetProperty(ref _isPowerLimited, value); }
    public string Identity { get => _identity; private set => SetProperty(ref _identity, value); }
    public string Model { get => _model; private set => SetProperty(ref _model, value); }
    public string ConnectionMessage { get => _connectionMessage; private set => SetProperty(ref _connectionMessage, value); }
    public string LastUpdate { get => _lastUpdate; private set => SetProperty(ref _lastUpdate, value); }
    public string ControlState { get => _controlState; private set => SetProperty(ref _controlState, value); }
    public string LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }
    public double SetVoltage { get => _setVoltage; private set => SetProperty(ref _setVoltage, value); }
    public double SetCurrent { get => _setCurrent; private set => SetProperty(ref _setCurrent, value); }
    public double? Ovp { get => _ovp; private set => SetProperty(ref _ovp, value); }
    public double? Ocp { get => _ocp; private set => SetProperty(ref _ocp, value); }
    public double MeasuredVoltage { get => _measuredVoltage; private set => SetProperty(ref _measuredVoltage, value); }
    public double MeasuredCurrent { get => _measuredCurrent; private set => SetProperty(ref _measuredCurrent, value); }
    public double MeasuredPower { get => _measuredPower; private set => SetProperty(ref _measuredPower, value); }
    public int OperationStatus { get => _operationStatus; private set => SetProperty(ref _operationStatus, value); }
    public int QuestionableStatus { get => _questionableStatus; private set => SetProperty(ref _questionableStatus, value); }
    public double MaxVoltage { get => _maxVoltage; private set => SetProperty(ref _maxVoltage, value); }
    public double MaxCurrent { get => _maxCurrent; private set => SetProperty(ref _maxCurrent, value); }
    public double? MinOvp { get => _minOvp; private set => SetProperty(ref _minOvp, value); }
    public double? MaxOvp { get => _maxOvp; private set => SetProperty(ref _maxOvp, value); }
    public double? MinOcp { get => _minOcp; private set => SetProperty(ref _minOcp, value); }
    public double? MaxOcp { get => _maxOcp; private set => SetProperty(ref _maxOcp, value); }
    public bool CanWriteOvp { get => _canWriteOvp; private set { if (SetProperty(ref _canWriteOvp, value)) RaiseCommandStates(); } }
    public bool CanWriteOcp { get => _canWriteOcp; private set { if (SetProperty(ref _canWriteOcp, value)) RaiseCommandStates(); } }
    public string VoltageInput { get => _voltageInput; set => SetProperty(ref _voltageInput, value); }
    public string CurrentInput { get => _currentInput; set => SetProperty(ref _currentInput, value); }
    public string OvpInput { get => _ovpInput; set => SetProperty(ref _ovpInput, value); }
    public string OcpInput { get => _ocpInput; set => SetProperty(ref _ocpInput, value); }
    public string ErrorQueueText { get => _errorQueueText; private set => SetProperty(ref _errorQueueText, value); }
    public string RawCommand { get => _rawCommand; set { if (SetProperty(ref _rawCommand, value)) RaiseCommandStates(); } }
    public bool RawExpectResponse { get => _rawExpectResponse; set => SetProperty(ref _rawExpectResponse, value); }
    public string RawResponse { get => _rawResponse; private set => SetProperty(ref _rawResponse, value); }

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ApplyVoltageCommand { get; }
    public AsyncRelayCommand ApplyCurrentCommand { get; }
    public AsyncRelayCommand ApplyOvpCommand { get; }
    public AsyncRelayCommand ApplyOcpCommand { get; }
    public AsyncRelayCommand OutputOnCommand { get; }
    public AsyncRelayCommand OutputOffCommand { get; }
    public AsyncRelayCommand ReadErrorsCommand { get; }
    public AsyncRelayCommand SendRawCommand { get; }

    public PowerSupplyEndpoint ToEndpoint()
    {
        if (!int.TryParse(PortText, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            throw new ArgumentException($"{DisplayName} 的端口不是有效整数。");
        }

        var endpoint = new PowerSupplyEndpoint
        {
            Id = Id,
            DisplayName = DisplayName.Trim(),
            Host = Host.Trim(),
            Port = port,
            Terminator = Terminator
        };
        endpoint.Validate();
        return endpoint;
    }

    public async Task ConnectAsync()
    {
        if (IsConnected || IsBusy)
        {
            return;
        }

        IsBusy = true;
        LastError = string.Empty;
        ConnectionMessage = "正在连接…";
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            _client = _clientFactory(ToEndpoint());
            var snapshot = await _client.ConnectAsync();
            ApplySnapshot(snapshot);
            ConnectionMessage = snapshot.IsVerifiedPsw ? "在线 · 身份已验证" : "在线 · 身份未验证（只读）";

            if (snapshot.OutputEnabled)
            {
                _dialogs.ShowWarning(
                    $"{DisplayName} 输出仍处于 ON",
                    $"{DisplayName}（{Host}:{PortText}）连接成功，但设备输出原本就是 ON。\n\n" +
                    $"当前设定：{snapshot.SetVoltage:0.###} V / {snapshot.SetCurrent:0.###} A\n" +
                    $"实测：{snapshot.MeasuredVoltage:0.###} V / {snapshot.MeasuredCurrent:0.###} A\n\n" +
                    "程序没有修改任何设定或输出状态，请按现场安全要求处理。");
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ConnectionMessage = "连接失败";
            IsConnected = false;
            IsVerifiedPsw = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            if (_client is not null)
            {
                await _client.DisconnectAsync();
                await _client.DisposeAsync();
                _client = null;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsConnected = false;
            IsVerifiedPsw = false;
            ConnectionMessage = "离线";
            IsBusy = false;
        }
    }

    public async Task RefreshAsync(bool silent)
    {
        if (_client is null || !IsConnected || IsBusy)
        {
            return;
        }

        IsBusy = !silent;
        try
        {
            ApplySnapshot(await _client.ReadSnapshotAsync());
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (!_client.IsConnected)
            {
                IsConnected = false;
                IsVerifiedPsw = false;
                ConnectionMessage = "连接中断";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyVoltageAsync()
    {
        if (!TryParseInput(VoltageInput, out var requested, "VSET"))
        {
            return;
        }
        if (!ValidateInputRange(requested, 0, MaxVoltage, "VSET", "V"))
        {
            return;
        }

        if (!ConfirmLiveChange("VSET", SetVoltage, requested, "V"))
        {
            return;
        }

        await ExecuteWriteAsync(async client =>
        {
            SetVoltage = await client.SetVoltageAsync(requested);
            VoltageInput = SetVoltage.ToString("0.###", CultureInfo.InvariantCulture);
        });
    }

    private async Task ApplyCurrentAsync()
    {
        if (!TryParseInput(CurrentInput, out var requested, "ISET"))
        {
            return;
        }
        if (!ValidateInputRange(requested, 0, MaxCurrent, "ISET", "A"))
        {
            return;
        }

        if (!ConfirmLiveChange("ISET", SetCurrent, requested, "A"))
        {
            return;
        }

        await ExecuteWriteAsync(async client =>
        {
            SetCurrent = await client.SetCurrentAsync(requested);
            CurrentInput = SetCurrent.ToString("0.###", CultureInfo.InvariantCulture);
        });
    }

    private async Task ApplyOvpAsync()
    {
        if (!TryParseInput(OvpInput, out var requested, "OVP"))
        {
            return;
        }
        if (!MinOvp.HasValue || !MaxOvp.HasValue ||
            !ValidateInputRange(requested, MinOvp.Value, MaxOvp.Value, "OVP", "V"))
        {
            return;
        }

        if (!ConfirmLiveChange("OVP", Ovp ?? 0, requested, "V"))
        {
            return;
        }

        await ExecuteWriteAsync(async client =>
        {
            Ovp = await client.SetOvpAsync(requested);
            OvpInput = Ovp.Value.ToString("0.###", CultureInfo.InvariantCulture);
        });
    }

    private async Task ApplyOcpAsync()
    {
        if (!TryParseInput(OcpInput, out var requested, "OCP"))
        {
            return;
        }
        if (!MinOcp.HasValue || !MaxOcp.HasValue ||
            !ValidateInputRange(requested, MinOcp.Value, MaxOcp.Value, "OCP", "A"))
        {
            return;
        }

        if (!ConfirmLiveChange("OCP", Ocp ?? 0, requested, "A"))
        {
            return;
        }

        await ExecuteWriteAsync(async client =>
        {
            Ocp = await client.SetOcpAsync(requested);
            OcpInput = Ocp.Value.ToString("0.###", CultureInfo.InvariantCulture);
        });
    }

    private async Task SetOutputAsync(bool enabled)
    {
        if (enabled && !_dialogs.Confirm(
                $"确认开启 {DisplayName} 输出",
                $"即将对 {DisplayName}（{Host}:{PortText}）执行 OUTP ON。\n\n" +
                $"VSET：{SetVoltage:0.###} V\nISET：{SetCurrent:0.###} A\n\n" +
                "请确认负载、线缆、极性和保护参数均已检查。",
                true))
        {
            return;
        }

        await ExecuteWriteAsync(async client => OutputEnabled = await client.SetOutputAsync(enabled));
    }

    private async Task ReadErrorsAsync()
    {
        if (_client is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var errors = await _client.ReadErrorQueueAsync();
            ErrorQueueText = string.Join(Environment.NewLine, errors);
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SendRawAsync()
    {
        if (_client is null)
        {
            return;
        }

        var command = RawCommand.Trim();
        if (!RawExpectResponse && !_dialogs.Confirm(
                "确认发送原始写命令",
                $"目标：{DisplayName}（{Host}:{PortText}）\n命令：{command}\n\n" +
                "该命令可能立即改变设备输出或配置。程序只会发送一次，不会自动重试。",
                true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            RawResponse = await _client.SendRawAsync(command, RawExpectResponse) ?? "命令已发送（未等待响应）。";
            LastError = string.Empty;
            if (!RawExpectResponse)
            {
                ApplySnapshot(await _client.ReadSnapshotAsync());
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            RawResponse = $"错误：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteWriteAsync(Func<IPswClient, Task> action)
    {
        if (_client is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action(_client);
            ApplySnapshot(await _client.ReadSnapshotAsync());
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _dialogs.ShowError($"{DisplayName} 操作失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryParseInput(string text, out double value, string name)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return true;
        }

        _dialogs.ShowError("参数格式错误", $"{name} 必须是有效数字。");
        return false;
    }

    private bool ConfirmLiveChange(string name, double current, double requested, string unit)
    {
        if (!OutputEnabled)
        {
            return true;
        }

        return _dialogs.Confirm(
            $"确认带电修改 {name}",
            $"{DisplayName} 当前 OUTP 为 ON，写入后会立即影响输出。\n\n" +
            $"当前值：{current:0.###} {unit}\n新值：{requested:0.###} {unit}\n" +
            $"允许范围：{GetRangeText(name, unit)}\n\n" +
            "确认继续写入并立即回读？",
            true);
    }

    private bool ValidateInputRange(double value, double minimum, double maximum, string name, string unit)
    {
        if (value >= minimum && value <= maximum)
        {
            return true;
        }

        _dialogs.ShowError("参数超出范围", $"{name} 必须在 {minimum:0.###}～{maximum:0.###} {unit} 范围内。");
        return false;
    }

    private string GetRangeText(string name, string unit) => name switch
    {
        "VSET" => $"0～{MaxVoltage:0.###} {unit}",
        "ISET" => $"0～{MaxCurrent:0.###} {unit}",
        "OVP" when MinOvp.HasValue && MaxOvp.HasValue => $"{MinOvp:0.###}～{MaxOvp:0.###} {unit}",
        "OCP" when MinOcp.HasValue && MaxOcp.HasValue => $"{MinOcp:0.###}～{MaxOcp:0.###} {unit}",
        _ => "待设备回读确认"
    };

    private bool CanWriteSetpoints() => CanWrite && !IsBusy;

    private void ApplySnapshot(PowerSupplySnapshot snapshot)
    {
        IsConnected = snapshot.IsConnected;
        IsVerifiedPsw = snapshot.IsVerifiedPsw;
        Identity = string.IsNullOrWhiteSpace(snapshot.Identity) ? "无身份响应" : snapshot.Identity;
        Model = snapshot.Capabilities.Model;
        OutputEnabled = snapshot.OutputEnabled;
        SetVoltage = snapshot.SetVoltage;
        SetCurrent = snapshot.SetCurrent;
        Ovp = snapshot.Ovp;
        Ocp = snapshot.Ocp;
        MeasuredVoltage = snapshot.MeasuredVoltage;
        MeasuredCurrent = snapshot.MeasuredCurrent;
        MeasuredPower = snapshot.MeasuredPower;
        ProtectionTripped = snapshot.ProtectionTripped;
        OperationStatus = snapshot.OperationStatus;
        QuestionableStatus = snapshot.QuestionableStatus;
        IsConstantVoltage = snapshot.IsConstantVoltage;
        IsConstantCurrent = snapshot.IsConstantCurrent;
        IsVoltageLimited = snapshot.IsVoltageLimited;
        IsCurrentLimited = snapshot.IsCurrentLimited;
        IsPowerLimited = snapshot.IsPowerLimited;
        ControlState = snapshot.ControlState;
        MaxVoltage = snapshot.Capabilities.MaxVoltage;
        MaxCurrent = snapshot.Capabilities.MaxCurrent;
        MinOvp = snapshot.Capabilities.MinOvp;
        MaxOvp = snapshot.Capabilities.MaxOvp;
        MinOcp = snapshot.Capabilities.MinOcp;
        MaxOcp = snapshot.Capabilities.MaxOcp;
        CanWriteOvp = snapshot.Capabilities.CanWriteOvp;
        CanWriteOcp = snapshot.Capabilities.CanWriteOcp;
        VoltageInput = SetVoltage.ToString("0.###", CultureInfo.InvariantCulture);
        CurrentInput = SetCurrent.ToString("0.###", CultureInfo.InvariantCulture);
        if (Ovp.HasValue)
        {
            OvpInput = Ovp.Value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        if (Ocp.HasValue)
        {
            OcpInput = Ocp.Value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        LastUpdate = snapshot.Timestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        OnPropertyChanged(nameof(CanWrite));
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanWrite));
        ConnectCommand?.RaiseCanExecuteChanged();
        DisconnectCommand?.RaiseCanExecuteChanged();
        RefreshCommand?.RaiseCanExecuteChanged();
        ApplyVoltageCommand?.RaiseCanExecuteChanged();
        ApplyCurrentCommand?.RaiseCanExecuteChanged();
        ApplyOvpCommand?.RaiseCanExecuteChanged();
        ApplyOcpCommand?.RaiseCanExecuteChanged();
        OutputOnCommand?.RaiseCanExecuteChanged();
        OutputOffCommand?.RaiseCanExecuteChanged();
        ReadErrorsCommand?.RaiseCanExecuteChanged();
        SendRawCommand?.RaiseCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }
}
