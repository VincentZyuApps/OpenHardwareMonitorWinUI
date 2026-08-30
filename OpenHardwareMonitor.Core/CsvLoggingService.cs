using System.Globalization;
using System.Text;

namespace OpenHardwareMonitor.Core;

public sealed class CsvLoggingService
{
    private DateTimeOffset _lastLogAt = DateTimeOffset.MinValue;

    public async Task LogAsync(HardwareSnapshot snapshot, LoggingSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled || snapshot.Timestamp == DateTimeOffset.MinValue) return;
        if (snapshot.Timestamp - _lastLogAt < TimeSpan.FromSeconds(settings.IntervalSeconds)) return;

        var directory = string.IsNullOrWhiteSpace(settings.Directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenHardwareMonitorWinUI", "Logs")
            : settings.Directory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"OpenHardwareMonitor-{snapshot.Timestamp:yyyy-MM-dd}.csv");
        var createHeader = !File.Exists(path);
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        if (createHeader) await writer.WriteLineAsync("Timestamp,SensorId,Hardware,Sensor,Value,Unit");
        foreach (var sensor in snapshot.Sensors.Where(item => item.Value is not null))
        {
            var line = string.Join(",", new[]
            {
                snapshot.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                Escape(sensor.Id), Escape(sensor.HardwareName), Escape(sensor.DisplayName),
                sensor.Value!.Value.ToString("R", CultureInfo.InvariantCulture), Escape(sensor.Unit)
            });
            await writer.WriteLineAsync(line);
        }
        _lastLogAt = snapshot.Timestamp;
    }

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
