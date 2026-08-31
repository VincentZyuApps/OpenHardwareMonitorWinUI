using System.Collections.ObjectModel;
using System.Diagnostics;
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

public enum HardwareToolbarOperation
{
    None,
    Refresh,
    ShowHiddenSensors,
    ExpandAll,
    CollapseAll
}

internal enum TreeNodeExpansionOutcome
{
    Completed,
    Canceled,
    AlreadyRealized
}

internal sealed record TreeNodeExpansionRequest(
    TreeViewNode Node,
    string NodeId,
    int IntentRevision,
    bool UserInitiated,
    bool NeedsRealization,
    bool TracksInteraction,
    CancellationTokenSource? CancellationSource)
{
    public CancellationToken Token => CancellationSource?.Token ?? CancellationToken.None;
}

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly SensorPresentationSettings DefaultSensorPresentation = new();
    private readonly SettingsStore _settingsStore;
    private readonly HardwareMonitorService _hardware;
    private readonly CsvLoggingService _logger;
    private readonly RemoteWebServer _webServer;
    private readonly Dictionary<string, TreeViewNode> _treeNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HardwareTreeItemViewModel> _treeItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _programmaticExpansionChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LazyTreeNodeState> _lazyTreeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _internalExpansionEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _internalCollapseEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _treeMutationGate = new(1, 1);
    private readonly object _refreshSync = new();
    private readonly object _treeSettingsSaveSync = new();
    private Task<bool> _activeRefreshTask = Task.FromResult(true);
    private Task _pendingTreeSettingsSave = Task.CompletedTask;
    private CancellationTokenSource? _treeSettingsSaveCancellation;
    private bool _initialized;
    private bool _isFlushingTreeSettings;
    private int _hardwareToolbarOperationActive;
    private int _activeTreeNodeLoadCount;

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
    [ObservableProperty] private HardwareToolbarOperation _activeHardwareToolbarOperation;

    public bool IsHardwareToolbarBusy => ActiveHardwareToolbarOperation != HardwareToolbarOperation.None;
    public bool IsHardwareTreeBusy => IsHardwareToolbarBusy || Volatile.Read(ref _activeTreeNodeLoadCount) > 0;

    public event EventHandler? ThemeChanged;
    public event EventHandler? SettingsLoaded;

    partial void OnShowHiddenSensorsChanged(bool value) => Settings.ShowHiddenSensors = value;

    partial void OnActiveHardwareToolbarOperationChanged(HardwareToolbarOperation value) =>
        OnPropertyChanged(nameof(IsHardwareToolbarBusy));

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

    public Task<bool> RefreshAsync()
    {
        if (!_initialized) return Task.FromResult(false);
        lock (_refreshSync)
        {
            if (!_activeRefreshTask.IsCompleted) return _activeRefreshTask;
            _activeRefreshTask = RefreshCoreAsync();
            return _activeRefreshTask;
        }
    }

    private async Task<bool> RefreshCoreAsync()
    {
        try
        {
            var snapshot = await _hardware.RefreshAsync();
            await _logger.LogAsync(snapshot, Settings.Logging);
            await _treeMutationGate.WaitAsync();
            try
            {
                await ProjectSnapshotAsync(snapshot);
            }
            finally
            {
                _treeMutationGate.Release();
            }
            StatusText = snapshot.Timestamp == DateTimeOffset.MinValue
                ? "等待传感器数据"
                : $"{snapshot.Sensors.Count} 个传感器 | 上次更新 {snapshot.Timestamp:HH:mm:ss}";
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
            StatusText = $"读取硬件数据失败: {exception.Message}";
            return false;
        }
    }

    public bool TryBeginHardwareToolbarOperation(HardwareToolbarOperation operation)
    {
        if (operation == HardwareToolbarOperation.None) return false;
        if (Interlocked.CompareExchange(ref _hardwareToolbarOperationActive, 1, 0) != 0) return false;
        ActiveHardwareToolbarOperation = operation;
        return true;
    }

    public void EndHardwareToolbarOperation(HardwareToolbarOperation operation)
    {
        if (ActiveHardwareToolbarOperation != operation) return;
        ActiveHardwareToolbarOperation = HardwareToolbarOperation.None;
        Volatile.Write(ref _hardwareToolbarOperationActive, 0);
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
        Settings.ColumnWidths.TryGetValue(column, out var width)
            ? Math.Clamp(width, AppSettings.MinimumHardwareColumnWidth, AppSettings.MaximumHardwareColumnWidth)
            : AppSettings.DefaultHardwareColumnWidth;

    public void PreviewHardwareColumnWidth(string column, double width)
    {
        Settings.ColumnWidths[column] = (int)Math.Round(Math.Clamp(
            width,
            AppSettings.MinimumHardwareColumnWidth,
            AppSettings.MaximumHardwareColumnWidth));
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
        GetOrCreateSensorPresentation(sensorId).ShowInChart = visible;
        var candidate = ChartCandidates.FirstOrDefault(item => string.Equals(item.SensorId, sensorId, StringComparison.OrdinalIgnoreCase));
        if (candidate is not null) candidate.IsSelected = visible;
        await SaveSettingsAsync();
        RebuildCharts(Snapshot);
    }

    public async Task SetSensorHiddenAsync(string sensorId, bool hidden)
    {
        await _treeMutationGate.WaitAsync();
        try
        {
            GetOrCreateSensorPresentation(sensorId).IsHidden = hidden;
            await SaveSettingsAsync();
            await RebuildHardwareTreeAsync(Snapshot, new UiWorkBudget());
            ProjectFlatRows(Snapshot);
        }
        finally
        {
            _treeMutationGate.Release();
        }
    }

    public async Task SetSensorDisplayNameAsync(string sensorId, string? displayName)
    {
        await _treeMutationGate.WaitAsync();
        try
        {
            GetOrCreateSensorPresentation(sensorId).DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
            await SaveSettingsAsync();
            await ProjectSnapshotAsync(Snapshot);
        }
        finally
        {
            _treeMutationGate.Release();
        }
    }

    public async Task SetSensorTrayVisibleAsync(string sensorId, bool visible)
    {
        GetOrCreateSensorPresentation(sensorId).ShowInTray = visible;
        await SaveSettingsAsync();
    }

    public async Task SetSensorGadgetVisibleAsync(string sensorId, bool visible)
    {
        GetOrCreateSensorPresentation(sensorId).ShowInGadget = visible;
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

    internal TreeNodeExpansionRequest? BeginTreeNodeExpansion(TreeViewNode node)
    {
        if (node.Content is not HardwareTreeItemViewModel item) return null;

        node.IsExpanded = true;
        var userInitiated = !(_programmaticExpansionChanges.Remove(item.Id, out var expected) && expected);
        if (_internalExpansionEvents.Remove(item.Id)) return null;
        var state = GetLazyTreeState(item.Id);
        var revision = ++state.IntentRevision;
        CancelSafely(state.LoadCancellation);

        if (userInitiated && string.IsNullOrWhiteSpace(SensorFilter))
        {
            Settings.ExpandedNodes[item.Id] = true;
            ScheduleTreeSettingsSave();
        }

        var needsRealization = !state.IsRealized && state.DesiredChildren.Count > 0;
        var tracksInteraction = state.DesiredChildren.Count > 0;
        CancellationTokenSource? cancellationSource = null;
        if (tracksInteraction)
        {
            cancellationSource = new CancellationTokenSource();
            state.LoadCancellation = cancellationSource;
            IncrementTreeBusyCount();
            try
            {
                item.SetExpansionBusy(true);
            }
            catch (Exception exception)
            {
                AppLog.Write(exception);
            }
        }

        return new TreeNodeExpansionRequest(
            node,
            item.Id,
            revision,
            userInitiated,
            needsRealization,
            tracksInteraction,
            cancellationSource);
    }

    internal async Task<TreeNodeExpansionOutcome> RealizeTreeNodeChildrenAsync(TreeNodeExpansionRequest request)
    {
        if (!request.NeedsRealization) return TreeNodeExpansionOutcome.AlreadyRealized;

        try
        {
            await _treeMutationGate.WaitAsync(request.Token);
        }
        catch (OperationCanceledException)
        {
            return TreeNodeExpansionOutcome.Canceled;
        }

        try
        {
            var state = GetLazyTreeState(request.NodeId);
            if (!IsCurrentExpansion(request, state)) return TreeNodeExpansionOutcome.Canceled;

            var budget = new UiWorkBudget();
            var completed = await ReconcileChildrenAsync(
                request.Node,
                state.DesiredChildren,
                budget,
                () => IsCurrentExpansion(request, state));
            if (!completed || !IsCurrentExpansion(request, state))
            {
                state.IsRealized = false;
                request.Node.HasUnrealizedChildren = state.DesiredChildren.Count > 0;
                return TreeNodeExpansionOutcome.Canceled;
            }

            state.IsRealized = true;
            request.Node.HasUnrealizedChildren = false;
            ApplyDesiredExpansionToChildren(state.DesiredChildren);
            return TreeNodeExpansionOutcome.Completed;
        }
        catch
        {
            if (_lazyTreeStates.TryGetValue(request.NodeId, out var state))
            {
                state.IsRealized = false;
                request.Node.HasUnrealizedChildren = state.DesiredChildren.Count > 0;
            }
            throw;
        }
        finally
        {
            _treeMutationGate.Release();
        }
    }

    internal void EndTreeNodeExpansion(TreeNodeExpansionRequest request)
    {
        var hasState = _lazyTreeStates.TryGetValue(request.NodeId, out var state);
        try
        {
            if (hasState && state!.IntentRevision == request.IntentRevision &&
                request.Node.Content is HardwareTreeItemViewModel item)
                item.SetExpansionBusy(false);
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
        }
        finally
        {
            if (hasState && ReferenceEquals(state!.LoadCancellation, request.CancellationSource))
                state.LoadCancellation = null;
            request.CancellationSource?.Dispose();
            if (request.TracksInteraction) DecrementTreeBusyCount();
        }
    }

    internal bool SetTreeNodeCollapsed(TreeViewNode node)
    {
        if (node.Content is not HardwareTreeItemViewModel item) return false;

        node.IsExpanded = false;
        _internalExpansionEvents.Remove(item.Id);
        var userInitiated = !(_programmaticExpansionChanges.Remove(item.Id, out var expected) && !expected);
        var internalCollapse = _internalCollapseEvents.Remove(item.Id);
        var state = GetLazyTreeState(item.Id);
        state.IntentRevision++;
        CancelSafely(state.LoadCancellation);
        try
        {
            item.SetExpansionBusy(false);
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
        }

        if (userInitiated && string.IsNullOrWhiteSpace(SensorFilter))
        {
            Settings.ExpandedNodes.Remove(item.Id);
            ScheduleTreeSettingsSave();
        }

        var moveSelectionToParent = SelectedTreeItem is { } selected &&
                                    ContainsDesiredDescendant(node, selected.Id);
        if (moveSelectionToParent)
        {
            try
            {
                SelectedTreeItem = item;
            }
            catch (Exception exception)
            {
                AppLog.Write(exception);
            }
        }

        if (!internalCollapse && state.DesiredChildren.Count > 0)
            _ = UnrealizeCollapsedSubtreeAsync(node, state.IntentRevision);
        return moveSelectionToParent;
    }

    public async Task ExpandAllAsync(bool expanded)
    {
        await _treeMutationGate.WaitAsync();
        try
        {
            var budget = new UiWorkBudget();
            foreach (var root in HardwareTreeNodes)
                await SetExpandedRecursiveAsync(root, expanded, budget);
        }
        finally
        {
            _treeMutationGate.Release();
        }
        await SaveSettingsAsync();
    }

    public async Task SetShowHiddenSensorsAsync(bool value)
    {
        await _treeMutationGate.WaitAsync();
        try
        {
            ShowHiddenSensors = value;
            await RebuildHardwareTreeAsync(Snapshot, new UiWorkBudget());
            await SaveSettingsAsync();
        }
        finally
        {
            _treeMutationGate.Release();
        }
    }

    public async Task SetSensorFilterAsync(string value)
    {
        await _treeMutationGate.WaitAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(SensorFilter) && string.IsNullOrWhiteSpace(value))
                foreach (var root in HardwareTreeNodes) ApplyStoredExpansionBeforeFilterClear(root);
            SensorFilter = value;
            await RebuildHardwareTreeAsync(Snapshot, new UiWorkBudget());
        }
        finally
        {
            _treeMutationGate.Release();
        }
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

    public async Task FlushPendingSettingsAsync()
    {
        Task pendingSave;
        CancellationTokenSource? cancellationSource;
        lock (_treeSettingsSaveSync)
        {
            _isFlushingTreeSettings = true;
            cancellationSource = _treeSettingsSaveCancellation;
            pendingSave = _pendingTreeSettingsSave;
        }

        CancelSafely(cancellationSource);
        try
        {
            await pendingSave;
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
        }
        await SaveSettingsAsync();
    }

    private void ScheduleTreeSettingsSave()
    {
        var cancellationSource = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_treeSettingsSaveSync)
        {
            if (_isFlushingTreeSettings)
            {
                cancellationSource.Dispose();
                return;
            }
            previous = _treeSettingsSaveCancellation;
            _treeSettingsSaveCancellation = cancellationSource;
            _pendingTreeSettingsSave = SaveTreeSettingsAfterDelayAsync(_pendingTreeSettingsSave, cancellationSource);
        }
        CancelSafely(previous);
    }

    private async Task SaveTreeSettingsAfterDelayAsync(Task predecessor, CancellationTokenSource cancellationSource)
    {
        try
        {
            await predecessor;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationSource.Token);
            await SaveSettingsAsync();
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
        }
        finally
        {
            lock (_treeSettingsSaveSync)
            {
                if (ReferenceEquals(_treeSettingsSaveCancellation, cancellationSource))
                    _treeSettingsSaveCancellation = null;
            }
            cancellationSource.Dispose();
        }
    }

    private void NotifyHardwareColumnWidthsChanged()
    {
        OnPropertyChanged(nameof(ValueColumnWidth));
        OnPropertyChanged(nameof(MinimumColumnWidth));
        OnPropertyChanged(nameof(MaximumColumnWidth));
    }

    private async Task ProjectSnapshotAsync(HardwareSnapshot snapshot)
    {
        ProjectFlatRows(snapshot);
        EnsureDefaultChartSelections(snapshot);
        RebuildChartCandidates(snapshot);
        RebuildCharts(snapshot);
        await RebuildHardwareTreeAsync(snapshot, new UiWorkBudget());
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

        ReconcileControlRows(rows.Where(row => row.IsControllable).ToArray());
        RebuildDashboard(rows);
    }

    private void ReconcileControlRows(IReadOnlyList<SensorRowViewModel> rows) =>
        ReconcileById(
            ControlRows,
            rows,
            item => item.SensorId,
            source => source.SensorId,
            source => new ControlRowViewModel(source),
            (item, source) => item.Update(source));

    private async Task RebuildHardwareTreeAsync(HardwareSnapshot snapshot, UiWorkBudget budget)
    {
        var liveNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var liveSensorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hardware in snapshot.Hardware)
            CollectLiveTreeNodeIds(hardware, liveNodeIds, liveSensorIds);

        if (snapshot.Hardware.Count == 0)
        {
            await ReconcileRootNodesAsync(Array.Empty<TreeViewNode>(), budget);
            if (PruneTreeCaches(liveNodeIds, liveSensorIds)) ScheduleTreeSettingsSave();
            return;
        }

        var roots = new List<TreeViewNode>();
        foreach (var hardware in snapshot.Hardware)
        {
            var root = await BuildHardwareNodeAsync(hardware, budget);
            if (root is not null) roots.Add(root);
            await budget.YieldIfNeededAsync();
        }
        await ReconcileRootNodesAsync(roots, budget);
        if (PruneTreeCaches(liveNodeIds, liveSensorIds)) ScheduleTreeSettingsSave();
    }

    private async Task<TreeViewNode?> BuildHardwareNodeAsync(HardwareNodeSnapshot snapshot, UiWorkBudget budget)
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
            desiredChildren.Add(await BuildTypeNodeAsync(snapshot, group.Key, typeTitle, sensors, budget));
        }

        foreach (var child in snapshot.Children)
        {
            var childNode = await BuildHardwareNodeAsync(child, budget);
            if (childNode is not null) desiredChildren.Add(childNode);
        }

        if (!string.IsNullOrWhiteSpace(SensorFilter) && !hardwareMatches && desiredChildren.Count == 0) return null;
        var item = GetTreeItem(snapshot.Id);
        item.UpdateHardware(snapshot, BuildHardwareSummary(snapshot));
        var node = GetTreeNode(item, defaultExpanded: false);
        await UpdateLazyChildrenAsync(node, desiredChildren, budget);
        await budget.YieldIfNeededAsync();
        return node;
    }

    private async Task<TreeViewNode> BuildTypeNodeAsync(
        HardwareNodeSnapshot hardware,
        string sensorType,
        string title,
        IReadOnlyList<SensorReading> sensors,
        UiWorkBudget budget)
    {
        var id = $"{hardware.Id}/type/{sensorType}";
        var item = GetTreeItem(id);
        item.UpdateType(id, hardware.Id, title, sensors.Count);
        var node = GetTreeNode(item, defaultExpanded: false);
        var desiredChildren = new List<TreeViewNode>(sensors.Count);
        foreach (var sensor in sensors)
        {
            var sensorItem = GetTreeItem(sensor.Id);
            sensorItem.UpdateSensor(sensor, GetSensorPresentation(sensor.Id));
            desiredChildren.Add(GetTreeNode(sensorItem, defaultExpanded: false));
            await budget.YieldIfNeededAsync();
        }
        await UpdateLazyChildrenAsync(node, desiredChildren, budget);
        return node;
    }

    private async Task ReconcileRootNodesAsync(IReadOnlyList<TreeViewNode> desired, UiWorkBudget budget)
    {
        var currentIds = HardwareTreeNodes.Select(GetNodeId).ToArray();
        var desiredIds = desired.Select(GetNodeId).ToArray();
        if (currentIds.SequenceEqual(desiredIds, StringComparer.OrdinalIgnoreCase)) return;

        var desiredNodes = desired.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var index = HardwareTreeNodes.Count - 1; index >= 0; index--)
        {
            if (desiredNodes.Contains(HardwareTreeNodes[index])) continue;
            var removed = HardwareTreeNodes[index];
            HardwareTreeNodes.RemoveAt(index);
            await PrepareDetachedRootForReuseAsync(removed, budget);
            await budget.YieldIfNeededAsync();
        }

        for (var index = 0; index < desired.Count; index++)
        {
            if (index < HardwareTreeNodes.Count && ReferenceEquals(HardwareTreeNodes[index], desired[index])) continue;
            var currentIndex = HardwareTreeNodes.IndexOf(desired[index]);
            if (currentIndex >= 0) HardwareTreeNodes.Move(currentIndex, index);
            else HardwareTreeNodes.Insert(index, desired[index]);
            await budget.YieldIfNeededAsync();
        }
    }

    private async Task PrepareDetachedRootForReuseAsync(TreeViewNode root, UiWorkBudget budget)
    {
        var rootId = GetNodeId(root);
        if (!_lazyTreeStates.TryGetValue(rootId, out var state)) return;

        state.IntentRevision++;
        CancelSafely(state.LoadCancellation);
        if (root.Content is HardwareTreeItemViewModel item)
        {
            try
            {
                item.SetExpansionBusy(false);
            }
            catch (Exception exception)
            {
                AppLog.Write(exception);
            }
        }

        if (SelectedTreeItem is { } selected &&
            (string.Equals(rootId, selected.Id, StringComparison.OrdinalIgnoreCase) ||
             ContainsDesiredDescendant(root, selected.Id)))
            SelectedTreeItem = null;

        if (root.IsExpanded)
        {
            _internalCollapseEvents.Add(rootId);
            try
            {
                SetExpandedProgrammatically(root, rootId, false);
            }
            finally
            {
                _internalCollapseEvents.Remove(rootId);
            }
        }

        await UnrealizeSubtreeCoreAsync(root, budget, () => true);
    }

    private static void CollectLiveTreeNodeIds(
        HardwareNodeSnapshot hardware,
        ISet<string> nodeIds,
        ISet<string> sensorIds)
    {
        nodeIds.Add(hardware.Id);
        foreach (var sensor in hardware.Sensors)
        {
            nodeIds.Add($"{hardware.Id}/type/{sensor.Type}");
            nodeIds.Add(sensor.Id);
            sensorIds.Add(sensor.Id);
        }
        foreach (var child in hardware.Children)
            CollectLiveTreeNodeIds(child, nodeIds, sensorIds);
    }

    private bool PruneTreeCaches(IReadOnlySet<string> liveNodeIds, IReadOnlySet<string> liveSensorIds)
    {
        var settingsChanged = false;
        foreach (var nodeId in Settings.ExpandedNodes.Keys
                     .Where(nodeId => !liveNodeIds.Contains(nodeId) || liveSensorIds.Contains(nodeId))
                     .ToArray())
        {
            Settings.ExpandedNodes.Remove(nodeId);
            settingsChanged = true;
        }

        if (SelectedTreeItem is { } selected && !liveNodeIds.Contains(selected.Id))
            SelectedTreeItem = null;

        foreach (var nodeId in _treeNodes.Keys.Where(nodeId => !liveNodeIds.Contains(nodeId)).ToArray())
        {
            if (_lazyTreeStates.Remove(nodeId, out var state)) CancelSafely(state.LoadCancellation);
            if (_treeItems.Remove(nodeId, out var item)) item.Node = null;
            _treeNodes.Remove(nodeId);
            _programmaticExpansionChanges.Remove(nodeId);
            _internalExpansionEvents.Remove(nodeId);
            _internalCollapseEvents.Remove(nodeId);
        }
        return settingsChanged;
    }

    private TreeViewNode GetTreeNode(HardwareTreeItemViewModel item, bool defaultExpanded)
    {
        item.UpdateColumnVisibility(Settings);
        if (_treeNodes.TryGetValue(item.Id, out var existing))
        {
            if (!ReferenceEquals(existing.Content, item)) existing.Content = item;
            item.Node = existing;
            return existing;
        }

        var node = new TreeViewNode
        {
            Content = item,
            IsExpanded = defaultExpanded
        };
        _treeNodes[item.Id] = node;
        item.Node = node;
        return node;
    }

    private void SetExpandedProgrammatically(TreeViewNode node, string id, bool expanded)
    {
        if (node.IsExpanded == expanded) return;
        _programmaticExpansionChanges[id] = expanded;
        try
        {
            node.IsExpanded = expanded;
        }
        finally
        {
            _programmaticExpansionChanges.Remove(id);
        }
    }

    internal void ApplyDesiredRootExpansions()
    {
        foreach (var root in HardwareTreeNodes) ApplyDesiredExpansionTree(root);
    }

    public (int SensorCount, int HardwareCount) CountProjectedTreeItems()
    {
        var sensorCount = 0;
        var hardwareCount = 0;
        foreach (var root in HardwareTreeNodes)
            CountProjectedTreeItems(root, ref sensorCount, ref hardwareCount);
        return (sensorCount, hardwareCount);
    }

    private void CountProjectedTreeItems(TreeViewNode node, ref int sensorCount, ref int hardwareCount)
    {
        if (node.Content is HardwareTreeItemViewModel item)
        {
            if (item.IsSensor) sensorCount++;
            else if (item.Kind == MonitorTreeNodeKind.Hardware) hardwareCount++;
        }

        foreach (var child in GetDesiredChildren(node))
            CountProjectedTreeItems(child, ref sensorCount, ref hardwareCount);
    }

    private async Task UpdateLazyChildrenAsync(TreeViewNode parent, IReadOnlyList<TreeViewNode> desired, UiWorkBudget budget)
    {
        var state = GetLazyTreeState(GetNodeId(parent));
        state.DesiredChildren = desired.ToArray();

        if (desired.Count == 0)
        {
            await ReconcileChildrenAsync(parent, desired, budget);
            state.IsRealized = true;
            parent.HasUnrealizedChildren = false;
            return;
        }

        if (state.IsRealized)
        {
            state.IsRealized = await ReconcileChildrenAsync(parent, state.DesiredChildren, budget);
            parent.HasUnrealizedChildren = !state.IsRealized;
            return;
        }

        await TrimUnrealizedChildrenAsync(parent, state.DesiredChildren, budget);
        var matchesDesired = parent.Children.Select(GetNodeId)
            .SequenceEqual(state.DesiredChildren.Select(GetNodeId), StringComparer.OrdinalIgnoreCase);
        state.IsRealized = matchesDesired;
        parent.HasUnrealizedChildren = !matchesDesired;
    }

    private static async Task TrimUnrealizedChildrenAsync(
        TreeViewNode parent,
        IReadOnlyList<TreeViewNode> desired,
        UiWorkBudget budget)
    {
        var desiredNodes = desired.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var index = parent.Children.Count - 1; index >= 0; index--)
        {
            if (desiredNodes.Contains(parent.Children[index])) continue;
            parent.Children.RemoveAt(index);
            await budget.YieldIfNeededAsync();
        }
    }

    private async Task<bool> ReconcileChildrenAsync(
        TreeViewNode parent,
        IReadOnlyList<TreeViewNode> desired,
        UiWorkBudget budget,
        Func<bool>? canContinue = null)
    {
        if (canContinue is not null && !canContinue()) return false;
        var currentIds = parent.Children.Select(GetNodeId).ToArray();
        var desiredIds = desired.Select(GetNodeId).ToArray();
        if (currentIds.SequenceEqual(desiredIds, StringComparer.OrdinalIgnoreCase)) return true;

        var desiredNodes = desired.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var index = parent.Children.Count - 1; index >= 0; index--)
        {
            if (canContinue is not null && !canContinue()) return false;
            if (desiredNodes.Contains(parent.Children[index])) continue;
            parent.Children.RemoveAt(index);
            await budget.YieldIfNeededAsync();
            if (canContinue is not null && !canContinue()) return false;
        }

        for (var index = 0; index < desired.Count; index++)
        {
            if (canContinue is not null && !canContinue()) return false;
            if (index < parent.Children.Count && ReferenceEquals(parent.Children[index], desired[index])) continue;
            var currentIndex = parent.Children.IndexOf(desired[index]);
            if (currentIndex >= 0) parent.Children.RemoveAt(currentIndex);
            parent.Children.Insert(index, desired[index]);
            await budget.YieldIfNeededAsync();
            if (canContinue is not null && !canContinue()) return false;
        }
        return true;
    }

    private LazyTreeNodeState GetLazyTreeState(string nodeId)
    {
        if (_lazyTreeStates.TryGetValue(nodeId, out var state)) return state;
        state = new LazyTreeNodeState();
        _lazyTreeStates[nodeId] = state;
        return state;
    }

    private IReadOnlyList<TreeViewNode> GetDesiredChildren(TreeViewNode node) =>
        _lazyTreeStates.TryGetValue(GetNodeId(node), out var state)
            ? state.DesiredChildren
            : node.Children.ToArray();

    private bool IsCurrentExpansion(TreeNodeExpansionRequest request, LazyTreeNodeState state) =>
        state.IntentRevision == request.IntentRevision &&
        !request.Token.IsCancellationRequested &&
        request.Node.IsExpanded;

    private async Task UnrealizeCollapsedSubtreeAsync(TreeViewNode node, int intentRevision)
    {
        IncrementTreeBusyCount();
        try
        {
            await Task.Delay(24);
            await _treeMutationGate.WaitAsync();
            try
            {
                var state = GetLazyTreeState(GetNodeId(node));
                if (state.IntentRevision != intentRevision || node.IsExpanded)
                    return;

                var budget = new UiWorkBudget();
                await UnrealizeSubtreeCoreAsync(
                    node,
                    budget,
                    () => state.IntentRevision == intentRevision && !node.IsExpanded);
            }
            finally
            {
                _treeMutationGate.Release();
            }
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
        }
        finally
        {
            DecrementTreeBusyCount();
        }
    }

    private async Task<bool> UnrealizeSubtreeCoreAsync(
        TreeViewNode node,
        UiWorkBudget budget,
        Func<bool> canContinue)
    {
        if (!canContinue()) return false;
        var state = GetLazyTreeState(GetNodeId(node));
        state.IsRealized = false;
        node.HasUnrealizedChildren = state.DesiredChildren.Count > 0;

        foreach (var child in state.DesiredChildren)
        {
            if (!canContinue()) return false;
            if (!_lazyTreeStates.TryGetValue(GetNodeId(child), out var childState) ||
                childState.DesiredChildren.Count == 0)
                continue;

            childState.IntentRevision++;
            CancelSafely(childState.LoadCancellation);
            if (child.Content is HardwareTreeItemViewModel childItem)
            {
                try
                {
                    childItem.SetExpansionBusy(false);
                }
                catch (Exception exception)
                {
                    AppLog.Write(exception);
                }
            }

            if (child.IsExpanded)
            {
                var childId = GetNodeId(child);
                _internalCollapseEvents.Add(childId);
                try
                {
                    SetExpandedProgrammatically(child, childId, false);
                }
                finally
                {
                    _internalCollapseEvents.Remove(childId);
                }
            }
            await UnrealizeSubtreeCoreAsync(child, budget, canContinue);
        }

        while (node.Children.Count > 0)
        {
            if (!canContinue()) return false;
            node.Children.RemoveAt(node.Children.Count - 1);
            await budget.YieldIfNeededAsync();
        }
        return canContinue();
    }

    private bool ContainsDesiredDescendant(TreeViewNode node, string nodeId)
    {
        foreach (var child in GetDesiredChildren(node))
        {
            if (string.Equals(GetNodeId(child), nodeId, StringComparison.OrdinalIgnoreCase) ||
                ContainsDesiredDescendant(child, nodeId))
                return true;
        }
        return false;
    }

    private void IncrementTreeBusyCount()
    {
        Interlocked.Increment(ref _activeTreeNodeLoadCount);
        NotifyHardwareTreeBusyChanged();
    }

    private void DecrementTreeBusyCount()
    {
        Interlocked.Decrement(ref _activeTreeNodeLoadCount);
        NotifyHardwareTreeBusyChanged();
    }

    private void NotifyHardwareTreeBusyChanged()
    {
        try
        {
            OnPropertyChanged(nameof(IsHardwareTreeBusy));
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
        }
    }

    private static void CancelSafely(CancellationTokenSource? cancellationSource)
    {
        if (cancellationSource is null) return;
        try
        {
            cancellationSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ApplyDesiredExpansionTree(TreeViewNode node)
    {
        var nodeId = GetNodeId(node);
        if (!_lazyTreeStates.TryGetValue(nodeId, out var state) || state.DesiredChildren.Count == 0) return;
        var expanded = !string.IsNullOrWhiteSpace(SensorFilter) ||
                       Settings.ExpandedNodes.TryGetValue(nodeId, out var stored) && stored;

        var loadInProgress = state.LoadCancellation is { IsCancellationRequested: false };
        if (expanded && node.IsExpanded && !state.IsRealized && !loadInProgress)
        {
            _internalCollapseEvents.Add(nodeId);
            try
            {
                SetExpandedProgrammatically(node, nodeId, false);
            }
            finally
            {
                _internalCollapseEvents.Remove(nodeId);
            }
        }
        if (loadInProgress) return;
        SetExpandedProgrammatically(node, nodeId, expanded);
        if (!expanded || !state.IsRealized) return;
        foreach (var child in state.DesiredChildren) ApplyDesiredExpansionTree(child);
    }

    private void ApplyDesiredExpansionToChildren(IEnumerable<TreeViewNode> children)
    {
        foreach (var child in children) ApplyDesiredExpansionTree(child);
    }

    private void ApplyStoredExpansionBeforeFilterClear(TreeViewNode node)
    {
        var nodeId = GetNodeId(node);
        if (!_lazyTreeStates.TryGetValue(nodeId, out var state) || state.DesiredChildren.Count == 0) return;
        var expanded = Settings.ExpandedNodes.TryGetValue(nodeId, out var stored) && stored;
        SetExpandedProgrammatically(node, nodeId, expanded);
        if (!expanded || !state.IsRealized) return;
        foreach (var child in state.DesiredChildren) ApplyStoredExpansionBeforeFilterClear(child);
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
            GetOrCreateSensorPresentation(sensor.Id).ShowInChart = true;
        Settings.ChartSelectionInitialized = true;
    }

    private void RebuildChartCandidates(HardwareSnapshot snapshot)
    {
        var sensors = snapshot.Sensors.Where(item => item.Value is not null && IsSensorVisible(item))
            .OrderBy(item => item.HardwareName)
            .ThenBy(item => item.Type)
            .ThenBy(item => item.DisplayName)
            .ToArray();
        ReconcileById(
            ChartCandidates,
            sensors,
            item => item.SensorId,
            source => source.Id,
            source => new ChartCandidateViewModel(source, GetSensorPresentation(source.Id)),
            (item, source) => item.Update(source, GetSensorPresentation(source.Id)));
    }

    private void RebuildCharts(HardwareSnapshot snapshot)
    {
        var sensors = snapshot.Sensors.Where(item => GetSensorPresentation(item.Id).ShowInChart).Take(8).ToArray();
        ReconcileById(
            ChartSeries,
            sensors,
            item => item.SensorId,
            source => source.Id,
            source => new ChartSeriesViewModel(source, GetSensorPresentation(source.Id), _hardware.GetHistory(source.Id)),
            (item, source) => item.Update(source, GetSensorPresentation(source.Id), _hardware.GetHistory(source.Id)));
    }

    private static void ReconcileById<TItem, TSource>(
        ObservableCollection<TItem> target,
        IReadOnlyList<TSource> sources,
        Func<TItem, string> itemId,
        Func<TSource, string> sourceId,
        Func<TSource, TItem> create,
        Action<TItem, TSource> update)
        where TItem : class
    {
        var existing = new Dictionary<string, TItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in target) existing.TryAdd(itemId(item), item);

        var desired = new List<TItem>(sources.Count);
        foreach (var source in sources)
        {
            if (!existing.TryGetValue(sourceId(source), out var item)) item = create(source);
            else update(item, source);
            desired.Add(item);
        }

        var desiredItems = desired.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!desiredItems.Contains(target[index])) target.RemoveAt(index);
        }

        for (var index = 0; index < desired.Count; index++)
        {
            if (index < target.Count && ReferenceEquals(target[index], desired[index])) continue;
            var currentIndex = target.IndexOf(desired[index]);
            if (currentIndex >= 0) target.Move(currentIndex, index);
            else target.Insert(index, desired[index]);
        }
    }

    private SensorPresentationSettings GetSensorPresentation(string sensorId) =>
        Settings.Sensors.TryGetValue(sensorId, out var value) ? value : DefaultSensorPresentation;

    private SensorPresentationSettings GetOrCreateSensorPresentation(string sensorId)
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

    private async Task SetExpandedRecursiveAsync(TreeViewNode node, bool expanded, UiWorkBudget budget)
    {
        var desiredChildren = GetDesiredChildren(node);
        if (desiredChildren.Count == 0 || node.Content is not HardwareTreeItemViewModel item) return;

        var state = GetLazyTreeState(item.Id);
        state.IntentRevision++;
        CancelSafely(state.LoadCancellation);
        try
        {
            item.SetExpansionBusy(false);
        }
        catch (Exception exception)
        {
            AppLog.Write(exception);
        }

        if (expanded)
        {
            Settings.ExpandedNodes[item.Id] = true;
            if (!node.IsExpanded) _internalExpansionEvents.Add(item.Id);
            try
            {
                SetExpandedProgrammatically(node, item.Id, true);
            }
            finally
            {
                _internalExpansionEvents.Remove(item.Id);
            }
            state.IsRealized = await ReconcileChildrenAsync(node, desiredChildren, budget);
            node.HasUnrealizedChildren = !state.IsRealized;
            await budget.YieldIfNeededAsync(force: true);
        }

        foreach (var child in desiredChildren)
            await SetExpandedRecursiveAsync(child, expanded, budget);

        if (!expanded)
        {
            Settings.ExpandedNodes.Remove(item.Id);
            _internalExpansionEvents.Remove(item.Id);
            if (node.IsExpanded) _internalCollapseEvents.Add(item.Id);
            try
            {
                SetExpandedProgrammatically(node, item.Id, false);
                state.IsRealized = false;
                node.HasUnrealizedChildren = true;
                while (node.Children.Count > 0)
                {
                    node.Children.RemoveAt(node.Children.Count - 1);
                    await budget.YieldIfNeededAsync();
                }
            }
            finally
            {
                _internalCollapseEvents.Remove(item.Id);
            }
            await budget.YieldIfNeededAsync(force: true);
        }
    }

    private sealed class LazyTreeNodeState
    {
        public IReadOnlyList<TreeViewNode> DesiredChildren { get; set; } = Array.Empty<TreeViewNode>();
        public bool IsRealized { get; set; }
        public int IntentRevision { get; set; }
        public CancellationTokenSource? LoadCancellation { get; set; }
    }

    private sealed class UiWorkBudget
    {
        private static readonly TimeSpan MaximumSlice = TimeSpan.FromMilliseconds(8);
        private const int MaximumOperationsPerSlice = 8;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _operations;

        public async ValueTask YieldIfNeededAsync(bool force = false)
        {
            _operations++;
            if (!force && _operations < MaximumOperationsPerSlice && _stopwatch.Elapsed < MaximumSlice) return;
            await Task.Delay(1);
            _stopwatch.Restart();
            _operations = 0;
        }
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
    private bool _isExpansionBusy;
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
    public bool IsExpansionBusy => _isExpansionBusy;
    public bool IsExpansionIdle => !_isExpansionBusy;
    public double ValueColumnWidth => _valueColumnWidth;
    public double MinimumColumnWidth => _minimumColumnWidth;
    public double MaximumColumnWidth => _maximumColumnWidth;
    public bool IsControllable => _sensor?.IsControllable == true;
    public bool IsCharted { get; private set; }
    public bool IsSensor => Kind == MonitorTreeNodeKind.Sensor;
    public string KindLabel => Kind switch { MonitorTreeNodeKind.Hardware => "硬件", MonitorTreeNodeKind.SensorType => "类型", _ => "传感器" };
    public string IconGlyph => Kind switch { MonitorTreeNodeKind.Hardware => "\uE950", MonitorTreeNodeKind.SensorType => "\uE8FD", _ => "\uE950" };

    public void SetExpansionBusy(bool value)
    {
        if (!SetProperty(ref _isExpansionBusy, value, nameof(IsExpansionBusy))) return;
        OnPropertyChanged(nameof(IsExpansionIdle));
    }

    public void UpdateHardware(HardwareNodeSnapshot snapshot, string summary)
    {
        var kindChanged = Kind != MonitorTreeNodeKind.Hardware;
        var hiddenChanged = _isHidden;
        var controllableChanged = IsControllable;
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
        if (hiddenChanged) OnPropertyChanged(nameof(IsHidden));
        if (controllableChanged) OnPropertyChanged(nameof(IsControllable));
        if (kindChanged)
        {
            OnPropertyChanged(nameof(IsSensor));
            OnPropertyChanged(nameof(KindLabel));
            OnPropertyChanged(nameof(IconGlyph));
        }
    }

    public void UpdateType(string id, string hardwareId, string title, int sensorCount)
    {
        var kindChanged = Kind != MonitorTreeNodeKind.SensorType;
        var hiddenChanged = _isHidden;
        var controllableChanged = IsControllable;
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
        if (hiddenChanged) OnPropertyChanged(nameof(IsHidden));
        if (controllableChanged) OnPropertyChanged(nameof(IsControllable));
        if (kindChanged)
        {
            OnPropertyChanged(nameof(IsSensor));
            OnPropertyChanged(nameof(KindLabel));
            OnPropertyChanged(nameof(IconGlyph));
        }
    }

    public void UpdateSensor(SensorReading sensor, SensorPresentationSettings presentation)
    {
        var kindChanged = Kind != MonitorTreeNodeKind.Sensor;
        var hidden = sensor.IsDefaultHidden || presentation.IsHidden;
        var hiddenChanged = _isHidden != hidden;
        var controllableChanged = IsControllable != sensor.IsControllable;
        var chartedChanged = IsCharted != presentation.ShowInChart;
        Kind = MonitorTreeNodeKind.Sensor;
        HardwareId = sensor.HardwareId;
        SensorType = sensor.Type;
        _sensor = sensor;
        _isHidden = hidden;
        Title = string.IsNullOrWhiteSpace(presentation.DisplayName) ? sensor.DisplayName : presentation.DisplayName!;
        Subtitle = sensor.HardwareName;
        SummaryText = string.Empty;
        ValueText = MainViewModel.Format(sensor.Value, sensor.Unit);
        MinimumText = MainViewModel.Format(sensor.Minimum, sensor.Unit);
        MaximumText = MainViewModel.Format(sensor.Maximum, sensor.Unit);
        Report = string.Empty;
        Properties.Clear();
        IsCharted = presentation.ShowInChart;
        if (hiddenChanged) OnPropertyChanged(nameof(IsHidden));
        if (controllableChanged) OnPropertyChanged(nameof(IsControllable));
        if (chartedChanged) OnPropertyChanged(nameof(IsCharted));
        if (kindChanged)
        {
            OnPropertyChanged(nameof(IsSensor));
            OnPropertyChanged(nameof(KindLabel));
            OnPropertyChanged(nameof(IconGlyph));
        }
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
        settings.ColumnWidths.TryGetValue(column, out var width)
            ? Math.Clamp(width, AppSettings.MinimumHardwareColumnWidth, AppSettings.MaximumHardwareColumnWidth)
            : AppSettings.DefaultHardwareColumnWidth;

    private void ReplaceProperties(IReadOnlyDictionary<string, string> properties)
    {
        if (Properties.Count == properties.Count)
        {
            var unchanged = true;
            var index = 0;
            foreach (var property in properties)
            {
                var current = Properties[index++];
                if (current.Key == property.Key && current.Value == property.Value) continue;
                unchanged = false;
                break;
            }
            if (unchanged) return;
        }
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

public sealed class ControlRowViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _hardwareName = string.Empty;
    private string _currentValue = string.Empty;
    private double _minimumValue;
    private double _maximumValue;
    private double _pendingValue;

    public ControlRowViewModel(SensorRowViewModel row)
    {
        SensorId = row.SensorId;
        Update(row);
        PendingValue = (MinimumValue + MaximumValue) / 2;
    }

    public string SensorId { get; }
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string HardwareName { get => _hardwareName; private set => SetProperty(ref _hardwareName, value); }
    public string CurrentValue { get => _currentValue; private set => SetProperty(ref _currentValue, value); }
    public double MinimumValue { get => _minimumValue; private set => SetProperty(ref _minimumValue, value); }
    public double MaximumValue { get => _maximumValue; private set => SetProperty(ref _maximumValue, value); }
    public double PendingValue { get => _pendingValue; set => SetProperty(ref _pendingValue, value); }

    public void Update(SensorRowViewModel row)
    {
        Name = row.Name;
        HardwareName = row.HardwareName;
        CurrentValue = row.ValueText;
        MinimumValue = row.MinimumControlValue;
        MaximumValue = row.MaximumControlValue;
    }
}

public sealed class ChartSeriesViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _valueText = string.Empty;
    private IReadOnlyList<DataPoint> _points = Array.Empty<DataPoint>();

    public ChartSeriesViewModel(SensorReading sensor, SensorPresentationSettings presentation, IReadOnlyList<DataPoint> points)
    {
        SensorId = sensor.Id;
        Update(sensor, presentation, points);
    }

    public string SensorId { get; }
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string ValueText { get => _valueText; private set => SetProperty(ref _valueText, value); }
    public IReadOnlyList<DataPoint> Points { get => _points; private set => SetProperty(ref _points, value); }

    public void Update(SensorReading sensor, SensorPresentationSettings presentation, IReadOnlyList<DataPoint> points)
    {
        var sensorName = string.IsNullOrWhiteSpace(presentation.DisplayName) ? sensor.DisplayName : presentation.DisplayName;
        Name = sensor.HardwareName + " - " + sensorName;
        ValueText = MainViewModel.Format(sensor.Value, sensor.Unit);
        Points = points;
    }
}

public sealed class ChartCandidateViewModel : ObservableObject
{
    private string _sensorName = string.Empty;
    private string _hardwareName = string.Empty;
    private string _typeLabel = string.Empty;
    private string _valueText = string.Empty;
    private string _fullName = string.Empty;
    private bool _isSelected;

    public ChartCandidateViewModel(SensorReading sensor, SensorPresentationSettings presentation)
    {
        SensorId = sensor.Id;
        Update(sensor, presentation);
    }

    public string SensorId { get; }
    public string SensorName { get => _sensorName; private set => SetProperty(ref _sensorName, value); }
    public string HardwareName { get => _hardwareName; private set => SetProperty(ref _hardwareName, value); }
    public string TypeLabel { get => _typeLabel; private set => SetProperty(ref _typeLabel, value); }
    public string ValueText { get => _valueText; private set => SetProperty(ref _valueText, value); }
    public string FullName { get => _fullName; private set => SetProperty(ref _fullName, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public void Update(SensorReading sensor, SensorPresentationSettings presentation)
    {
        SensorName = string.IsNullOrWhiteSpace(presentation.DisplayName) ? sensor.DisplayName : presentation.DisplayName!;
        HardwareName = sensor.HardwareName;
        TypeLabel = MainViewModel.GetSensorTypeName(sensor.Type);
        ValueText = MainViewModel.Format(sensor.Value, sensor.Unit);
        FullName = $"{HardwareName} - {SensorName}";
        IsSelected = presentation.ShowInChart;
    }
}
