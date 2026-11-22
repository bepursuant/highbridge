using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Highbridge.Barcodes;
using Highbridge.Cards;
using Highbridge.Configuration;
using Highbridge.Consent;
using Highbridge.Devices;
using Highbridge.Jobs;
using Highbridge.Protocol;
using Highbridge.Provenance;
using Highbridge.Scanning;
using Highbridge.Security;

namespace Highbridge;

/// <summary>Everything a host (tray app, dev host, tests) needs, wired together.</summary>
public sealed class HighbridgeRuntime : IDisposable
{
    /// <summary>
    /// The only port the app listens on. Web pages hard-code it, so it is deliberately not
    /// configurable and there is no fallback.
    /// </summary>
    public const int FixedPort = 47116;

    /// <param name="port">Only the dev host and tests pass this, to run several hosts side by side.</param>
    public HighbridgeRuntime(
        IScannerBackend scannerBackend,
        ICardReaderBackend cardReaderBackend,
        IConsentService consent,
        SettingsStore settings,
        HighbridgePolicy policy,
        TimeProvider? time = null,
        ILoggerFactory? loggerFactory = null,
        int port = FixedPort,
        ProvenanceIdentity? identity = null,
        BarcodeWorker? barcodeWorker = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        ScannerBackend = scannerBackend;
        CardReaderBackend = cardReaderBackend;
        Consent = consent;
        Settings = settings;
        Policy = policy;
        Port = port;
        Origins = new OriginRegistry(settings, policy, time);
        Jobs = new ScanJobManager(
            scannerBackend,
            Origins,
            time,
            logger: loggerFactory.CreateLogger<ScanJobManager>(),
            provenance: new ProvenanceStamping(policy.ProvenanceStamp, identity),
            barcodeWorker: barcodeWorker);
        CardReads = new CardReadManager(cardReaderBackend, Origins, policy, time, logger: loggerFactory.CreateLogger<CardReadManager>());
        Logger = loggerFactory.CreateLogger<HighbridgeRuntime>();
    }

    public IScannerBackend ScannerBackend { get; }

    public ICardReaderBackend CardReaderBackend { get; }

    public IConsentService Consent { get; }

    public SettingsStore Settings { get; }

    public HighbridgePolicy Policy { get; }

    public OriginRegistry Origins { get; }

    public ScanJobManager Jobs { get; }

    public CardReadManager CardReads { get; }

    public int Port { get; }

    private ILogger Logger { get; }

    /// <summary>Handles a highbridge:// URL received on the command line or from a second instance.</summary>
    public ProtocolResult HandleProtocolUrl(string url)
    {
        if (!HighbridgeUri.TryParse(url, out var command, out string? error))
        {
            Logger.LogWarning("Ignoring invalid highbridge URL: {Error}", error);
            return new ProtocolResult(ProtocolOutcome.Invalid, error);
        }

        if (command!.Action == ProtocolAction.Launch)
        {
            return new ProtocolResult(ProtocolOutcome.Launched, null);
        }

        // The origin in a protocol URL is an unverified claim. We only act on it for origins that
        // were already paired through the HTTP API (where the browser vouches for Origin), the
        // scanner waits until that origin claims the job over HTTP, and the result can only be
        // collected by that origin using the unguessable request id.
        if (command.Action == ProtocolAction.ReadCard && !Policy.CardReaderEnabled)
        {
            Logger.LogWarning("Rejected highbridge:// card read because the reader is disabled by policy");
            return new ProtocolResult(ProtocolOutcome.Rejected, CardReadManager.DisabledMessage);
        }

        if (!Origins.TryNormalize(command.Origin, out string origin) || !Origins.IsPaired(origin))
        {
            Logger.LogWarning("Rejected highbridge:// {Action} for unpaired origin {Origin}", command.Action, command.Origin);
            return new ProtocolResult(ProtocolOutcome.OriginNotPaired, command.Origin);
        }

        try
        {
            if (command.Action == ProtocolAction.ReadCard)
            {
                var card = Resolve(command.Fields, fields => DeviceOptions.ResolveCardAsync(Settings, CardReaderBackend, fields, CancellationToken.None), command.CardRead!);
                var read = CardReads.Start(origin, card, CardReadTrigger.ProtocolUrl, command.RequestId);
                return new ProtocolResult(ProtocolOutcome.CardReadStarted, read.Id);
            }

            var options = Resolve(command.Fields, fields => DeviceOptions.ResolveScanAsync(Settings, ScannerBackend, fields, CancellationToken.None), command.Options);
            var job = Jobs.Start(origin, options, ScanTrigger.ProtocolUrl, command.RequestId);
            return new ProtocolResult(ProtocolOutcome.ScanStarted, job.Id);
        }
        catch (JobRejectedException ex)
        {
            Logger.LogWarning("Rejected highbridge:// {Action}: {Code}", command.Action, ex.Code);
            return new ProtocolResult(ProtocolOutcome.Rejected, ex.Message);
        }
        catch (System.Text.Json.JsonException ex)
        {
            Logger.LogWarning(ex, "Rejected highbridge:// {Action}: the options were not valid", command.Action);
            return new ProtocolResult(ProtocolOutcome.Rejected, "The scan options were not valid.");
        }
    }

    /// <summary>
    /// Device defaults fill fields the link left out. Listing devices can touch TWAIN, so it runs
    /// off the caller's thread (the tray UI thread, when a link is opened).
    /// </summary>
    private static T Resolve<T>(System.Text.Json.JsonElement? fields, Func<System.Text.Json.JsonElement, Task<T>> resolve, T parsed)
    {
        if (fields is not { } request)
        {
            return parsed;
        }

        return Task.Run(() => resolve(request)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Jobs.Dispose();
        CardReads.Dispose();
    }
}

public enum ProtocolOutcome
{
    Launched,
    ScanStarted,
    CardReadStarted,
    OriginNotPaired,
    Rejected,
    Invalid,
}

public sealed record ProtocolResult(ProtocolOutcome Outcome, string? Detail);
