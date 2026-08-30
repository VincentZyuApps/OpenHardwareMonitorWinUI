using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenHardwareMonitor.Core;

namespace OpenHardwareMonitor.App.Pages;

public sealed partial class SettingsPage : Page
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public SettingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        DataContext = e.Parameter as MainViewModel;
        ThemeSelector.SelectedIndex = ViewModel?.Settings.Theme switch
        {
            ThemePreference.Light => 1,
            ThemePreference.Dark => 2,
            _ => 0
        };
    }

    private async void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || ThemeSelector.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var theme = tag switch { "Light" => ThemePreference.Light, "Dark" => ThemePreference.Dark, _ => ThemePreference.System };
        await ViewModel.SetThemeAsync(theme);
    }

    private async void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.SaveSettingsAsync();
    }

    private async void SettingsChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.SaveSettingsAsync();
    }

    private async void DisplayColumnsChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.ApplyDisplayColumnSettingsAsync();
    }

    private async void WebServerChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.SaveSettingsAsync();
        await ViewModel.ApplyServiceStateAsync();
    }

    private async void WebSettingsChanged(object sender, TextChangedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.SaveSettingsAsync();
    }

    private async void WebSettingsChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.SaveSettingsAsync();
    }

    private void ToggleGadget_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.ToggleGadget();

    private async void RestoreWindowSize_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is { } window) await window.RestoreDefaultWindowSizeAsync();
    }
}
