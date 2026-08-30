namespace OpenHardwareMonitor.Core;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public bool IsPortable { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool GadgetEnabled { get; set; }
    public bool ChartSelectionInitialized { get; set; }
    public bool ShowHiddenSensors { get; set; }
    public bool ShowValueColumn { get; set; } = true;
    public bool ShowMinimumColumn { get; set; } = true;
    public bool ShowMaximumColumn { get; set; } = true;
    public bool NavigationPaneOpen { get; set; } = true;
    public int RefreshIntervalMilliseconds { get; set; } = 1000;
    public WindowSettings Window { get; set; } = new();
    public WindowPlacementSettings HardwareInfoWindow { get; set; } = new();
    public HardwareSettings Hardware { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public WebServerSettings WebServer { get; set; } = new();
    public Dictionary<string, SensorPresentationSettings> Sensors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> ExpandedNodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ColumnWidths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WindowSettings
{
    public int Width { get; set; } = 980;
    public int Height { get; set; } = 680;
}

public sealed class WindowPlacementSettings
{
    public int Width { get; set; } = 620;
    public int Height { get; set; } = 520;
    public int? X { get; set; }
    public int? Y { get; set; }
}

public sealed class HardwareSettings
{
    public bool Motherboard { get; set; } = true;
    public bool Cpu { get; set; } = true;
    public bool Memory { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Storage { get; set; } = true;
    public bool Network { get; set; } = true;
    public bool Battery { get; set; } = true;
    public bool Controller { get; set; }
    public bool Psu { get; set; }
}

public sealed class LoggingSettings
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 5;
    public string Directory { get; set; } = string.Empty;
}

public sealed class WebServerSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8085;
    public bool RequireAuthentication { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string PasswordSha256 { get; set; } = string.Empty;
}

public sealed class SensorPresentationSettings
{
    public bool IsHidden { get; set; }
    public bool ShowInTray { get; set; }
    public bool ShowInGadget { get; set; }
    public bool ShowInChart { get; set; }
    public string? DisplayName { get; set; }
}

public sealed record DataPoint(DateTimeOffset Timestamp, double Value);

public sealed record SensorReading(
    string Id,
    string HardwareId,
    string HardwareName,
    string HardwareType,
    string Name,
    string DisplayName,
    string Type,
    double? Value,
    double? Minimum,
    double? Maximum,
    string Unit,
    bool IsDefaultHidden,
    bool IsControllable,
    double MinimumControlValue,
    double MaximumControlValue,
    double? ControlValue,
    bool IsSoftwareControlled,
    double? LowLimit,
    double? HighLimit,
    double? CriticalLowLimit,
    double? CriticalHighLimit,
    IReadOnlyList<ParameterReading> Parameters);

public sealed record ParameterReading(
    string Id,
    string Name,
    string Description,
    double Value,
    double DefaultValue,
    bool IsDefault);

public sealed record HardwareNodeSnapshot(
    string Id,
    string Name,
    string Type,
    IReadOnlyDictionary<string, string> Properties,
    string Report,
    IReadOnlyList<SensorReading> Sensors,
    IReadOnlyList<HardwareNodeSnapshot> Children);

public sealed record HardwareSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<HardwareNodeSnapshot> Hardware,
    IReadOnlyList<SensorReading> Sensors)
{
    public static HardwareSnapshot Empty { get; } = new(DateTimeOffset.MinValue, Array.Empty<HardwareNodeSnapshot>(), Array.Empty<SensorReading>());
}
