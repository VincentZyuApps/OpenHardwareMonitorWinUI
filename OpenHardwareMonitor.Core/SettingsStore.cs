using System.Text.Json;

namespace OpenHardwareMonitor.Core;

public sealed class SettingsStore
{
    private const string FileName = "OpenHardwareMonitor.WinUI.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public SettingsStore(string? executableDirectory = null, string? localAppDataDirectory = null)
    {
        ExecutableDirectory = executableDirectory ?? AppContext.BaseDirectory;
        LocalAppDataDirectory = localAppDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenHardwareMonitorWinUI");
    }

    public string ExecutableDirectory { get; }
    public string LocalAppDataDirectory { get; }
    public string SettingsPath { get; private set; } = string.Empty;
    public async Task<AppSettings> LoadAsync(bool forcePortable = false, CancellationToken cancellationToken = default)
    {
        var portable = forcePortable || File.Exists(Path.Combine(ExecutableDirectory, ".portable"));
        SettingsPath = Path.Combine(portable ? ExecutableDirectory : LocalAppDataDirectory, FileName);

        if (File.Exists(SettingsPath))
        {
            AppSettings settings;
            await using (var stream = File.OpenRead(SettingsPath))
                settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken) ?? new AppSettings();
            var previousSchemaVersion = settings.SchemaVersion;
            settings.IsPortable = portable;
            Normalize(settings);
            if (previousSchemaVersion < AppSettings.CurrentSchemaVersion)
                await SaveAsync(settings, cancellationToken);
            return settings;
        }

        var created = new AppSettings { IsPortable = portable };
        Normalize(created);
        await SaveAsync(created, cancellationToken);
        return created;
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Normalize(settings);
        return SaveSnapshotAsync(CreateSnapshot(settings), cancellationToken);
    }

    private async Task SaveSnapshotAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            await Task.Run(async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath) ?? LocalAppDataDirectory);
                temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await using (var stream = File.Create(temporaryPath))
                    await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, SettingsPath, true);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            finally
            {
                _saveLock.Release();
            }
        }
    }

    private static AppSettings CreateSnapshot(AppSettings source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        Theme = source.Theme,
        IsPortable = source.IsPortable,
        CloseToTray = source.CloseToTray,
        StartMinimized = source.StartMinimized,
        GadgetEnabled = source.GadgetEnabled,
        ChartSelectionInitialized = source.ChartSelectionInitialized,
        ShowHiddenSensors = source.ShowHiddenSensors,
        ShowValueColumn = source.ShowValueColumn,
        ShowMinimumColumn = source.ShowMinimumColumn,
        ShowMaximumColumn = source.ShowMaximumColumn,
        NavigationPaneOpen = source.NavigationPaneOpen,
        RefreshIntervalMilliseconds = source.RefreshIntervalMilliseconds,
        Window = new WindowSettings
        {
            Width = source.Window.Width,
            Height = source.Window.Height
        },
        HardwareInfoWindow = new WindowPlacementSettings
        {
            Width = source.HardwareInfoWindow.Width,
            Height = source.HardwareInfoWindow.Height,
            X = source.HardwareInfoWindow.X,
            Y = source.HardwareInfoWindow.Y
        },
        Hardware = new HardwareSettings
        {
            Motherboard = source.Hardware.Motherboard,
            Cpu = source.Hardware.Cpu,
            Memory = source.Hardware.Memory,
            Gpu = source.Hardware.Gpu,
            Storage = source.Hardware.Storage,
            Network = source.Hardware.Network,
            Battery = source.Hardware.Battery,
            Controller = source.Hardware.Controller,
            Psu = source.Hardware.Psu
        },
        Logging = new LoggingSettings
        {
            Enabled = source.Logging.Enabled,
            IntervalSeconds = source.Logging.IntervalSeconds,
            Directory = source.Logging.Directory
        },
        WebServer = new WebServerSettings
        {
            Enabled = source.WebServer.Enabled,
            Host = source.WebServer.Host,
            Port = source.WebServer.Port,
            RequireAuthentication = source.WebServer.RequireAuthentication,
            UserName = source.WebServer.UserName,
            PasswordSha256 = source.WebServer.PasswordSha256
        },
        Sensors = source.Sensors.ToDictionary(
            pair => pair.Key,
            pair => new SensorPresentationSettings
            {
                IsHidden = pair.Value.IsHidden,
                ShowInTray = pair.Value.ShowInTray,
                ShowInGadget = pair.Value.ShowInGadget,
                ShowInChart = pair.Value.ShowInChart,
                DisplayName = pair.Value.DisplayName
            },
            StringComparer.OrdinalIgnoreCase),
        ExpandedNodes = new Dictionary<string, bool>(source.ExpandedNodes, StringComparer.OrdinalIgnoreCase),
        ColumnWidths = new Dictionary<string, int>(source.ColumnWidths, StringComparer.OrdinalIgnoreCase)
    };

    private static void Normalize(AppSettings settings)
    {
        var previousSchemaVersion = settings.SchemaVersion;
        if (previousSchemaVersion < 3 && settings.Window is { Width: 1180, Height: 760 })
        {
            settings.Window.Width = 980;
            settings.Window.Height = 680;
        }
        if (previousSchemaVersion < 3)
        {
            settings.ExpandedNodes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            settings.ShowValueColumn = true;
            settings.ShowMinimumColumn = true;
            settings.ShowMaximumColumn = true;
            settings.ColumnWidths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        settings.Window ??= new WindowSettings();
        settings.HardwareInfoWindow ??= new WindowPlacementSettings();
        settings.Hardware ??= new HardwareSettings();
        settings.Logging ??= new LoggingSettings();
        settings.WebServer ??= new WebServerSettings();
        settings.Sensors ??= new Dictionary<string, SensorPresentationSettings>(StringComparer.OrdinalIgnoreCase);
        settings.ExpandedNodes ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        settings.ColumnWidths ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var sensorId in settings.Sensors
                     .Where(pair => IsDefault(pair.Value))
                     .Select(pair => pair.Key)
                     .ToArray())
            settings.Sensors.Remove(sensorId);
        foreach (var nodeId in settings.ExpandedNodes
                     .Where(pair => !pair.Value)
                     .Select(pair => pair.Key)
                     .ToArray())
            settings.ExpandedNodes.Remove(nodeId);
        foreach (var column in settings.ColumnWidths.Keys.ToArray())
            settings.ColumnWidths[column] = Math.Clamp(
                settings.ColumnWidths[column],
                AppSettings.MinimumHardwareColumnWidth,
                AppSettings.MaximumHardwareColumnWidth);
        settings.RefreshIntervalMilliseconds = Math.Clamp(settings.RefreshIntervalMilliseconds, 250, 10_000);
        settings.Window.Width = Math.Clamp(settings.Window.Width, 800, 3840);
        settings.Window.Height = Math.Clamp(settings.Window.Height, 560, 2160);
        settings.HardwareInfoWindow.Width = Math.Clamp(settings.HardwareInfoWindow.Width, 420, 1920);
        settings.HardwareInfoWindow.Height = Math.Clamp(settings.HardwareInfoWindow.Height, 360, 1440);
        settings.Logging.IntervalSeconds = Math.Clamp(settings.Logging.IntervalSeconds, 1, 3600);
        settings.WebServer.Port = Math.Clamp(settings.WebServer.Port, 1024, 65535);
    }

    private static bool IsDefault(SensorPresentationSettings settings) =>
        !settings.IsHidden &&
        !settings.ShowInTray &&
        !settings.ShowInGadget &&
        !settings.ShowInChart &&
        string.IsNullOrWhiteSpace(settings.DisplayName);
}
