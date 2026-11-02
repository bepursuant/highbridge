using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Highbridge.Configuration;
using Highbridge.Consent;
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
        IScannerBackend backend,
        IConsentService consent,
        SettingsStore settings,
        HighbridgePolicy policy,
        TimeProvider? time = null,
        ILoggerFactory? loggerFactory = null,
        int port = FixedPort,
        ProvenanceIdentity? identity = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        Backend = backend;
        Consent = consent;
        Settings = settings;
        Policy = policy;
        Port = port;
        Origins = new OriginRegistry(settings, policy, time);
        Jobs = new ScanJobManager(
            backend,
            Origins,
            time,
            logger: loggerFactory.CreateLogger<ScanJobManager>(),
            provenance: new ProvenanceStamping(policy.ProvenanceStamp, identity));
        Logger = loggerFactory.CreateLogger<HighbridgeRuntime>();
    }

    public IScannerBackend Backend { get; }

    public IConsentService Consent { get; }

    public SettingsStore Settings { get; }

    public HighbridgePolicy Policy { get; }

    public OriginRegistry Origins { get; }

    public ScanJobManager Jobs { get; }

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
        if (!Origins.TryNormalize(command.Origin, out string origin) || !Origins.IsPaired(origin))
        {
            Logger.LogWarning("Rejected highbridge:// scan for unpaired origin {Origin}", command.Origin);
            return new ProtocolResult(ProtocolOutcome.OriginNotPaired, command.Origin);
        }

        try
        {
            var job = Jobs.Start(origin, command.Options, ScanTrigger.ProtocolUrl, command.RequestId);
            return new ProtocolResult(ProtocolOutcome.ScanStarted, job.Id);
        }
        catch (JobRejectedException ex)
        {
            Logger.LogWarning("Rejected highbridge:// scan: {Code}", ex.Code);
            return new ProtocolResult(ProtocolOutcome.Rejected, ex.Message);
        }
    }

    public void Dispose() => Jobs.Dispose();
}

public enum ProtocolOutcome
{
    Launched,
    ScanStarted,
    OriginNotPaired,
    Rejected,
    Invalid,
}

public sealed record ProtocolResult(ProtocolOutcome Outcome, string? Detail);
