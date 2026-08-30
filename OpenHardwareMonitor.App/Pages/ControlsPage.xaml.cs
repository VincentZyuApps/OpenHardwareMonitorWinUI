using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace OpenHardwareMonitor.App.Pages;

public sealed partial class ControlsPage : Page
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;
    public ControlsPage() => InitializeComponent();
    protected override void OnNavigatedTo(NavigationEventArgs e) => DataContext = e.Parameter as MainViewModel;
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && (sender as FrameworkElement)?.DataContext is ControlRowViewModel row) await ViewModel.SetControlAsync(row, row.PendingValue);
    }
    private async void Default_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && (sender as FrameworkElement)?.DataContext is ControlRowViewModel row) await ViewModel.SetControlAsync(row, null);
    }
}
