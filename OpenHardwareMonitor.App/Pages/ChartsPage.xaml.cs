using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;

namespace OpenHardwareMonitor.App.Pages;

public sealed partial class ChartsPage : Page
{
    public ChartsPage() => InitializeComponent();
    protected override void OnNavigatedTo(NavigationEventArgs e) => DataContext = e.Parameter as MainViewModel;

    private async void ChartSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string sensorId, IsChecked: bool isSelected } && DataContext is MainViewModel viewModel)
            await viewModel.SetChartVisibleAsync(sensorId, isSelected);
    }
}
