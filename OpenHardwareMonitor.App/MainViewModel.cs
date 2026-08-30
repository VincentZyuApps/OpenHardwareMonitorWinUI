using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using OpenHardwareMonitor.Core;

namespace OpenHardwareMonitor.App;

public enum MonitorTreeNodeKind
{
    Hardware,
    SensorType,
    Sensor
}

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly HardwareMonitorService _hardware;
    private readonly CsvLoggingService _logger;
    private readonly RemoteWebServer _webServer;
    private readonly Dictionary<string, TreeViewNode> _treeNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HardwareTreeItemViewModel> _treeItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _programmaticExpansionChanges = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;
    private int _refreshInProgress;

    public MainViewModel(SettingsStore settingsStore, HardwareMonitorService hardware, CsvLoggingService logger, RemoteWebServer webServer)
    {
        _settingsStore = settingsStore;
        _hardware = hardware;
        _logger = logger;
        _webServer = webServer;
    }

    public AppSettings Settings { get; private set; } = new();
    public HardwareSnapshot Snapshot => _hardware.Snapshot;
    public ObservableCollection<TreeViewNode> HardwareTreeNodes { get; } = new();
    public ObservableCollection<DashboardMetricViewModel> DashboardMetrics { get; } = new();
    public ObservableCollection<SensorRowViewModel> SensorRows { get; } = new();
    public ObservableCollection<ControlRowViewModel> ControlRows { get; } = new();
    public ObservableCollection<ChartSeriesViewModel> ChartSeries { get; } = new();
    public ObservableCollection<ChartCandidateViewModel> ChartCandidates { get; } = new();
    public string SettingsPath => _settingsStore.SettingsPath;
    public bool IsWebServerRunning => _webServer.IsRunning;
    public string WebServerAddress => _webServer.Address;
    public double ValueColumnWidth => GetHardwareColumnWidth("Value");
    public double MinimumColumnWidth => GetHardwareColumnWidth("Minimum");
    public double MaximumColumnWidth => GetHardwareColumnWidth("Maximum");

    [ObservableProperty] private string _statusText = "正在初始化硬件监控...";
    [ObservableProperty] private string _sensorFilter = string.Empty;
    [ObservableProperty] private bool _showHiddenSensors;
    [ObservableProperty] private HardwareTreeItemViewModel? _selectedTreeItem;
    [ObservableProperty] private HardwareTreeItemViewModel? _selectedHardware;

    public event EventHandler? ThemeChanged;
    public event EventHandler? SettingsLoaded;

    partial void OnSensorFilterChanged(string value) => RebuildHardwareTree(Snapshot);

    partial void OnShowHiddenSensorsChanged(bool value)
    {
        Settings.ShowHiddenSensors = value;
        if (_initialized)
        {
            RebuildHardwareTree(Snapshot);
            _ = SaveSettingsAsync();
        }
    }

    partial void OnSelectedTreeItemChanged(HardwareTreeItemViewModel? value)
    {
        if (value is null)
        {
            SelectedHardware = null;
            return;
        }

        SelectedHardware = value.Kind == MonitorTreeNodeKind.Hardware
            ? value
            : _treeItems.GetValueOrDefault(value.HardwareId);
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        var portable = Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "--portable", StringComparison.OrdinalIgnoreCase));
        Settings = await _settingsStore.LoadAsync(portable);
        ShowHiddenSensors = Settings.ShowHiddenSensors;
        OnPropertyChanged(nameof(Settings));
        NotifyHardwareColumnWidthsChanged();
        SettingsLoaded?.Invoke(this, EventArgs.Empty);
        await _hardware.StartAsync(Settings);
        await ApplyServiceStateAsync();
        _initialized = true;
        await RefreshAsync();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync()
    {
        if (!_initialized || Interlocked.Exchange(ref _refreshInProgress, 1) != 0) return;
        try
        {
            var snapshot = await _hardware.RefreshAsync();
            await _logger.LogAsync(snapshot, Settings.Logging);
            ProjectSnapshot(snapshot);
            StatusText = snapshot.Timestamp == DateTimeOffset.MinValue
                ? "等待传感器数据"
                : $"{snapshot.Sensors.Count} 个传感器 | 上次更新 {snapshot.Timestamp:HH:mm:ss}";
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
            StatusText = $"读取硬件数据失败: {exception.Message}";
        }
        finally
        {
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    public async Task ResetMinMaxAsync()
    {
        await _hardware.ResetMinMaxAsync();
        StatusText = "已重置全部传感器的最小值和最大值";
        await RefreshAsync();
    }

    public async Task ResetSensorMinMaxAsync(string sensorId)
    {
        await _hardware.ResetMinMaxAsync(sensorId);
        await RefreshAsync();
    }

    public async Task SetControlAsync(ControlRowViewModel control, double? value)
    {
        try
        {
            await _hardware.SetControlAsync(control.SensorId, value);
            StatusText = value is null ? $"{control.Name} 已恢复自动控制" : $"{control.Name} 设置为 {value:0}%";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
            StatusText = $"无法调整 {control.Name}: {exception.Message}";
        }
    }

    public async Task SetThemeAsync(ThemePreference preference)
    {
        Settings.Theme = preference;
        await SaveSettingsAsync();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ApplyDisplayColumnSettingsAsync()
    {
        foreach (var item in _treeItems.Values) item.UpdateColumnVisibility(Settings);
        await SaveSettingsAsync();
    }

    public double GetHardwareColumnWidth(string column) =>
        Settings.ColumnWidths.TryGetValue(column, out var width) ? Math.Clamp(width, 64, 160) : 80;

    public void PreviewHardwareColumnWidth(string column, double width)
    {
        Settings.ColumnWidths[column] = (int)Math.Round(Math.Clamp(width, 64, 160));
        NotifyHardwareColumnWidthsChanged();
        foreach (var item in _treeItems.Values) item.UpdateColumnVisibility(Settings);
    }

    public Task PersistHardwareColumnWidthsAsync() => SaveSettingsAsync();

    public async Task CycleThemeAsync()
    {
        var next = Settings.Theme switch
        {
            ThemePreference.System => ThemePreference.Light,
            ThemePreference.Light => ThemePreference.Dark,
            _ => ThemePreference.System
        };
        await SetThemeAsync(next);
        StatusText = $"主题：{GetThemeName(next)}";
    }

    public async Task SetChartVisibleAsync(string sensorId, bool visible)
    {
        Settings.ChartSelectionInitialized = true;
        GetSensorPresentation(sensorId).ShowInChart = visible;
        var candidate = ChartCandidates.FirstOrDefault(item => string.Equals(item.SensorId, sensorId, StringComparison.OrdinalIgnoreCase));
        if (candidate is not null) candidate.IsSelected = visible;
        await SaveSettingsAsync();
        RebuildCharts(Snapshot);
    }

    public async Task SetSensorHiddenAsync(string sensorId, bool hidden)
    {
        GetSensorPresentation(sensorId).IsHidden = hidden;
        await SaveSettingsAsync();
        RebuildHardwareTree(Snapshot);
        ProjectFlatRows(Snapshot);
    }

    public async Task SetSensorDisplayNameAsync(string sensorId, string? displayName)
    {
        GetSensorPresentation(sensorId).DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        await SaveSettingsAsync();
        ProjectSnapshot(Snapshot);
    }

    public async Task SetSensorTrayVisibleAsync(string sensorId, bool visible)
    {
        GetSensorPresentation(sensorId).ShowInTray = visible;
        await SaveSettingsAsync();
    }

    public async Task SetSensorGadgetVisibleAsync(string sensorId, bool visible)
    {
        GetSensorPresentation(sensorId).ShowInGadget = visible;
        await SaveSettingsAsync();
    }

    public async Task SetParameterAsync(string sensorId, string parameterId, double value, bool useDefault)
    {
        try
        {
            await _hardware.SetParameterAsync(sensorId, parameterId, value, useDefault);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
            StatusText = $"无法更新传感器参数: {exception.Message}";
        }
    }

    public async Task SetTreeNodeExpandedAsync(TreeViewNode node, bool expanded)
    {
        if (node.Content is not HardwareTreeItemViewModel item) return;
        node.IsExpanded = expanded;
        if (_programmaticExpansionChanges.Remove(item.Id, out var expected) && expected == expanded) return;
        if (!string.IsNullOrWhiteSpace(SensorFilter)) return;
        Settings.ExpandedNodes[item.Id] = expanded;
        await SaveSettingsAsync();
    }

    public async Task ExpandAllAsync(bool expanded)
    {
        foreach (var root in HardwareTreeNodes) SetExpandedRecursive(root, expanded);
        await SaveSettingsAsync();
    }

    public void SelectTreeItem(object? selectedItem)
    {
        var item = selectedItem switch
        {
            TreeViewNode node => node.Content as HardwareTreeItemViewModel,
            HardwareTreeItemViewModel viewModel => viewModel,
            _ => null
        };
        SelectedTreeItem = item;
    }

    public async Task ApplyServiceStateAsync()
    {
        try
        {
            if (Settings.WebServer.Enabled)
            {
                if (!_webServer.IsRunning) await _webServer.StartAsync(Settings.WebServer);
            }
            else if (_webServer.IsRunning)
            {
                await _webServer.StopAsync();
            }
            OnPropertyChanged(nameof(IsWebServerRunning));
            OnPropertyChanged(nameof(WebServerAddress));
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
            Settings.WebServer.Enabled = false;
            StatusText = $"无法启动 Web 服务: {exception.Message}";
        }
    }

    public IReadOnlyList<DataPoint> GetHistory(string sensorId) => _hardware.GetHistory(sensorId);

    public async Task SaveSettingsAsync()
    {
        await _settingsStore.SaveAsync(Settings);
        OnPropertyChanged(nameof(SettingsPath));
    }

    private void NotifyHardwareColumnWidthsChanged()
    {
        OnPropertyChanged(nameof(ValueColumnWidth));
        OnPropertyChanged(nameof(MinimumColumnWidth));
        OnPropertyChanged(nameof(MaximumColumnWidth));
    }

    private void ProjectSnapshot(HardwareSnapshot snapshot)
    {
        ProjectFlatRows(snapshot);
        EnsureDefaultChartSelections(snapshot);
        RebuildChartCandidates(snapshot);
        RebuildCharts(snapshot);
        RebuildHardwareTree(snapshot);
    }

    private void ProjectFlatRows(HardwareSnapshot snapshot)
    {
        var rows = snapshot.Sensors
            .Where(IsSensorVisible)
            .Select(sensor => new SensorRowViewModel(sensor, GetSensorPresentation(sensor.Id)))
            .OrderBy(row => row.HardwareName)
            .ThenBy(row => row.Type)
            .ThenBy(row => row.Name)
            .ToArray();

        SensorRows.Clear();
        foreach (var row in rows) SensorRows.Add(row);

        ControlRows.Clear();
        foreach (var row in rows.Where(row => row.IsControllable)) ControlRows.Add(new ControlRowViewModel(row));
        RebuildDashboard(rows);
    }

    private void RebuildHardwareTree(HardwareSnapshot snapshot)
    {
        if (snapshot.Hardware.Count == 0)
        {
            HardwareTreeNodes.Clear();
            return;
        }

        var roots = snapshot.Hardware.Select(BuildHardwareNode).Where(node => node is not null).Cast<TreeViewNode>().ToArray();
        var ids = roots.Select(GetNodeId).ToArray();
        var existingIds = HardwareTreeNodes.Select(GetNodeId).ToArray();
        if (!ids.SequenceEqual(existingIds, StringComparer.OrdinalIgnoreCase))
        {
            HardwareTreeNodes.Clear();
            foreach (var root in roots) HardwareTreeNodes.Add(root);
        }
    }

    private TreeViewNode? BuildHardwareNode(HardwareNodeSnapshot snapshot)
    {
        var hardwareMatches = Matches(snapshot.Name) || Matches(snapshot.Type);
        var desiredChildren = new List<TreeViewNode>();

        foreach (var group in snapshot.Sensors.GroupBy(sensor => sensor.Type).OrderBy(group => group.Key))
        {
            var typeTitle = GetSensorTypeName(group.Key);
            var typeMatches = hardwareMatches || Matches(group.Key) || Matches(typeTitle);
            var sensors = group.Where(IsSensorVisible)
                .Where(sensor => typeMatches || Matches(sensor.DisplayName) || Matches(sensor.Name) || Matches(sensor.Unit))
                .ToArray();
            if (sensors.Length == 0) continue;
            desiredChildren.Add(BuildTypeNode(snapshot, group.Key, typeTitle, sensors));
        }

        foreach (var child in snapshot.Children)
        {
            var childNode = BuildHardwareNode(child);
            if (childNode is not null) desiredChildren.Add(childNode);
        }

        if (!string.IsNullOrWhiteSpace(SensorFilter) && !hardwareMatches && desiredChildren.Count == 0) return null;
        var item = GetTreeItem(snapshot.Id);
        item.UpdateHardware(snapshot, BuildHardwareSummary(snapshot));
        var node = GetTreeNode(item, defaultExpanded: false);
        ReconcileChildren(node, desiredChildren);
        ApplyFilterExpansion(node, desiredChildren.Count > 0);
        return node;
    }

    private TreeViewNode BuildTypeNode(HardwareNodeSnapshot hardware, string sensorType, string title, IReadOnlyList<SensorReading> sensors)
    {
        var id = $"{hardware.Id}/type/{sensorType}";
        var item = GetTreeItem(id);
        item.UpdateType(id, hardware.Id, title, sensors.Count);
        var node = GetTreeNode(item, defaultExpanded: false);
        var desiredChildren = sensors.Select(sensor =>
        {
            var sensorItem = GetTreeItem(sensor.Id);
            sensorItem.UpdateSensor(sensor, GetSensorPresentation(sensor.Id));
            return GetTreeNode(sensorItem, defaultExpanded: false);
        }).ToArray();
        ReconcileChildren(node, desiredChildren);
        ApplyFilterExpansion(node, desiredChildren.Length > 0);
        return node;
    }

    private TreeViewNode GetTreeNode(HardwareTreeItemViewModel item, bool defaultExpanded)
    {
        item.UpdateColumnVisibility(Settings);
        if (_treeNodes.TryGetValue(item.Id, out var existing))
        {
            existing.Content = item;
            item.Node = existing;
            if (string.IsNullOrWhiteSpace(SensorFilter))
                SetExpandedProgrammatically(existing, item.Id, Settings.ExpandedNodes.TryGetValue(item.Id, out var existingExpanded) ? existingExpanded : defaultExpanded);
            return existing;
        }

        var initialExpanded = Settings.ExpandedNodes.TryGetValue(item.Id, out var expanded) ? expanded : defaultExpanded;
        var node = new TreeViewNode
        {
            Content = item,
            IsExpanded = initialExpanded
        };
        if (initialExpanded) _programmaticExpansionChanges[item.Id] = true;
        _treeNodes[item.Id] = node;
        item.Node = node;
        return node;
    }

    private void ApplyFilterExpansion(TreeViewNode node, bool hasMatchingChildren)
    {
        if (!string.IsNullOrWhiteSpace(SensorFilter) && hasMatchingChildren)
            SetExpandedProgrammatically(node, GetNodeId(node), true);
    }

    private void SetExpandedProgrammatically(TreeViewNode node, string id, bool expanded)
    {
        if (node.IsExpanded == expanded) return;
        _programmaticExpansionChanges[id] = expanded;
        node.IsExpanded = expanded;
    }

    private static void ReconcileChildren(TreeViewNode parent, IReadOnlyList<TreeViewNode> desired)
    {
        var currentIds = parent.Children.Select(GetNodeId).ToArray();
        var desiredIds = desired.Select(GetNodeId).ToArray();
        if (currentIds.SequenceEqual(desiredIds, StringComparer.OrdinalIgnoreCase)) return;
        parent.Children.Clear();
        foreach (var child in desired) parent.Children.Add(child);
    }

    private HardwareTreeItemViewModel GetTreeItem(string id)
    {
        if (_treeItems.TryGetValue(id, out var item)) return item;
        item = new HardwareTreeItemViewModel(id);
        _treeItems[id] = item;
        return item;
    }

    private void RebuildDashboard(IReadOnlyList<SensorRowViewModel> rows)
    {
        DashboardMetrics.Clear();
        AddMetric("CPU", FindMetric(rows, "Cpu", "Load", "CPU Total"), "Processor");
        AddMetric("GPU", FindMetric(rows, "Gpu", "Load", null), "Graphics");
        AddMetric("Memory", FindMetric(rows, "Memory", "Load", null), "Memory");
        AddMetric("Storage", FindMetric(rows, "Storage", "Temperature", null), "Storage");
    }

    private void AddMetric(string title, SensorRowViewModel? row, string icon) =>
        DashboardMetrics.Add(new DashboardMetricViewModel(title, icon, row?.ValueText ?? "--", row?.Name ?? "No sensor", row?.SensorId));

    private static SensorRowViewModel? FindMetric(IEnumerable<SensorRowViewModel> rows, string hardwareType, string type, string? preferredName) =>
        rows.FirstOrDefault(row => row.HardwareType.Contains(hardwareType, StringComparison.OrdinalIgnoreCase) && row.Type == type && (preferredName is null || row.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase)))
        ?? rows.FirstOrDefault(row => row.HardwareType.Contains(hardwareType, StringComparison.OrdinalIgnoreCase) && row.Type == type);

    private void EnsureDefaultChartSelections(HardwareSnapshot snapshot)
    {
        if (Settings.ChartSelectionInitialized || snapshot.Sensors.Count == 0) return;
        foreach (var sensor in snapshot.Sensors.Where(item => item.Value is not null && (item.Type is "Temperature" or "Load" or "Power")).Take(4))
            GetSensorPresentation(sensor.Id).ShowInChart = true;
        Settings.ChartSelectionInitialized = true;
    }

    private void RebuildChartCandidates(HardwareSnapshot snapshot)
    {
        ChartCandidates.Clear();
        foreach (var sensor in snapshot.Sensors.Where(item => item.Value is not null && IsSensorVisible(item))
                     .OrderBy(item => item.HardwareName).ThenBy(item => item.Type).ThenBy(item => item.DisplayName))
            ChartCandidates.Add(new ChartCandidateViewModel(sensor, GetSensorPresentation(sensor.Id).ShowInChart));
    }

    private void RebuildCharts(HardwareSnapshot snapshot)
    {
        ChartSeries.Clear();
        foreach (var sensor in snapshot.Sensors.Where(item => GetSensorPresentation(item.Id).ShowInChart).Take(8))
            ChartSeries.Add(new ChartSeriesViewModel(sensor, _hardware.GetHistory(sensor.Id)));
    }

    private SensorPresentationSettings GetSensorPresentation(string sensorId)
    {
        if (!Settings.Sensors.TryGetValue(sensorId, out var value))
            Settings.Sensors[sensorId] = value = new SensorPresentationSettings();
        return value;
    }

    private bool IsSensorVisible(SensorReading sensor) => ShowHiddenSensors || (!sensor.IsDefaultHidden && !GetSensorPresentation(sensor.Id).IsHidden);

    private bool Matches(string value) => string.IsNullOrWhiteSpace(SensorFilter) || value.Contains(SensorFilter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string BuildHardwareSummary(HardwareNodeSnapshot snapshot)
    {
        var parts = snapshot.Sensors.Where(sensor => sensor.Value is not null && (sensor.Type is "Temperature" or "Load" or "Power" or "Fan"))
            .Take(2).Select(sensor => $"{sensor.DisplayName} {Format(sensor.Value, sensor.Unit)}").ToArray();
        return parts.Length == 0 ? $"{snapshot.Sensors.Count} 个传感器" : string.Join(" · ", parts);
    }

    private static string GetNodeId(TreeViewNode node) => (node.Content as HardwareTreeItemViewModel)?.Id ?? string.Empty;

    private void SetExpandedRecursive(TreeViewNode node, bool expanded)
    {
        if (node.Content is HardwareTreeItemViewModel item)
        {
            SetExpandedProgrammatically(node, item.Id, expanded);
            Settings.ExpandedNodes[item.Id] = expanded;
        }
        foreach (var child in node.Children) SetExpandedRecursive(child, expanded);
    }

    public static string GetSensorTypeName(string type) => type switch
    {
        "Temperature" => "温度",
        "Load" => "负载",
        "Fan" => "风扇",
        "Voltage" => "电压",
        "Current" => "电流",
        "Clock" => "时钟",
        "Power" => "功耗",
        "Data" or "SmallData" => "数据",
        "Frequency" => "频率",
        "Throughput" => "吞吐量",
        "Control" => "控制",
        "Humidity" => "湿度",
        _ => type
    };

    public static string Format(double? value, string unit) => value is null ? "--" : $"{value:0.##} {unit}".TrimEnd();

    public static string GetThemeName(ThemePreference theme) => theme switch
    {
        ThemePreference.System => "跟随系统",
        ThemePreference.Light => "浅色",
        ThemePreference.Dark => "深色",
        _ => theme.ToString()
    };

    public async ValueTask DisposeAsync()
    {
        await _webServer.DisposeAsync();
        await _hardware.DisposeAsync();
    }
}

public sealed class HardwareTreeItemViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _summaryText = string.Empty;
    private string _valueText = string.Empty;
    private string _minimumText = string.Empty;
    private string _maximumText = string.Empty;
    private string _report = string.Empty;
    private SensorReading? _sensor;
    private bool _isHidden;
    private bool _showValueColumn = true;
    private bool _showMinimumColumn = true;
    private bool _showMaximumColumn = true;
    private double _valueColumnWidth = 80;
    private double _minimumColumnWidth = 80;
    private double _maximumColumnWidth = 80;

    public HardwareTreeItemViewModel(string id) => Id = id;
    public string Id { get; }
    public MonitorTreeNodeKind Kind { get; private set; }
    public string HardwareId { get; private set; } = string.Empty;
    public string SensorType { get; private set; } = string.Empty;
    public TreeViewNode? Node { get; set; }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }
    public string SummaryText { get => _summaryText; private set => SetProperty(ref _summaryText, value); }
    public string ValueText { get => _valueText; private set => SetProperty(ref _valueText, value); }
    public string MinimumText { get => _minimumText; private set => SetProperty(ref _minimumText, value); }
    public string MaximumText { get => _maximumText; private set => SetProperty(ref _maximumText, value); }
    public string Report { get => _report; private set => SetProperty(ref _report, value); }
    public ObservableCollection<HardwarePropertyViewModel> Properties { get; } = new();
    public SensorReading? Sensor => _sensor;
    public bool IsHidden => _isHidden;
    public bool ShowValueColumn => _showValueColumn;
    public bool ShowMinimumColumn => _showMinimumColumn;
    public bool ShowMaximumColumn => _showMaximumColumn;
    public double ValueColumnWidth => _valueColumnWidth;
    public double MinimumColumnWidth => _minimumColumnWidth;
    public double MaximumColumnWidth => _maximumColumnWidth;
    public bool IsControllable => _sensor?.IsControllable == true;
    public bool IsCharted { get; private set; }
    public bool IsSensor => Kind == MonitorTreeNodeKind.Sensor;
    public string KindLabel => Kind switch { MonitorTreeNodeKind.Hardware => "硬件", MonitorTreeNodeKind.SensorType => "类型", _ => "传感器" };
    public string IconGlyph => Kind switch { MonitorTreeNodeKind.Hardware => "\uE950", MonitorTreeNodeKind.SensorType => "\uE8FD", _ => "\uE950" };

    public void UpdateHardware(HardwareNodeSnapshot snapshot, string summary)
    {
        Kind = MonitorTreeNodeKind.Hardware;
        HardwareId = snapshot.Id;
        _sensor = null;
        _isHidden = false;
        Title = snapshot.Name;
        Subtitle = snapshot.Type;
        SummaryText = summary;
        ValueText = string.Empty;
        MinimumText = string.Empty;
        MaximumText = string.Empty;
        Report = snapshot.Report;
        ReplaceProperties(snapshot.Properties);
        OnPropertyChanged(nameof(Sensor));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsControllable));
        OnPropertyChanged(nameof(IsSensor));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(IconGlyph));
    }

    public void UpdateType(string id, string hardwareId, string title, int sensorCount)
    {
        Kind = MonitorTreeNodeKind.SensorType;
        HardwareId = hardwareId;
        SensorType = title;
        _sensor = null;
        _isHidden = false;
        Title = title;
        Subtitle = $"{sensorCount} 个传感器";
        SummaryText = string.Empty;
        ValueText = string.Empty;
        MinimumText = string.Empty;
        MaximumText = string.Empty;
        Report = string.Empty;
        Properties.Clear();
        OnPropertyChanged(nameof(Sensor));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsControllable));
        OnPropertyChanged(nameof(IsSensor));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(IconGlyph));
    }

    public void UpdateSensor(SensorReading sensor, SensorPresentationSettings presentation)
    {
        Kind = MonitorTreeNodeKind.Sensor;
        HardwareId = sensor.HardwareId;
        SensorType = sensor.Type;
        _sensor = sensor;
        _isHidden = sensor.IsDefaultHidden || presentation.IsHidden;
        Title = string.IsNullOrWhiteSpace(presentation.DisplayName) ? sensor.DisplayName : presentation.DisplayName!;
        Subtitle = sensor.HardwareName;
        SummaryText = string.Empty;
        ValueText = MainViewModel.Format(sensor.Value, sensor.Unit);
        MinimumText = MainViewModel.Format(sensor.Minimum, sensor.Unit);
        MaximumText = MainViewModel.Format(sensor.Maximum, sensor.Unit);
        Report = string.Empty;
        Properties.Clear();
        IsCharted = presentation.ShowInChart;
        OnPropertyChanged(nameof(Sensor));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsControllable));
        OnPropertyChanged(nameof(IsCharted));
        OnPropertyChanged(nameof(IsSensor));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(IconGlyph));
    }

    public void UpdateColumnVisibility(AppSettings settings)
    {
        SetProperty(ref _showValueColumn, settings.ShowValueColumn, nameof(ShowValueColumn));
        SetProperty(ref _showMinimumColumn, settings.ShowMinimumColumn, nameof(ShowMinimumColumn));
        SetProperty(ref _showMaximumColumn, settings.ShowMaximumColumn, nameof(ShowMaximumColumn));
        SetProperty(ref _valueColumnWidth, GetColumnWidth(settings, "Value"), nameof(ValueColumnWidth));
        SetProperty(ref _minimumColumnWidth, GetColumnWidth(settings, "Minimum"), nameof(MinimumColumnWidth));
        SetProperty(ref _maximumColumnWidth, GetColumnWidth(settings, "Maximum"), nameof(MaximumColumnWidth));
    }

    private static double GetColumnWidth(AppSettings settings, string column) =>
        settings.ColumnWidths.TryGetValue(column, out var width) ? Math.Clamp(width, 64, 160) : 80;

    private void ReplaceProperties(IReadOnlyDictionary<string, string> properties)
    {
        Properties.Clear();
        foreach (var property in properties)
            Properties.Add(new HardwarePropertyViewModel(property.Key, property.Value));
    }
}

