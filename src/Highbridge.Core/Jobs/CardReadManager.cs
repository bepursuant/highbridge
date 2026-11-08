using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Highbridge.Cards;
using Highbridge.Configuration;
using Highbridge.Security;

namespace Highbridge.Jobs;

/// <summary>
/// Owns card-read jobs. One read at a time, on a lock that is not the scanner's. The reader is not
/// touched until a protocol job is claimed. Results stay in memory until they expire.
/// </summary>
public sealed class CardReadManager : IDisposable
{
    public const string DisabledMessage = "Card reading is turned off by policy.";

    public const string ApprovalMessage =
        "Approve this site again so it can read cards. A card read sends the last four digits of the card number, the expiry, and the cardholder name.";

    /// <summary>Extra time after <see cref="CardReadOptions.WaitForCardMs"/> before a stuck read is given up.</summary>
    private static readonly TimeSpan ReadSlack = TimeSpan.FromSeconds(30);

    private readonly ICardReaderBackend _backend;
    private readonly OriginRegistry _origins;
    private readonly HighbridgePolicy _policy;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;
    private readonly TimeSpan _claimTimeout;
    private readonly ConcurrentDictionary<string, CardReadJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly ITimer _purgeTimer;
    private readonly ILogger _logger;

    public CardReadManager(
        ICardReaderBackend backend,
        OriginRegistry origins,
        HighbridgePolicy policy,
        TimeProvider? time = null,
        TimeSpan? retention = null,
        TimeSpan? claimTimeout = null,
        ILogger<CardReadManager>? logger = null)
    {
        _logger = logger ?? NullLogger<CardReadManager>.Instance;
        _backend = backend;
        _origins = origins;
        _policy = policy;
        _time = time ?? TimeProvider.System;
        _retention = retention ?? DeviceJobRules.DefaultRetention;
        _claimTimeout = claimTimeout ?? DeviceJobRules.DefaultClaimTimeout;
        _purgeTimer = _time.CreateTimer(_ => PurgeExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public event Action<CardReadJob>? JobFinished;

    public string BackendName => _backend.Name;

    public CardReadJob Start(string origin, CardReadOptions options, CardReadTrigger trigger, string? requestedId = null)
    {
        if (!_policy.CardReaderEnabled)
        {
            throw new JobRejectedException("card_reader_disabled", DisabledMessage);
        }

        var problems = options.Validate();
        if (problems.Count > 0)
        {
            throw new JobRejectedException("invalid_options", string.Join(" ", problems));
        }

        if (!_origins.IsPaired(origin))
        {
            throw new JobRejectedException("origin_not_paired", "This site is not paired with highbridge.");
        }

        if (!_origins.ApprovalCoversIdentity(origin))
        {
            throw new JobRejectedException("approval_required", ApprovalMessage);
        }

        PurgeExpired();
        if (_jobs.Count >= DeviceJobRules.MaxJobs)
        {
            throw new JobRejectedException("too_many_jobs", "Too many card reads are pending. Delete finished reads and retry.");
        }

        string id = requestedId ?? DeviceJobRules.NewId();
        if (!DeviceJobRules.IsValidRequestId(id))
        {
            throw new JobRejectedException("invalid_request_id", "request id must be 16-64 characters of [A-Za-z0-9_-].");
        }

        var job = new CardReadJob(id, origin, options, trigger, _time.GetUtcNow());
        if (!_jobs.TryAdd(id, job))
        {
            throw new JobRejectedException("duplicate_request_id", "A card read with this request id already exists.");
        }

        _logger.LogInformation(
            "Card read {JobId} started for {Origin} ({Trigger}): reader={Reader} interface={Interface} aid={Aid} waitMs={Wait}",
            job.Id,
            origin,
            trigger,
            options.ReaderId ?? "(any)",
            options.Interface,
            options.NormalizedAid ?? "(none)",
            options.WaitForCardMs);
        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    public CardReadJob? Get(string id, string origin) =>
        _jobs.TryGetValue(id, out var job) && job.Origin == origin ? job : null;

    public CardReadJob? Claim(string id, string origin)
    {
        var job = Get(id, origin);
        job?.Claim();
        return job;
    }

    public bool Delete(string id, string origin)
    {
        if (Get(id, origin) is not { } job)
        {
            return false;
        }

        job.Cancellation.Cancel();
        Complete(job, CardReadStatus.Canceled, "canceled", "The card read was canceled.");
        _jobs.TryRemove(id, out _);
        return true;
    }

    public Task<IReadOnlyList<CardReaderInfo>> ListReadersAsync(CancellationToken cancellationToken) =>
        _backend.ListReadersAsync(cancellationToken);

    public void PurgeExpired()
    {
        var cutoff = _time.GetUtcNow() - _retention;
        foreach (var job in _jobs.Values)
        {
            if (job.IsFinished && job.FinishedAt < cutoff)
            {
                _jobs.TryRemove(job.Id, out _);
            }
        }
    }

    public void Dispose()
    {
        _purgeTimer.Dispose();
        foreach (var job in _jobs.Values)
        {
            job.Cancellation.Cancel();
        }
    }

    private async Task RunAsync(CardReadJob job)
    {
        var ct = job.Cancellation.Token;
        try
        {
            if (!job.IsClaimed)
            {
                await job.Claimed.WaitAsync(_claimTimeout, _time, ct).ConfigureAwait(false);
            }

            await _readLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("highbridge:// card read {JobId} for {Origin} was never collected; not reading", job.Id, job.Origin);
            Complete(job, CardReadStatus.Canceled, "not_claimed", "No page from this site collected the card read, so it was not started.");
            return;
        }
        catch (OperationCanceledException)
        {
            Complete(job, CardReadStatus.Canceled, "canceled", "The card read was canceled.");
            return;
        }

        try
        {
            job.SetStatus(CardReadStatus.Reading);
            var budget = TimeSpan.FromMilliseconds(job.Options.WaitForCardMs) + ReadSlack;
            var result = await _backend.ReadAsync(job.Options, ct).WaitAsync(budget, _time, ct).ConfigureAwait(false);
            Complete(job, CardReadStatus.Completed, result);
        }
        catch (OperationCanceledException)
        {
            Complete(job, CardReadStatus.Canceled, "canceled", "The card read was canceled.");
        }
        catch (TimeoutException)
        {
            _logger.LogError("Card read {JobId} did not finish within the time limit; giving up on it", job.Id);
            job.Cancellation.Cancel();
            Complete(job, CardReadStatus.Failed, "read_timeout", "The card reader did not finish the read. Check the reader, then try again.");
        }
        catch (CardReaderException ex)
        {
            _logger.LogWarning("Card read {JobId} failed: {Code} {Message}", job.Id, ex.Code, ex.Message);
            Complete(job, CardReadStatus.Failed, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Card read {JobId} failed", job.Id);
            Complete(job, CardReadStatus.Failed, "read_failed", "The card could not be read.");
        }
        finally
        {
            _readLock.Release();
        }
    }

    private void Complete(CardReadJob job, CardReadStatus status, string code, string message) =>
        Complete(job, status, result: null, code, message);

    private void Complete(CardReadJob job, CardReadStatus status, CardReadResult result) =>
        Complete(job, status, result, null, null);

    private void Complete(CardReadJob job, CardReadStatus status, CardReadResult? result, string? code, string? message)
    {
        if (job.Finish(status, _time.GetUtcNow(), result, code, message))
        {
            _logger.LogInformation(
                "Card read {JobId} finished: {Status} code={Code} reader={Reader} interface={Interface} aid={Aid}",
                job.Id,
                status,
                code ?? "",
                result?.Reader.Name ?? job.Options.ReaderId ?? "(any)",
                (result?.Interface ?? job.Options.Interface).ToString(),
                result?.Selected.Aid ?? job.Options.NormalizedAid ?? "(none)");
            JobFinished?.Invoke(job);
        }
    }
}
