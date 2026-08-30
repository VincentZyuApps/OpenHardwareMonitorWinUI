using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using OpenHardwareMonitor.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinUIEx;

namespace OpenHardwareMonitor.App;

public sealed partial class HardwareInfoWindow : WindowEx
{
    private readonly MainViewModel _viewModel;

    public HardwareInfoWindow(MainViewModel viewModel, HardwareTreeItemViewModel hardware)
    {
        _viewModel = viewModel;
        HardwareId = hardware.HardwareId;
        InitializeComponent();
        RootGrid.DataContext = hardware;
        Title = $"{hardware.Title} - 硬件信息";
        AppWindow.SetIcon("Assets\\OpenHardwareMonitor.ico");
        ApplyPlacement(viewModel.Settings.HardwareInfoWindow);
        ApplyTheme();
        AppWindow.Changed += AppWindow_Changed;
        _viewModel.ThemeChanged += ViewModel_ThemeChanged;
        Closed += HardwareInfoWindow_Closed;
    }

    public string HardwareId { get; }

    private void ApplyPlacement(WindowPlacementSettings placement)
    {
        var width = Math.Clamp(placement.Width, 420, 1920);
        var height = Math.Clamp(placement.Height, 360, 1440);
        if (placement.X is not int savedX || placement.Y is not int savedY)
        {
            AppWindow.Resize(new SizeInt32(width, height));
            return;
        }

        var displayArea = DisplayArea.GetFromPoint(new PointInt32(savedX, savedY), DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        var x = Math.Clamp(savedX, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - width));
        var y = Math.Clamp(savedY, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - height));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        var placement = _viewModel.Settings.HardwareInfoWindow;
        if (args.DidSizeChange)
        {
            placement.Width = Math.Max(420, sender.Size.Width);
            placement.Height = Math.Max(360, sender.Size.Height);
        }
        if (args.DidPositionChange)
        {
            placement.X = sender.Position.X;
            placement.Y = sender.Position.Y;
        }
    }

    private void ViewModel_ThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _viewModel.Settings.Theme switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (RootGrid.DataContext is not HardwareTreeItemViewModel hardware || string.IsNullOrWhiteSpace(hardware.Report)) return;
        var data = new DataPackage();
        data.SetText(hardware.Report);
        Clipboard.SetContent(data);
        Clipboard.Flush();
        CopyButtonLabel.Text = "已复制";
    }

    private async void HardwareInfoWindow_Closed(object sender, WindowEventArgs args)
    {
        AppWindow.Changed -= AppWindow_Changed;
        _viewModel.ThemeChanged -= ViewModel_ThemeChanged;
        await _viewModel.SaveSettingsAsync();
    }
}
