using System.Text.Json;
using OpenHardwareMonitor.Core;

namespace OpenHardwareMonitor.App.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task PortableSettingsAreStoredBesideExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "ohm-winui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SettingsStore(root, Path.Combine(root, "local"));

        var settings = await store.LoadAsync(forcePortable: true);
        settings.Theme = ThemePreference.Dark;
        await store.SaveAsync(settings);

        Assert.StartsWith(root, store.SettingsPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(store.SettingsPath));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task NewAppDoesNotReadLegacyXmlConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "ohm-winui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "OpenHardwareMonitor.config"), "<settings><entry name=\"mainForm.Width\" value=\"1920\"/></settings>");
        var store = new SettingsStore(root, Path.Combine(root, "local"));

        var settings = await store.LoadAsync();

        Assert.Equal(980, settings.Window.Width);
        Assert.False(File.Exists(Path.Combine(root, "OpenHardwareMonitor.config.pre-winui-backup")));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task PreviousDefaultWindowSizeMigratesToCompactDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "ohm-winui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SettingsStore(root, Path.Combine(root, "local"));
        await File.WriteAllTextAsync(Path.Combine(root, "OpenHardwareMonitor.WinUI.json"), """
            { "SchemaVersion": 2, "ShowMinimumColumn": false, "Window": { "Width": 1180, "Height": 760 }, "ExpandedNodes": { "/cpu/0": true } }
            """);

        var settings = await store.LoadAsync(forcePortable: true);

        Assert.Equal(980, settings.Window.Width);
        Assert.Equal(680, settings.Window.Height);
        Assert.Empty(settings.ExpandedNodes);
        Assert.True(settings.ShowMinimumColumn);
        var saved = await File.ReadAllTextAsync(store.SettingsPath);
        Assert.Contains("\"SchemaVersion\": 3", saved, StringComparison.Ordinal);
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task ConcurrentSavesLeaveOneValidSettingsFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "ohm-winui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SettingsStore(root, Path.Combine(root, "local"));
        var settings = await store.LoadAsync(forcePortable: true);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => store.SaveAsync(settings)));

        Assert.True(File.Exists(store.SettingsPath));
        var saved = await File.ReadAllTextAsync(store.SettingsPath);
        Assert.Contains("\"SchemaVersion\"", saved, StringComparison.Ordinal);
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task CurrentSchemaPreservesExpansionAndClampsColumnWidths()
    {
        var root = Path.Combine(Path.GetTempPath(), "ohm-winui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SettingsStore(root, Path.Combine(root, "local"));
        await File.WriteAllTextAsync(Path.Combine(root, "OpenHardwareMonitor.WinUI.json"), """
            { "SchemaVersion": 3, "ExpandedNodes": { "/cpu/0": true }, "ColumnWidths": { "Value": 8, "Maximum": 900 } }
            """);

        var settings = await store.LoadAsync(forcePortable: true);

        Assert.True(settings.ExpandedNodes["/cpu/0"]);
        Assert.Equal(64, settings.ColumnWidths["Value"]);
        Assert.Equal(160, settings.ColumnWidths["Maximum"]);
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task SaveCapturesStableSnapshotBeforeBackgroundWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "ohm-winui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SettingsStore(root, Path.Combine(root, "local"));
        var settings = await store.LoadAsync(forcePortable: true);
        settings.Theme = ThemePreference.Dark;
        settings.ExpandedNodes["/cpu/0"] = true;

        var save = store.SaveAsync(settings);
        settings.Theme = ThemePreference.Light;
        settings.ExpandedNodes["/cpu/0"] = false;
        await save;

        var saved = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(store.SettingsPath));
        Assert.NotNull(saved);
        Assert.Equal(ThemePreference.Dark, saved.Theme);
        Assert.True(saved.ExpandedNodes["/cpu/0"]);
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task SaveOmitsDefaultSensorPresentationAndCollapsedNodes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ohm-winui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SettingsStore(root, Path.Combine(root, "local"));
        var settings = await store.LoadAsync(forcePortable: true);
        settings.Sensors["default"] = new SensorPresentationSettings();
        settings.Sensors["charted"] = new SensorPresentationSettings { ShowInChart = true };
        settings.ExpandedNodes["collapsed"] = false;
        settings.ExpandedNodes["expanded"] = true;

        await store.SaveAsync(settings);

        var saved = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(store.SettingsPath));
        Assert.NotNull(saved);
        Assert.False(saved.Sensors.ContainsKey("default"));
        Assert.True(saved.Sensors["charted"].ShowInChart);
        Assert.False(saved.ExpandedNodes.ContainsKey("collapsed"));
        Assert.True(saved.ExpandedNodes["expanded"]);
        Directory.Delete(root, true);
    }
}
