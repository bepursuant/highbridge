using System.Diagnostics;
using System.Globalization;
using System.IO;
using Highbridge.Hosting;
using Highbridge.Scanning;

namespace Highbridge.App;

internal static class Program
{
    private const string MutexName = @"Local\Highbridge.SingleInstance.v1";

    [STAThread]
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        string exePath = Environment.ProcessPath ?? Application.ExecutablePath;

        if (options.Register || options.Unregister)
        {
            return RunRegistration(options, exePath);
        }

        if (options.Exit)
        {
            return StopRunningInstance();
        }

        if (options.DiagnoseScanner || options.TestScan)
        {
            ApplicationConfiguration.Initialize();
            return ScannerTools.Run(options);
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isPrimary);
        if (!isPrimary)
        {
            if (options.Urls.Count > 0)
            {
                SingleInstanceChannel.TrySendAsync(options.Urls.ToArray()).GetAwaiter().GetResult();
            }

            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext(options);
        Application.Run(context);
        return 0;
    }

    /// <summary>
    /// Run by the MSI before it replaces or removes files: asks the instance in this session to
    /// quit, waits for its process to end, and kills it if it hangs.
    /// </summary>
    private static int StopRunningInstance()
    {
        using var self = Process.GetCurrentProcess();
        var running = Process.GetProcessesByName(self.ProcessName)
            .Where(p => p.Id != self.Id && p.SessionId == self.SessionId)
            .ToArray();
        if (running.Length == 0)
        {
            return 0;
        }

        SingleInstanceChannel.TrySendAsync([AppOptions.ExitCommand]).GetAwaiter().GetResult();
        foreach (var process in running)
        {
            using (process)
            {
                if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
                {
                    process.Kill();
                    process.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
        }

        return 0;
    }

    private static int RunRegistration(AppOptions options, string exePath)
    {
        try
        {
            if (options.Register)
            {
                WindowsRegistration.RegisterProtocol(exePath);
                if (options.AutoStart)
                {
                    WindowsRegistration.SetAutoStart(true, exePath);
                }
            }
            else
            {
                WindowsRegistration.UnregisterProtocol();
                WindowsRegistration.SetAutoStart(false, exePath);
            }

            if (!options.Quiet)
            {
                MessageBox.Show(
                    options.Register ? $"highbridge:// links now open\n{exePath}" : "highbridge:// links are no longer handled.",
                    "highbridge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            if (!options.Quiet)
            {
                MessageBox.Show(ex.Message, "highbridge registration failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 1;
        }
    }
}

internal sealed class AppOptions
{
    /// <summary>Sent over the single-instance pipe to make the running instance quit.</summary>
    public const string ExitCommand = "--exit";

    public List<string> Urls { get; } = [];

    public bool Register { get; private set; }

    public bool Unregister { get; private set; }

    public bool AutoStart { get; private set; }

    public bool Quiet { get; private set; }

    public bool Exit { get; private set; }

    public bool UseMockScanner { get; private set; }

    /// <summary>Started by the sign-in Run key: stay silent until needed.</summary>
    public bool Background { get; private set; }

    /// <summary>Write a TWAIN report (managers, sources, capabilities) to the log folder, then exit.</summary>
    public bool DiagnoseScanner { get; private set; }

    /// <summary>Scan once with the options below, save the pages to the log folder, then exit.</summary>
    public bool TestScan { get; private set; }

    public int? Dpi { get; private set; }

    public ColorMode? ColorMode { get; private set; }

    public PaperSource? PaperSource { get; private set; }

    public string? ScannerId { get; private set; }

    public string ToolDescription =>
        $"scanner {ScannerId ?? "(default)"}, dpi {Dpi?.ToString(CultureInfo.InvariantCulture) ?? "(default)"}, color {ColorMode?.ToString() ?? "(default)"}, source {PaperSource?.ToString() ?? "(default)"}";

    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();

        // Launched by the URL handler ("Highbridge.exe" "%1"): treat everything as URLs so a
        // crafted link can never smuggle in command-line switches.
        if (args.Length > 0 && args[0].StartsWith("highbridge:", StringComparison.OrdinalIgnoreCase))
        {
            options.Urls.AddRange(args.Where(a => a.StartsWith("highbridge:", StringComparison.OrdinalIgnoreCase)));
            return options;
        }

        foreach (string arg in args)
        {
            if (TryParseValue(options, arg))
            {
                continue;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--register": options.Register = true; break;
                case "--unregister": options.Unregister = true; break;
                case "--autostart": options.AutoStart = true; break;
                case "--quiet": options.Quiet = true; break;
                case "--mock": options.UseMockScanner = true; break;
                case "--background": options.Background = true; break;
                case ExitCommand: options.Exit = true; break;
                case "--diagnose-scanner": options.DiagnoseScanner = true; break;
                case "--test-scan": options.TestScan = true; break;
            }
        }

        return options;
    }

    private static bool TryParseValue(AppOptions options, string arg)
    {
        int equals = arg.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0)
        {
            return false;
        }

        string value = arg[(equals + 1)..];
        switch (arg[..equals].ToLowerInvariant())
        {
            case "--dpi" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int dpi):
                options.Dpi = Math.Clamp(dpi, ScanOptions.MinDpi, ScanOptions.MaxDpi);
                return true;
            case "--color":
                options.ColorMode = value.ToLowerInvariant() switch
                {
                    "bw" or "blackwhite" => Scanning.ColorMode.BlackWhite,
                    "gray" or "grey" => Scanning.ColorMode.Gray,
                    _ => Scanning.ColorMode.Color,
                };
                return true;
            case "--source":
                options.PaperSource = value.ToLowerInvariant() switch
                {
                    "flatbed" => Scanning.PaperSource.Flatbed,
                    "feeder" => Scanning.PaperSource.Feeder,
                    _ => Scanning.PaperSource.Auto,
                };
                return true;
            case "--scanner":
                options.ScannerId = value.Length > 0 ? value : null;
                return true;
            default:
                return false;
        }
    }
}
