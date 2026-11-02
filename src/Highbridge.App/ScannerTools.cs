using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using Highbridge.Scanning;
using Highbridge.Twain;

namespace Highbridge.App;

/// <summary>
/// <c>--diagnose-scanner</c> and <c>--test-scan</c>. Each runs in its own process, next to the tray
/// app if one is running: a fresh TWAIN manager load (so TWAINDSM.dll picks up its log setting),
/// and a crashing driver cannot take the tray app down.
/// </summary>
internal static class ScannerTools
{
    public const string DiagnosticsFileName = "scanner-diagnostics.txt";
    public const string ManagerLogFileName = "twaindsm.log";

    public static int Run(AppOptions options)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        TwainScannerBackend.EnableManagerLog(Path.Combine(AppPaths.LogDirectory, ManagerLogFileName));

        using var logProvider = new FileLoggerProvider(AppPaths.LogDirectory);
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logProvider).SetMinimumLevel(LogLevel.Information));
        var logger = loggerFactory.CreateLogger("highbridge.tools");
        logger.LogInformation("highbridge {Tool} started: {Args}", options.DiagnoseScanner ? "--diagnose-scanner" : "--test-scan", options.ToolDescription);

        string message;
        bool ok;
        try
        {
            using var backend = new TwainScannerBackend(loggerFactory.CreateLogger<TwainScannerBackend>());
            (ok, message) = options.DiagnoseScanner ? Diagnose(backend) : TestScan(backend, options, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The scanner tool failed");
            (ok, message) = (false, ex is ScannerException se ? $"{se.Message} ({se.Code})" : ex.Message);
        }

        logger.LogInformation("highbridge tool finished: {Outcome}", message);
        if (!options.Quiet)
        {
            MessageBox.Show(
                $"{message}\n\nThe log folder opens next. Please send highbridge.log, {DiagnosticsFileName} and {ManagerLogFileName} (if present) from:\n{AppPaths.LogDirectory}",
                "highbridge",
                MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            Process.Start(new ProcessStartInfo(AppPaths.LogDirectory) { UseShellExecute = true });
        }

        return ok ? 0 : 1;
    }

    private static (bool Ok, string Message) Diagnose(TwainScannerBackend backend)
    {
        string report = backend.DiagnoseAsync(CancellationToken.None).GetAwaiter().GetResult();
        string path = Path.Combine(AppPaths.LogDirectory, DiagnosticsFileName);
        File.WriteAllText(path, report);
        return (true, $"Scanner diagnostics written to {path}.");
    }

    private static (bool Ok, string Message) TestScan(TwainScannerBackend backend, AppOptions options, ILogger logger)
    {
        var scanOptions = new ScanOptions
        {
            ScannerId = options.ScannerId,
            Dpi = options.Dpi ?? 200,
            ColorMode = options.ColorMode ?? ColorMode.Color,
            Source = options.PaperSource ?? PaperSource.Auto,
        };
        var pages = backend.ScanAsync(scanOptions, null, CancellationToken.None).GetAwaiter().GetResult();
        var saved = new List<string>();
        foreach (var page in pages)
        {
            string path = Path.Combine(AppPaths.LogDirectory, string.Create(CultureInfo.InvariantCulture, $"test-scan-{page.Index + 1}.png"));
            File.WriteAllBytes(path, page.Data);
            saved.Add(path);
            logger.LogInformation("Saved {Path} ({Width} x {Height})", path, page.Width, page.Height);
        }

        return pages.Count == 0
            ? (false, "The scanner finished without returning a page.")
            : (true, $"Scanned {pages.Count} page(s): {string.Join(", ", saved.Select(Path.GetFileName))}.");
    }
}
