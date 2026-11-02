using System.IO;

namespace Highbridge.App;

internal static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\Highbridge\logs: highbridge.log and the scanner diagnostics.</summary>
    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Highbridge", "logs");
}
