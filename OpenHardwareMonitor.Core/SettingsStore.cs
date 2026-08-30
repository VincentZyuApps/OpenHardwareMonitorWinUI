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

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            Normalize(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath) ?? LocalAppDataDirectory);
            temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            File.Move(temporaryPath, SettingsPath, true);
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
            _saveLock.Release();
        }
    }

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
        foreach (var column in settings.ColumnWidths.Keys.ToArray())
            settings.ColumnWidths[column] = Math.Clamp(settings.ColumnWidths[column], 64, 160);
        settings.RefreshIntervalMilliseconds = Math.Clamp(settings.RefreshIntervalMilliseconds, 250, 10_000);
        settings.Window.Width = Math.Clamp(settings.Window.Width, 800, 3840);
        settings.Window.Height = Math.Clamp(settings.Window.Height, 560, 2160);
        settings.HardwareInfoWindow.Width = Math.Clamp(settings.HardwareInfoWindow.Width, 420, 1920);
        settings.HardwareInfoWindow.Height = Math.Clamp(settings.HardwareInfoWindow.Height, 360, 1440);
        settings.Logging.IntervalSeconds = Math.Clamp(settings.Logging.IntervalSeconds, 1, 3600);
        settings.WebServer.Port = Math.Clamp(settings.WebServer.Port, 1024, 65535);
    }
}
