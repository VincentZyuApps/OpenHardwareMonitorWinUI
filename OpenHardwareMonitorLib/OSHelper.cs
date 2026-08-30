namespace OpenHardwareMonitor;

/// <summary>
/// Windows-only runtime facts used by the hardware library.
/// The legacy helper from SergiyE.Common is not compatible with modern .NET.
/// </summary>
internal static class OSHelper
{
    public static bool IsUnix => false;
    public static bool Is64Bit => Environment.Is64BitProcess;
    public static bool IsWindows8OrGreater => Environment.OSVersion.Version >= new Version(6, 2);

    public static bool IsCompatible(bool requireAdministrator, out string errorMessage, out Action fixAction)
    {
        errorMessage = string.Empty;
        fixAction = null;
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
    }
}
