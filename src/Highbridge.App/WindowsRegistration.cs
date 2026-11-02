using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Highbridge.App;

/// <summary>Per-user (HKCU) registration: no administrator rights needed.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsRegistration
{
    private const string ProtocolKey = @"Software\Classes\highbridge";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Highbridge";

    public static void RegisterProtocol(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ProtocolKey);
        key.SetValue(string.Empty, "URL:highbridge scanning");
        key.SetValue("URL Protocol", string.Empty);
        using (var icon = key.CreateSubKey("DefaultIcon"))
        {
            icon.SetValue(string.Empty, $"\"{exePath}\",0");
        }

        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
    }

    public static void UnregisterProtocol() => Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false);

    public static bool IsProtocolRegistered(string exePath)
    {
        using var command = Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\shell\open\command");
        return command?.GetValue(string.Empty) is string value && value.Contains(exePath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAutoStartEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(RunValue) is string;
    }

    public static void SetAutoStart(bool enabled, string exePath)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            run.SetValue(RunValue, $"\"{exePath}\" --background");
        }
        else
        {
            run.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
