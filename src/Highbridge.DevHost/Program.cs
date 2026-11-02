using System.Globalization;
using Microsoft.Extensions.Logging;
using Highbridge;
using Highbridge.Cards;
using Highbridge.Configuration;
using Highbridge.Consent;
using Highbridge.Hosting;
using Highbridge.Provenance;
using Highbridge.Scanning;
using Highbridge.Server;

// Headless highbridge host with the mock scanner. Runs anywhere .NET runs (Linux CI, macOS,
// Windows without a scanner). Consent prompts are answered automatically, so never point this
// at real hardware.

var port = HighbridgeRuntime.FixedPort;
bool allow = true;
var preApproved = new List<string>();
var urls = new List<string>();
int pageDelayMs = 400;
string? settingsPath = null;
StampMode stampMode = StampMode.Optional;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":
            port = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--consent":
            allow = args[++i] switch
            {
                "allow" => true,
                "deny" => false,
                var other => throw new ArgumentException($"--consent must be allow or deny, not '{other}'."),
            };
            break;
        case "--allow-origin":
            preApproved.Add(args[++i]);
            break;
        case "--page-delay-ms":
            pageDelayMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--settings":
            settingsPath = args[++i];
            break;
        case "--stamp":
            stampMode = args[++i] switch
            {
                "optional" => StampMode.Optional,
                "required" => StampMode.Required,
                "disabled" => StampMode.Disabled,
                var other => throw new ArgumentException($"--stamp must be optional, required or disabled, not '{other}'."),
            };
            break;
        case "--write-barcode-sheets":
            string folder = args[++i];
            Directory.CreateDirectory(folder);
            for (int page = 0; page < 3; page++)
            {
                string file = Path.Combine(folder, $"highbridge-barcode-sheet-{page + 1}.png");
                File.WriteAllBytes(file, Highbridge.Barcodes.MockBarcodeSheet.RenderPng(page, 3, 300));
                Console.WriteLine($"Wrote {file} (A4 at 300 dpi; print it on A4, fit to page)");
            }

            return 0;
        case "-h" or "--help":
            Console.WriteLine("""
                highbridge dev host (mock scanner and mock card reader)

                  --port <n>             API port (default 47116; the real app always uses 47116)
                  --consent allow|deny   auto-answer pairing prompts (default allow)
                  --allow-origin <url>   pre-approve an origin, like policy.json AllowedOrigins
                  --page-delay-ms <n>    simulated time per page (default 400)
                  --settings <path>      persist pairings to a JSON file (default: in memory)
                  --stamp <mode>         provenance stamp policy: optional (default), required, disabled
                  --write-barcode-sheets <folder>
                                         write the mock barcode test sheets as printable PNGs and exit
                  highbridge://...       forward a protocol URL to the running host (or handle it at startup)
                """);
            return 0;
        default:
            urls.Add(args[i]);
            break;
    }
}

string pipeName = $"{SingleInstanceChannel.DefaultPipeName}-dev-{port}";
if (urls.Count > 0 && await SingleInstanceChannel.TrySendAsync(urls.ToArray(), pipeName, TimeSpan.FromMilliseconds(500)))
{
    Console.WriteLine($"Forwarded {urls.Count} URL(s) to the running dev host.");
    return 0;
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("highbridge");

var policy = new HighbridgePolicy { AllowedOrigins = preApproved, ProvenanceStamp = new ProvenanceStampPolicy { Mode = stampMode } };
var settings = new SettingsStore(settingsPath);
var consent = new LoggingConsent(new FixedConsentService(allow), logger);
using var runtime = new HighbridgeRuntime(
    new MockScannerBackend(new MockScannerOptions { PageDelay = TimeSpan.FromMilliseconds(pageDelayMs) }),
    new MockCardReaderBackend(),
    consent,
    settings,
    policy,
    loggerFactory: loggerFactory,
    port: port);

runtime.Jobs.JobFinished += job => logger.LogInformation("Scan {Id} for {Origin}: {Status} ({Pages} pages)", job.Id, job.Origin, job.Status, job.Pages.Count);
runtime.CardReads.JobFinished += job => logger.LogInformation("Card read {Id} for {Origin}: {Status} {Code}", job.Id, job.Origin, job.Status, job.ErrorCode);

var app = HighbridgeServer.Build(runtime, b =>
{
    b.UseQuietLogging();
    b.Logging.ClearProviders();
    b.Logging.AddSimpleConsole(o => o.SingleLine = true);
});

await using var channel = new SingleInstanceChannel(pipeName);
channel.Start(forwarded =>
{
    foreach (string url in forwarded)
    {
        var result = runtime.HandleProtocolUrl(url);
        logger.LogInformation("highbridge URL -> {Outcome} {Detail}", result.Outcome, result.Detail);
    }

    return Task.CompletedTask;
});

foreach (string url in urls)
{
    var result = runtime.HandleProtocolUrl(url);
    logger.LogInformation("highbridge URL -> {Outcome} {Detail}", result.Outcome, result.Detail);
}

logger.LogInformation("highbridge dev host (mock scanner and mock card reader) listening on http://127.0.0.1:{Port}; consent={Consent}", port, allow ? "allow" : "deny");
await app.RunAsync();
return 0;

internal sealed class LoggingConsent(IConsentService inner, ILogger logger) : IConsentService
{
    public Task<bool> RequestPairingAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Pairing request from {Origin} (auto-answered)", request.Origin);
        return inner.RequestPairingAsync(request, cancellationToken);
    }
}
