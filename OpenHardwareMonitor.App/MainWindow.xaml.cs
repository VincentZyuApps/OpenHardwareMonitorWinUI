using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using OpenHardwareMonitor.Core;
using Windows.Graphics;
using WinUIEx;

namespace OpenHardwareMonitor.App;

public sealed partial class MainWindow : WindowEx
{
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly Dictionary<string, HardwareInfoWindow> _hardwareInfoWindows = new(StringComparer.OrdinalIgnoreCase);
    private int _notificationVersion;
    private Storyboard? _notificationStoryboard;
    private bool _notificationIsPersistent;
    private bool _exitRequested;
    private bool _initialized;
    private bool _settingsReady;
    private GadgetWindow? _gadgetWindow;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        ShowWindowCommand = new RelayCommand(ShowFromTray);
        ResetMinMaxCommand = new AsyncRelayCommand(ViewModel.ResetMinMaxAsync);
        ExitCommand = new RelayCommand(ExitApplication);
        InitializeComponent();

        AppWindow.Resize(new SizeInt32(980, 680));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets\\OpenHardwareMonitor.ico");
        AppWindow.Changed += AppWindow_Changed;
        ViewModel.ThemeChanged += ViewModel_ThemeChanged;
        ViewModel.SettingsLoaded += ViewModel_SettingsLoaded;
        _refreshTimer.Interval = TimeSpan.FromSeconds(1);
        _refreshTimer.Tick += RefreshTimer_Tick;
        RootGrid.Loaded += RootGrid_Loaded;
        AppWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;
    }

    public MainViewModel ViewModel { get; }
    public static MainWindow? Instance { get; private set; }
    public ICommand ShowWindowCommand { get; }
    public ICommand ResetMinMaxCommand { get; }
    public ICommand ExitCommand { get; }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        Instance = this;
        Navigate("hardware");
        TrayIcon.ForceCreate();
        await ViewModel.InitializeAsync();
        if (!_settingsReady) ApplyLoadedSettings();
        var commandLineArguments = Environment.GetCommandLineArgs();
        var smokeHardwareInfoOnly = commandLineArguments.Any(argument =>
            string.Equals(argument, "--smoke-hardware-info-only", StringComparison.OrdinalIgnoreCase));
        var smokeHardwareInfo = smokeHardwareInfoOnly || commandLineArguments.Any(argument =>
            string.Equals(argument, "--smoke-hardware-info", StringComparison.OrdinalIgnoreCase));
        if (smokeHardwareInfo && ViewModel.HardwareTreeNodes.FirstOrDefault()?.Content is HardwareTreeItemViewModel firstHardware)
        {
            ShowHardwareInfo(firstHardware);
            ShowHardwareInfo(firstHardware);
            if (smokeHardwareInfoOnly) AppWindow.Hide();
        }
        if (ViewModel.Settings.GadgetEnabled) ShowGadget();
        if (ViewModel.Settings.StartMinimized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
        _refreshTimer.Start();
    }

    private async void RefreshTimer_Tick(object? sender, object e)
    {
        if (!ViewModel.IsHardwareToolbarBusy) await ViewModel.RefreshAsync();
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        Navigate(tag);
        if (sender.DisplayMode != NavigationViewDisplayMode.Expanded) sender.IsPaneOpen = false;
    }

    private void Navigation_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        if (!_settingsReady) return;
        sender.IsPaneOpen = args.DisplayMode == NavigationViewDisplayMode.Expanded && ViewModel.Settings.NavigationPaneOpen;
    }

    private async void Navigation_PaneOpened(NavigationView sender, object args)
    {
        if (!_settingsReady || sender.DisplayMode != NavigationViewDisplayMode.Expanded) return;
        ViewModel.Settings.NavigationPaneOpen = true;
        await ViewModel.SaveSettingsAsync();
    }

    private async void Navigation_PaneClosed(NavigationView sender, object args)
    {
        if (!_settingsReady || sender.DisplayMode != NavigationViewDisplayMode.Expanded) return;
        ViewModel.Settings.NavigationPaneOpen = false;
        await ViewModel.SaveSettingsAsync();
    }

    private void Navigate(string tag)
    {
        var page = tag switch
        {
            "charts" => typeof(Pages.ChartsPage),
            "controls" => typeof(Pages.ControlsPage),
            "settings" => typeof(Pages.SettingsPage),
            _ => typeof(Pages.HardwarePage)
        };
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page, ViewModel);
    }

    public void ShowHardwareInfo(HardwareTreeItemViewModel hardware)
    {
        if (hardware.Kind != MonitorTreeNodeKind.Hardware) return;
        if (_hardwareInfoWindows.TryGetValue(hardware.HardwareId, out var existing))
        {
            existing.AppWindow.Show();
            existing.Activate();
            return;
        }

        var window = new HardwareInfoWindow(ViewModel, hardware);
        _hardwareInfoWindows[hardware.HardwareId] = window;
        window.Closed += (_, _) => _hardwareInfoWindows.Remove(hardware.HardwareId);
        window.Activate();
    }

    public void ShowNotification(string message, InfoBarSeverity severity) =>
        ShowNotificationCore(message, severity, persistent: false);

    public void ShowProgressNotification(string message) =>
        ShowNotificationCore(message, InfoBarSeverity.Informational, persistent: true);

    public void HideTransientNotification()
    {
        if (!_notificationIsPersistent) HideNotification();
    }

    private void ShowNotificationCore(string message, InfoBarSeverity severity, bool persistent)
    {
        var wasOpen = NotificationBar.IsOpen;
        var version = ++_notificationVersion;
        StopNotificationAnimation();
        _notificationIsPersistent = persistent;
        NotificationBar.Message = message;
        NotificationBar.Severity = severity;
        NotificationBar.IsClosable = !persistent;
        NotificationBar.IsOpen = true;

        if (wasOpen)
        {
            NotificationBar.Opacity = 1;
            NotificationTransform.TranslateY = 0;
        }
        else
        {
            NotificationBar.Opacity = 0;
            NotificationTransform.TranslateY = -8;
            if (!DispatcherQueue.TryEnqueue(() => StartNotificationEntranceAnimation(version)))
            {
                NotificationBar.Opacity = 1;
                NotificationTransform.TranslateY = 0;
            }
        }

        if (!persistent) _ = HideNotificationAfterDelayAsync(version);
    }

    private void StartNotificationEntranceAnimation(int version)
    {
        if (version != _notificationVersion || !NotificationBar.IsOpen) return;
        var storyboard = CreateNotificationStoryboard(1, 0, 180, 220, EasingMode.EaseOut);
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_notificationStoryboard, storyboard)) _notificationStoryboard = null;
        };
        _notificationStoryboard = storyboard;
        storyboard.Begin();
    }

    public void HideNotification()
    {
        var version = ++_notificationVersion;
        _notificationIsPersistent = false;
        if (!NotificationBar.IsOpen) return;
        StopNotificationAnimation();

        var storyboard = CreateNotificationStoryboard(0, -6, 140, 160, EasingMode.EaseIn);
        storyboard.Completed += (_, _) =>
        {
            if (version == _notificationVersion) NotificationBar.IsOpen = false;
            if (ReferenceEquals(_notificationStoryboard, storyboard)) _notificationStoryboard = null;
        };
        _notificationStoryboard = storyboard;
        storyboard.Begin();
    }

    private async Task HideNotificationAfterDelayAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (_notificationVersion == version) HideNotification();
    }

    private Storyboard CreateNotificationStoryboard(
        double opacity,
        double translateY,
        int opacityDurationMilliseconds,
        int translationDurationMilliseconds,
        EasingMode easingMode)
    {
        var fade = new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(opacityDurationMilliseconds),
            EasingFunction = new CubicEase { EasingMode = easingMode }
        };
        Storyboard.SetTarget(fade, NotificationBar);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            To = translateY,
            Duration = TimeSpan.FromMilliseconds(translationDurationMilliseconds),
            EasingFunction = new CubicEase { EasingMode = easingMode },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(slide, NotificationTransform);
        Storyboard.SetTargetProperty(slide, "TranslateY");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        return storyboard;
    }

    private void StopNotificationAnimation()
    {
        _notificationStoryboard?.Stop();
        _notificationStoryboard = null;
    }

    public async Task RestoreDefaultWindowSizeAsync()
    {
        ViewModel.Settings.Window.Width = 980;
        ViewModel.Settings.Window.Height = 680;
        AppWindow.Resize(new SizeInt32(980, 680));
        await ViewModel.SaveSettingsAsync();
    }

    private async void ThemeButton_Click(object sender, RoutedEventArgs e) => await ViewModel.CycleThemeAsync();

    private void HideToTray_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_exitRequested && ViewModel.Settings.CloseToTray)
        {
            args.Cancel = true;
            HideToTray();
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange) return;
        var size = sender.Size;
        if (size.Width >= 800 && size.Height >= 560)
        {
            ViewModel.Settings.Window.Width = size.Width;
            ViewModel.Settings.Window.Height = size.Height;
        }
    }

    private void ViewModel_ThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ViewModel_SettingsLoaded(object? sender, EventArgs e) => ApplyLoadedSettings();

    private void ApplyLoadedSettings()
    {
        _settingsReady = true;
        AppWindow.Resize(new SizeInt32(ViewModel.Settings.Window.Width, ViewModel.Settings.Window.Height));
        Navigation.IsPaneOpen = Navigation.DisplayMode == NavigationViewDisplayMode.Expanded && ViewModel.Settings.NavigationPaneOpen;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = ViewModel.Settings.Theme switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void HideToTray()
    {
        foreach (var window in _hardwareInfoWindows.Values) window.AppWindow.Hide();
        AppWindow.Hide();
    }

    public async void ToggleGadget()
    {
        ViewModel.Settings.GadgetEnabled = !ViewModel.Settings.GadgetEnabled;
        await ViewModel.SaveSettingsAsync();
        if (ViewModel.Settings.GadgetEnabled) ShowGadget();
        else CloseGadget();
    }

    private void ShowGadget()
    {
        if (_gadgetWindow is not null) { _gadgetWindow.Activate(); return; }
        _gadgetWindow = new GadgetWindow(ViewModel);
        _gadgetWindow.Closed += (_, _) => _gadgetWindow = null;
        _gadgetWindow.Activate();
    }

    private void CloseGadget()
    {
        if (_gadgetWindow is null) return;
        _gadgetWindow.Close();
        _gadgetWindow = null;
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _refreshTimer.Stop();
        _notificationVersion++;
        StopNotificationAnimation();
        foreach (var window in _hardwareInfoWindows.Values.ToArray()) window.Close();
        _hardwareInfoWindows.Clear();
        CloseGadget();
        TrayIcon.Dispose();
        AppWindow.Changed -= AppWindow_Changed;
        ViewModel.ThemeChanged -= ViewModel_ThemeChanged;
        ViewModel.SettingsLoaded -= ViewModel_SettingsLoaded;
        await ViewModel.SaveSettingsAsync();
        await ViewModel.DisposeAsync();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
}
