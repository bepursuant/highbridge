using System.Globalization;
using Microsoft.Extensions.Logging;
using Highbridge.Scanning;
using Highbridge.Twain;

// Usage: Highbridge.TwainProbe [--diagnose] [--color=bw|gray|color] [--dpi=N] [--source=auto|flatbed|feeder]
//        [--max-pages=N] [--idle-timeout=SECONDS] [--out=DIR]
// Prints "RESULT ok pages=N" or "RESULT error code=..." as its last line.
var settings = args.Select(a => a.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : string.Empty, StringComparer.OrdinalIgnoreCase);
string Get(string name, string fallback) => settings.TryGetValue(name, out var value) ? value : fallback;

var timeouts = settings.ContainsKey("--idle-timeout")
    ? TwainTimeouts.Default with { Idle = TimeSpan.FromSeconds(double.Parse(Get("--idle-timeout", "120"), CultureInfo.InvariantCulture)) }
    : TwainTimeouts.Default;

using var backend = new TwainScannerBackend(new ConsoleLogger<TwainScannerBackend>(), timeouts);
try
{
    if (settings.ContainsKey("--diagnose"))
    {
        string report = await backend.DiagnoseAsync(CancellationToken.None);
        Console.WriteLine(report);
        Console.WriteLine("RESULT ok diagnose");
        return 0;
    }

    var scanners = await backend.ListScannersAsync(CancellationToken.None);
    foreach (var scanner in scanners)
    {
        Console.WriteLine($"scanner: {scanner.Name} ({scanner.Manufacturer}) default={scanner.IsDefault}");
    }

    var options = new ScanOptions
    {
        ColorMode = Get("--color", "color") switch { "bw" => ColorMode.BlackWhite, "gray" => ColorMode.Gray, _ => ColorMode.Color },
        Dpi = int.Parse(Get("--dpi", "200"), CultureInfo.InvariantCulture),
        Source = Get("--source", "auto") switch { "flatbed" => PaperSource.Flatbed, "feeder" => PaperSource.Feeder, _ => PaperSource.Auto },
        MaxPages = int.Parse(Get("--max-pages", "0"), CultureInfo.InvariantCulture),
    };
    var pages = await backend.ScanAsync(options, null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
    string outDir = Get("--out", ".");
    foreach (var page in pages)
    {
        string path = Path.Combine(outDir, $"page-{page.Index + 1}.png");
        File.WriteAllBytes(path, page.Data);
        Console.WriteLine($"page {page.Index + 1}: {page.Width} x {page.Height}, {page.Data.Length} bytes -> {path}");
    }

    Console.WriteLine($"RESULT ok pages={pages.Count}");
    return 0;
}
catch (ScannerException ex)
{
    Console.WriteLine($"RESULT error code={ex.Code} message={ex.Message}");
    return 1;
}
catch (TimeoutException)
{
    Console.WriteLine("RESULT error code=stalled message=no result after 2 minutes");
    return 1;
}

internal sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Console.WriteLine($"[{logLevel}] {formatter(state, exception)}{(exception is null ? string.Empty : $" | {exception.GetType().Name}: {exception.Message}")}");
}
