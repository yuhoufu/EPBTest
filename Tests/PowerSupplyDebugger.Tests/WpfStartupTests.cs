using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PowerSupplyDebugger.ViewModels;

namespace PowerSupplyDebugger.Tests;

public sealed class WpfStartupTests
{
    [Fact]
    public void MainWindowCanLoadAllXamlResources()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new App();
                application.InitializeComponent();
                AssertThemeResources(application);
                var window = new MainWindow();
                var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
#pragma warning disable xUnit1031 // Safe here: this dedicated STA thread has no dispatcher synchronization context yet.
                viewModel.InitializeAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                window.Show();
                window.Dispatcher.Invoke(
                    static () => { },
                    DispatcherPriority.ApplicationIdle);
                window.Close();
                application.Shutdown();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF 启动检查超时。");

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void AssertThemeResources(Application application)
    {
        var expectedBrushes = new Dictionary<string, string>
        {
            ["BackgroundBrush"] = "#F3F6FA",
            ["SurfaceBrush"] = "#FFFFFF",
            ["SurfaceRaisedBrush"] = "#F8FAFC",
            ["BorderBrush"] = "#CBD5E1",
            ["BorderStrongBrush"] = "#94A3B8",
            ["PrimaryBrush"] = "#0369A1",
            ["PrimarySoftBrush"] = "#E0F2FE",
            ["TextBrush"] = "#0F172A",
            ["MutedTextBrush"] = "#475569",
            ["DisabledSurfaceBrush"] = "#E2E8F0",
            ["DisabledTextBrush"] = "#64748B",
            ["SuccessBrush"] = "#166534",
            ["SuccessSurfaceBrush"] = "#DCFCE7",
            ["WarningBrush"] = "#92400E",
            ["WarningSurfaceBrush"] = "#FEF3C7",
            ["DangerBrush"] = "#991B1B",
            ["DangerSurfaceBrush"] = "#FEE2E2"
        };

        foreach (var (resourceKey, expectedColorText) in expectedBrushes)
        {
            var brush = Assert.IsType<SolidColorBrush>(application.Resources[resourceKey]);
            var expectedColor = (Color)ColorConverter.ConvertFromString(expectedColorText);
            Assert.Equal(expectedColor, brush.Color);
        }

        Assert.Equal(typeof(Button), Assert.IsType<Style>(application.Resources[typeof(Button)]).TargetType);
        Assert.Equal(typeof(TextBox), Assert.IsType<Style>(application.Resources[typeof(TextBox)]).TargetType);
        Assert.Equal(typeof(GroupBox), Assert.IsType<Style>(application.Resources[typeof(GroupBox)]).TargetType);
        Assert.Equal(typeof(TabItem), Assert.IsType<Style>(application.Resources[typeof(TabItem)]).TargetType);
        Assert.Equal(typeof(Button), Assert.IsType<Style>(application.Resources["SecondaryButton"]).TargetType);
        Assert.Equal(typeof(Button), Assert.IsType<Style>(application.Resources["DangerButton"]).TargetType);
    }
}
