namespace OpenHardwareMonitor.App;

internal static class AppLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenHardwareMonitorWinUI", "app.log");

    public static void Write(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}");
        }
        catch
        {
            // An unhandled-exception logger must never mask the original failure.
        }
    }
}
