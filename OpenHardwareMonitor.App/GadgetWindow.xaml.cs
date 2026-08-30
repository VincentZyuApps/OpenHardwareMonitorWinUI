using Microsoft.UI.Xaml;
using WinUIEx;
using Windows.Graphics;

namespace OpenHardwareMonitor.App;

public sealed partial class GadgetWindow : WindowEx
{
    private readonly MainViewModel _viewModel;

    public GadgetWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        RootGrid.DataContext = viewModel;
        AppWindow.Resize(new SizeInt32(280, 260));
        AppWindow.SetIcon("Assets\\OpenHardwareMonitor.ico");
        IsAlwaysOnTop = true;
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Settings.GadgetEnabled = false;
        await _viewModel.SaveSettingsAsync();
        Close();
    }
}