public sealed record HardwarePropertyViewModel(string Key, string Value);

public sealed class SensorRowViewModel
{
    public SensorRowViewModel(SensorReading reading, SensorPresentationSettings presentation)
    {
        SensorId = reading.Id;
        HardwareName = reading.HardwareName;
        HardwareType = reading.HardwareType;
        Name = string.IsNullOrWhiteSpace(presentation.DisplayName) ? reading.DisplayName : presentation.DisplayName!;
        Type = reading.Type;
        ValueText = MainViewModel.Format(reading.Value, reading.Unit);
        MinimumText = MainViewModel.Format(reading.Minimum, reading.Unit);
        MaximumText = MainViewModel.Format(reading.Maximum, reading.Unit);
        Unit = reading.Unit;
        IsHidden = reading.IsDefaultHidden || presentation.IsHidden;
        IsControllable = reading.IsControllable;
        IsCharted = presentation.ShowInChart;
        Parameters = reading.Parameters;
        MinimumControlValue = reading.MinimumControlValue;
        MaximumControlValue = reading.MaximumControlValue;
    }

    public string SensorId { get; }
    public string HardwareName { get; }
    public string HardwareType { get; }
    public string Name { get; }
    public string Type { get; }
    public string ValueText { get; }
    public string MinimumText { get; }
    public string MaximumText { get; }
    public string Unit { get; }
    public bool IsHidden { get; }
    public bool IsControllable { get; }
    public bool IsCharted { get; }
    public IReadOnlyList<ParameterReading> Parameters { get; }
    public double MinimumControlValue { get; }
    public double MaximumControlValue { get; }
}

