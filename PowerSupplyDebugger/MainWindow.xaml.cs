using System.Windows;
using PowerSupplyDebugger.Services;
using PowerSupplyDebugger.ViewModels;

namespace PowerSupplyDebugger;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        var logService = new FileLogService();
        _viewModel = new MainViewModel(
            new DeviceConfigurationStore(),
            logService,
            new MessageBoxDialogService());
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        await _viewModel.DisposeAsync();
    }
}
