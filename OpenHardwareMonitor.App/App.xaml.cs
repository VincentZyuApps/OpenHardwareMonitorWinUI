using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using OpenHardwareMonitor.Core;

namespace OpenHardwareMonitor.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
        UnhandledException += (_, args) => AppLog.Write(args.Exception);
    }

    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(Services.GetRequiredService<MainViewModel>());
        _window.Activate();
    }

    private static IServiceProvider ConfigureServices() => new ServiceCollection()
        .AddSingleton<SettingsStore>()
        .AddSingleton<HardwareMonitorService>()
        .AddSingleton<CsvLoggingService>()
        .AddSingleton<RemoteWebServer>()
        .AddSingleton<MainViewModel>()
        .BuildServiceProvider();
}