public sealed class DashboardMetricViewModel
{
    public DashboardMetricViewModel(string title, string icon, string value, string description, string? sensorId)
    {
        Title = title;
        Icon = icon;
        Value = value;
        Description = description;
        SensorId = sensorId;
    }

    public string Title { get; }
    public string Icon { get; }
    public string Value { get; }
    public string Description { get; }
    public string? SensorId { get; }
}

public sealed class ControlRowViewModel
{
    public ControlRowViewModel(SensorRowViewModel row)
    {
        SensorId = row.SensorId;
        Name = row.Name;
        HardwareName = row.HardwareName;
        CurrentValue = row.ValueText;
        MinimumValue = row.MinimumControlValue;
        MaximumValue = row.MaximumControlValue;
        PendingValue = (MinimumValue + MaximumValue) / 2;
    }

    public string SensorId { get; }
    public string Name { get; }
    public string HardwareName { get; }
    public string CurrentValue { get; }
    public double MinimumValue { get; }
    public double MaximumValue { get; }
    public double PendingValue { get; set; }
}

public sealed class ChartSeriesViewModel
{
    public ChartSeriesViewModel(SensorReading sensor, IReadOnlyList<DataPoint> points)
    {
        SensorId = sensor.Id;
        Name = sensor.HardwareName + " - " + sensor.DisplayName;
        ValueText = sensor.Value is null ? "--" : $"{sensor.Value:0.##} {sensor.Unit}".TrimEnd();
        Points = points;
    }

    public string SensorId { get; }
    public string Name { get; }
    public string ValueText { get; }
    public IReadOnlyList<DataPoint> Points { get; }
}

public sealed class ChartCandidateViewModel
{
    public ChartCandidateViewModel(SensorReading sensor, bool isSelected)
    {
        SensorId = sensor.Id;
        Name = sensor.HardwareName + " - " + sensor.DisplayName;
        Type = sensor.Type;
        IsSelected = isSelected;
    }

    public string SensorId { get; }
    public string Name { get; }
    public string Type { get; }
    public bool IsSelected { get; set; }
}
