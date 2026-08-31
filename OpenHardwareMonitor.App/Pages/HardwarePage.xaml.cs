using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using OpenHardwareMonitor.Core;

namespace OpenHardwareMonitor.App.Pages;

public sealed partial class HardwarePage : Page
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;
    private ObservableCollection<TreeViewNode>? _boundRootNodes;
    private bool _rootSyncQueued;
    private string? _resizingColumn;
    private uint _resizePointerId;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private int _searchVersion;
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    public HardwarePage()
    {
        InitializeComponent();
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        DataContext = e.Parameter as MainViewModel;
        if (ViewModel is null) return;
        _boundRootNodes = ViewModel.HardwareTreeNodes;
        _boundRootNodes.CollectionChanged += HardwareTreeNodes_CollectionChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        SyncRootNodes();
        UpdateToolbarOperationState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _searchDebounceTimer.Stop();
        _searchVersion++;
        MainWindow.Instance?.HideTransientNotification();
        if (ViewModel is not null) ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_boundRootNodes is not null) _boundRootNodes.CollectionChanged -= HardwareTreeNodes_CollectionChanged;
        _boundRootNodes = null;
        base.OnNavigatedFrom(e);
    }

    private void HardwareTreeNodes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_rootSyncQueued) return;
        _rootSyncQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _rootSyncQueued = false;
            SyncRootNodes();
        });
    }

    private void SyncRootNodes()
    {
        if (_boundRootNodes is null) return;
        var desired = _boundRootNodes.ToArray();
        if (HardwareTree.RootNodes.SequenceEqual(desired)) return;
        HardwareTree.RootNodes.Clear();
        foreach (var node in desired) HardwareTree.RootNodes.Add(node);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        await RunToolbarOperationAsync(
            HardwareToolbarOperation.Refresh,
            "正在刷新硬件数据...",
            () => viewModel.RefreshAsync(),
            () => $"已刷新 {viewModel.Snapshot.Sensors.Count} 个传感器");
    }

    private async void ShowHiddenSensors_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || sender is not ToggleButton toggle) return;
        var showHidden = toggle.IsChecked == true;
        await RunToolbarOperationAsync(
            HardwareToolbarOperation.ShowHiddenSensors,
            showHidden ? "正在显示隐藏的传感器..." : "正在隐藏标记为隐藏的传感器...",
            async () =>
            {
                await viewModel.SetShowHiddenSensorsAsync(showHidden);
                return true;
            },
            () => showHidden ? "已显示隐藏的传感器" : "已隐藏标记为隐藏的传感器");
    }

    private async void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        await RunToolbarOperationAsync(
            HardwareToolbarOperation.ExpandAll,
            "正在展开全部硬件项目...",
            async () =>
            {
                await viewModel.ExpandAllAsync(true);
                return true;
            },
            () => "已展开全部硬件项目");
    }

    private async void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        await RunToolbarOperationAsync(
            HardwareToolbarOperation.CollapseAll,
            "正在折叠全部硬件项目...",
            async () =>
            {
                await viewModel.ExpandAllAsync(false);
                return true;
            },
            () => "已折叠全部硬件项目");
    }

    private async Task RunToolbarOperationAsync(
        HardwareToolbarOperation operation,
        string progressMessage,
        Func<Task<bool>> action,
        Func<string> successMessage)
    {
        if (ViewModel is not { } viewModel || !viewModel.TryBeginHardwareToolbarOperation(operation)) return;
        MainWindow.Instance?.ShowProgressNotification(progressMessage);
        await Task.Yield();

        string message;
        var severity = InfoBarSeverity.Success;
        try
        {
            if (await action()) message = successMessage();
            else
            {
                message = viewModel.StatusText;
                severity = InfoBarSeverity.Error;
            }
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
            message = $"操作失败: {exception.Message}";
            severity = InfoBarSeverity.Error;
        }
        finally
        {
            viewModel.EndHardwareToolbarOperation(operation);
        }

        MainWindow.Instance?.ShowNotification(message, severity);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ActiveHardwareToolbarOperation) or nameof(MainViewModel.IsHardwareToolbarBusy))
            UpdateToolbarOperationState();
    }

    private void UpdateToolbarOperationState()
    {
        var operation = ViewModel?.ActiveHardwareToolbarOperation ?? HardwareToolbarOperation.None;
        var busy = operation != HardwareToolbarOperation.None;
        RefreshButton.IsEnabled = !busy;
        ShowHiddenSensorsButton.IsEnabled = !busy;
        ExpandAllButton.IsEnabled = !busy;
        CollapseAllButton.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        SetOperationVisual(RefreshIcon, RefreshProgress, operation == HardwareToolbarOperation.Refresh);
        SetOperationVisual(ShowHiddenSensorsIcon, ShowHiddenSensorsProgress, operation == HardwareToolbarOperation.ShowHiddenSensors);
        SetOperationVisual(ExpandAllIcon, ExpandAllProgress, operation == HardwareToolbarOperation.ExpandAll);
        SetOperationVisual(CollapseAllIcon, CollapseAllProgress, operation == HardwareToolbarOperation.CollapseAll);
    }

    private static void SetOperationVisual(FrameworkElement icon, ProgressRing progress, bool active)
    {
        icon.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        progress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        progress.IsActive = active;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounceTimer.Stop();
        var version = ++_searchVersion;
        if (sender is not TextBox searchBox) return;
        if (string.IsNullOrWhiteSpace(searchBox.Text))
        {
            _ = ApplySearchFilterAsync(string.Empty, version, showResult: false);
            MainWindow.Instance?.HideTransientNotification();
            return;
        }
        _searchDebounceTimer.Start();
    }

    private async void SearchDebounceTimer_Tick(object? sender, object e)
    {
        _searchDebounceTimer.Stop();
        if (ViewModel is null) return;
        if (ViewModel.IsHardwareToolbarBusy)
        {
            _searchDebounceTimer.Start();
            return;
        }
        await ApplySearchFilterAsync(SearchBox.Text.Trim(), _searchVersion, showResult: true);
    }

    private async Task ApplySearchFilterAsync(string query, int version, bool showResult)
    {
        if (ViewModel is not { } viewModel) return;
        try
        {
            await viewModel.SetSensorFilterAsync(query);
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
            if (version == _searchVersion) ShowNotification($"搜索失败: {exception.Message}", InfoBarSeverity.Error);
            return;
        }
        if (version != _searchVersion || !showResult || query.Length == 0) return;

        var (sensorCount, hardwareCount) = CountFilteredItems(viewModel.HardwareTreeNodes);
        if (sensorCount > 0)
        {
            var hardwareSuffix = hardwareCount > 0 ? $"，来自 {hardwareCount} 个硬件" : string.Empty;
            ShowNotification($"“{query}”筛选出 {sensorCount} 个传感器{hardwareSuffix}", InfoBarSeverity.Success);
        }
        else if (hardwareCount > 0)
        {
            ShowNotification($"“{query}”筛选出 {hardwareCount} 个硬件", InfoBarSeverity.Success);
        }
        else
        {
            ShowNotification($"未找到与“{query}”匹配的硬件或传感器", InfoBarSeverity.Warning);
        }
    }

    private static (int SensorCount, int HardwareCount) CountFilteredItems(IEnumerable<TreeViewNode> roots)
    {
        var sensorCount = 0;
        var hardwareCount = 0;
        foreach (var root in roots) CountFilteredItems(root, ref sensorCount, ref hardwareCount);
        return (sensorCount, hardwareCount);
    }

    private static void CountFilteredItems(TreeViewNode node, ref int sensorCount, ref int hardwareCount)
    {
        if (node.Content is HardwareTreeItemViewModel item)
        {
            if (item.IsSensor) sensorCount++;
            else if (item.Kind == MonitorTreeNodeKind.Hardware) hardwareCount++;
        }
        foreach (var child in node.Children) CountFilteredItems(child, ref sensorCount, ref hardwareCount);
    }

    private void ShowNotification(string message, InfoBarSeverity severity)
        => MainWindow.Instance?.ShowNotification(message, severity);

    private void HardwareTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args) =>
        ViewModel?.SelectTreeItem(sender.SelectedNode ?? sender.SelectedItem);

    private async void HardwareTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (ViewModel is not null) await ViewModel.SetTreeNodeExpandedAsync(args.Node, true);
    }

    private async void HardwareTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (ViewModel is not null) await ViewModel.SetTreeNodeExpandedAsync(args.Node, false);
    }

    private void HardwareTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args) => ViewModel?.SelectTreeItem(args.InvokedItem);

    private async void TreeRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TreeViewNode { Content: HardwareTreeItemViewModel item } })
        {
            if (item.Kind == MonitorTreeNodeKind.Hardware)
            {
                OpenHardwareInfo(item);
                e.Handled = true;
            }
            else if (item is { IsSensor: true, Sensor.Parameters.Count: > 0 })
            {
                await ShowParametersAsync(item);
                e.Handled = true;
            }
        }
    }

    private void ColumnResize_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null || sender is not UIElement { } handle || sender is not FrameworkElement { Tag: string column }) return;
        _resizingColumn = column;
        _resizePointerId = e.Pointer.PointerId;
        _resizeStartX = e.GetCurrentPoint(this).Position.X;
        _resizeStartWidth = ViewModel.GetHardwareColumnWidth(column);
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ColumnResize_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null || _resizingColumn is null || e.Pointer.PointerId != _resizePointerId) return;
        var currentX = e.GetCurrentPoint(this).Position.X;
        ViewModel.PreviewHardwareColumnWidth(_resizingColumn, _resizeStartWidth + _resizeStartX - currentX);
        e.Handled = true;
    }

    private async void ColumnResize_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizingColumn is null || e.Pointer.PointerId != _resizePointerId) return;
        if (sender is UIElement handle) handle.ReleasePointerCapture(e.Pointer);
        _resizingColumn = null;
        if (ViewModel is not null) await ViewModel.PersistHardwareColumnWidthsAsync();
        e.Handled = true;
    }

    private void ColumnResize_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_resizingColumn is null || e.Pointer.PointerId != _resizePointerId) return;
        if (ViewModel is not null) ViewModel.PreviewHardwareColumnWidth(_resizingColumn, _resizeStartWidth);
        _resizingColumn = null;
        e.Handled = true;
    }

    private static HardwareTreeItemViewModel? ItemFrom(object sender) =>
        (sender as MenuFlyoutItem)?.Tag as HardwareTreeItemViewModel;

    private void NodeMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        var item = menu.Items.OfType<MenuFlyoutItem>()
            .Select(entry => entry.Tag as HardwareTreeItemViewModel)
            .FirstOrDefault(value => value is not null);
        foreach (var entry in menu.Items.OfType<MenuFlyoutItem>())
        {
            var action = AutomationProperties.GetAutomationId(entry);
            entry.Visibility = action switch
            {
                "hardware-info" => item?.Kind == MonitorTreeNodeKind.Hardware ? Visibility.Visible : Visibility.Collapsed,
                "parameters" => item is { IsSensor: true, Sensor.Parameters.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed,
                _ => item?.IsSensor == true ? Visibility.Visible : Visibility.Collapsed
            };
            if (action == "chart") entry.Text = item?.IsCharted == true ? "从图表中移除" : "在图表中显示";
            if (action == "hide") entry.Text = item?.IsHidden == true ? "取消隐藏" : "隐藏传感器";
        }
    }

    private void HardwareInfo_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item) OpenHardwareInfo(item);
    }

    private static void OpenHardwareInfo(HardwareTreeItemViewModel item)
    {
        if (item.Kind == MonitorTreeNodeKind.Hardware) MainWindow.Instance?.ShowHardwareInfo(item);
    }

    private async void Parameters_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { IsSensor: true, Sensor.Parameters.Count: > 0 } item)
            await ShowParametersAsync(item);
    }

    private async Task ShowParametersAsync(HardwareTreeItemViewModel item)
    {
        if (ViewModel is null || item.Sensor is not { Parameters.Count: > 0 } sensor) return;
        var content = new StackPanel { Spacing = 12 };
        var editors = new List<(ParameterReading Parameter, NumberBox Value, CheckBox UseDefault)>();
        foreach (var parameter in sensor.Parameters)
        {
            var value = new NumberBox
            {
                Value = parameter.Value,
                Minimum = -1_000_000,
                Maximum = 1_000_000,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                IsEnabled = !parameter.IsDefault
            };
            var useDefault = new CheckBox { Content = "使用默认值", IsChecked = parameter.IsDefault, VerticalAlignment = VerticalAlignment.Center };
            useDefault.Checked += (_, _) => value.IsEnabled = false;
            useDefault.Unchecked += (_, _) => value.IsEnabled = true;
            var editorRow = new Grid { ColumnSpacing = 12 };
            editorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            editorRow.Children.Add(value);
            Grid.SetColumn(useDefault, 1);
            editorRow.Children.Add(useDefault);
            var parameterBlock = new StackPanel { Spacing = 5 };
            parameterBlock.Children.Add(new TextBlock { Text = parameter.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (!string.IsNullOrWhiteSpace(parameter.Description))
                parameterBlock.Children.Add(new TextBlock { Text = parameter.Description, FontSize = 12, Opacity = 0.72, TextWrapping = TextWrapping.Wrap });
            parameterBlock.Children.Add(editorRow);
            content.Children.Add(parameterBlock);
            editors.Add((parameter, value, useDefault));
        }

        var dialog = new ContentDialog
        {
            Title = $"{item.Title} - 参数",
            Content = new ScrollViewer { Content = content, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var editor in editors)
        {
            var value = double.IsNaN(editor.Value.Value) ? editor.Parameter.Value : editor.Value.Value;
            await ViewModel.SetParameterAsync(sensor.Id, editor.Parameter.Id, value, editor.UseDefault.IsChecked == true);
        }
    }

    private async void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && ItemFrom(sender) is { IsSensor: true } item)
            await ViewModel.SetSensorHiddenAsync(item.Sensor!.Id, !item.IsHidden);
    }

    private async void Chart_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && ItemFrom(sender) is { IsSensor: true } item)
            await ViewModel.SetChartVisibleAsync(item.Sensor!.Id, !item.IsCharted);
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && ItemFrom(sender) is { IsSensor: true } item)
            await ViewModel.ResetSensorMinMaxAsync(item.Sensor!.Id);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ItemFrom(sender) is not { IsSensor: true } item) return;
        var editor = new TextBox { Text = item.Title, PlaceholderText = "显示名称" };
        var dialog = new ContentDialog
        {
            Title = "设置显示名称",
            Content = editor,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.SetSensorDisplayNameAsync(item.Sensor!.Id, editor.Text);
    }
}
