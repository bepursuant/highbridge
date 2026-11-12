using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Highbridge.Barcodes;
using Highbridge.Provenance;
using Highbridge.Scanning;
using Highbridge.Security;

namespace Highbridge.Jobs;

public sealed class JobRejectedException : Exception
{
    public JobRejectedException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Owns scan jobs: running one scan at a time, keeping results in memory until they expire, and
/// releasing them only to the origin that owns the job. Scans from paired origins never prompt.
/// </summary>
public sealed class ScanJobManager : IDisposable
{
    public static readonly TimeSpan DefaultRetention = DeviceJobRules.DefaultRetention;

    /// <summary>A highbridge:// scan that its origin does not claim within this time never starts.</summary>
    public static readonly TimeSpan DefaultClaimTimeout = DeviceJobRules.DefaultClaimTimeout;

    /// <summary>
    /// Backstop for a backend that never returns. The TWAIN backend has its own, shorter
    /// no-activity timeout; this one only guarantees the page always gets an answer.
    /// </summary>
    public static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// After the scanner finishes, how long a job waits for barcode detection of its last pages
    /// (earlier pages are analysed while later ones are scanned). Pages not done by then are "skipped".
    /// </summary>
    public static readonly TimeSpan BarcodeWaitAfterScan = TimeSpan.FromSeconds(30);

    private readonly IScannerBackend _backend;
    private readonly OriginRegistry _origins;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;
    private readonly TimeSpan _claimTimeout;
    private readonly TimeSpan _scanTimeout;
    private readonly ConcurrentDictionary<string, ScanJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly ITimer _purgeTimer;
    private readonly ILogger _logger;
    private readonly BarcodeWorker _barcodeWorker;
    private readonly ProvenanceStamping _provenance;

    public ScanJobManager(
        IScannerBackend backend,
        OriginRegistry origins,
        TimeProvider? time = null,
        TimeSpan? retention = null,
        TimeSpan? claimTimeout = null,
        ILogger<ScanJobManager>? logger = null,
        TimeSpan? scanTimeout = null,
        ProvenanceStamping? provenance = null,
        BarcodeWorker? barcodeWorker = null)
    {
        _barcodeWorker = barcodeWorker ?? new BarcodeWorker();
        _logger = logger ?? NullLogger<ScanJobManager>.Instance;
        _backend = backend;
        _origins = origins;
        _time = time ?? TimeProvider.System;
        _retention = retention ?? DefaultRetention;
        _claimTimeout = claimTimeout ?? DefaultClaimTimeout;
        _scanTimeout = scanTimeout ?? DefaultScanTimeout;
        _provenance = provenance ?? new ProvenanceStamping();
        _purgeTimer = _time.CreateTimer(_ => PurgeExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Raised (on a worker thread) whenever a job reaches a terminal state.</summary>
    public event Action<ScanJob>? JobFinished;

    public static bool IsValidRequestId(string? id) => DeviceJobRules.IsValidRequestId(id);

    public static string NewId() => DeviceJobRules.NewId();

    /// <summary>Queues a scan. The caller must already have verified that <paramref name="origin"/> is paired.</summary>
    public ScanJob Start(string origin, ScanOptions options, ScanTrigger trigger, string? requestedId = null)
    {
        var problems = options.Validate();
        if (problems.Count > 0)
        {
            throw new JobRejectedException("invalid_options", string.Join(" ", problems));
        }

        if (!_origins.IsPaired(origin))
        {
            throw new JobRejectedException("origin_not_paired", "This site is not paired with highbridge.");
        }

        PurgeExpired();
        if (_jobs.Count >= DeviceJobRules.MaxJobs)
        {
            throw new JobRejectedException("too_many_jobs", "Too many scans are pending. Delete finished scans and retry.");
        }

        string id = requestedId ?? NewId();
        if (!IsValidRequestId(id))
        {
            throw new JobRejectedException("invalid_request_id", "request id must be 16-64 characters of [A-Za-z0-9_-].");
        }

        var stamp = _provenance.Resolve(options.Stamp, _origins.ApprovalCoversIdentity(origin), out bool stampRequired);
        var job = new ScanJob(id, origin, options with { Stamp = stamp }, trigger, _time.GetUtcNow())
        {
            StampRequiredByPolicy = stampRequired,
            ScanId = stamp is null ? null : ProvenanceStamping.NewScanId(),
        };
        if (!_jobs.TryAdd(id, job))
        {
            throw new JobRejectedException("duplicate_request_id", "A scan with this request id already exists.");
        }

        _logger.LogInformation(
            "Scan {JobId} started for {Origin} ({Trigger}): scanner={Scanner} dpi={Dpi} color={Color} source={Source} duplex={Duplex} oneSheet={OneSheet} driverUi={DriverUi} maxPages={MaxPages} format={Format} barcodes={Barcodes}",
            job.Id,
            origin,
            trigger,
            options.ScannerId ?? "(default)",
            options.Dpi,
            options.ColorMode,
            options.Source,
            options.Duplex,
            options.OneSheet,
            options.ShowDriverUi,
            options.MaxPages,
            options.Format,
            options.Barcodes);
        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    /// <summary>Returns the job only if <paramref name="origin"/> owns it.</summary>
    public ScanJob? Get(string id, string origin) =>
        _jobs.TryGetValue(id, out var job) && job.Origin == origin ? job : null;

    /// <summary>
    /// Like <see cref="Get"/>, and lets a highbridge:// job start: the request now comes with a
    /// browser-verified Origin, which proves the link was opened by that site's page.
    /// </summary>
    public ScanJob? Claim(string id, string origin)
    {
        var job = Get(id, origin);
        job?.Claim();
        return job;
    }

    public IReadOnlyList<ScanJob> List() => _jobs.Values.OrderBy(j => j.CreatedAt).ToArray();

    public bool Delete(string id, string origin)
    {
        if (Get(id, origin) is not { } job)
        {
            return false;
        }

        job.Cancellation.Cancel();
        Complete(job, ScanJobStatus.Canceled, "canceled", "The scan was canceled.");
        _jobs.TryRemove(id, out _);
        return true;
    }

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

        _barcodeWorker.Dispose();
    }

    /// <summary>Runs on the backend's thread (the TWAIN thread for real scanners), so it only queues work.</summary>
    private void OnPage(ScanJob job, ScannedPage page, List<ReceivedPage> held)
    {
        // Barcodes are read from a copy of the page exactly as the scanner delivered it.
        // That copy is taken here, before a stamp replaces the image that gets published.
        job.Barcodes?.Enqueue(page, _barcodeWorker, job.Cancellation.Token);
        // A PDF is one file for this job, so the page images stay unpublished until it is built.
        // A stamp waits for the same reason: the band says "page N of M".
        if (job.Stamp is null && job.Options.Format != OutputFormat.Pdf)
        {
            job.AddPage(page);
            return;
        }

        // Never publish a page before it is stamped. The band says "page N of M".
        lock (held)
        {
            held.Add(new ReceivedPage(page, _time.GetUtcNow()));
        }

        job.HoldPage();
    }

    private async Task RunAsync(ScanJob job)
    {
        var ct = job.Cancellation.Token;
        try
        {
            if (!job.IsClaimed)
            {
                await job.Claimed.WaitAsync(_claimTimeout, _time, ct).ConfigureAwait(false);
            }

            await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("highbridge:// scan {JobId} for {Origin} was never collected; not scanning", job.Id, job.Origin);
            Complete(job, ScanJobStatus.Canceled, "not_claimed", "No page from this site collected the scan, so it was not started.");
            return;
        }
        catch (OperationCanceledException)
        {
            Complete(job, ScanJobStatus.Canceled, "canceled", "The scan was canceled.");
            return;
        }

        try
        {
            job.SetStatus(ScanJobStatus.Scanning);
            if (job.Options.OneSheet)
            {
                // The empty-feeder wait is not part of the scan timeout: a session stays armed
                // until someone loads a sheet or cancels the job. The one-scan lock is unchanged.
                job.UsedFeeder = job.Options.Source == PaperSource.Flatbed
                    ? false
                    : await _backend.WaitForFeederAsync(job.Options, ct).ConfigureAwait(false);
            }

            var held = new List<ReceivedPage>();
            var pages = await _backend.ScanAsync(job.Options, page => OnPage(job, page, held), ct).WaitAsync(_scanTimeout, _time, ct).ConfigureAwait(false);
            if (pages.Count == 0)
            {
                Complete(job, ScanJobStatus.Failed, "no_pages", "The scanner returned no pages. Is there paper in the feeder?");
                return;
            }

            IReadOnlyList<ScannedPage> images = pages;
            if (job.Stamp is { } stamp)
            {
                images = await StampAsync(job, stamp, pages, held, ct).ConfigureAwait(false);
            }

            if (job.Options.Format == OutputFormat.Pdf)
            {
                // This job only. The next scan, including the next sheet of a session, builds its own file.
                byte[] pdf = ScanPdf.Create(images, job.Options.Dpi);
                var first = images[0];
                job.PublishDocument(new ScannedPage(0, OutputFormats.PdfMediaType, first.Width, first.Height, pdf));
            }
            else if (job.Stamp is not null)
            {
                job.ReleaseHeldPages(images);
            }

            if (job.Barcodes is { } barcodes)
            {
                await barcodes.WaitAsync(BarcodeWaitAfterScan, _time).WaitAsync(ct).ConfigureAwait(false);
            }

            Complete(job, ScanJobStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            Complete(job, ScanJobStatus.Canceled, "canceled", "The scan was canceled.");
        }
        catch (TimeoutException)
        {
            _logger.LogError("Scan job {JobId} did not finish within {Timeout}; giving up on it", job.Id, _scanTimeout);
            job.Cancellation.Cancel();
            Complete(job, ScanJobStatus.Failed, "scan_timeout", "The scanner did not finish the scan. Check the scanner, then try again.");
        }
        catch (ScannerException ex)
        {
            _logger.LogWarning(ex, "Scan job {JobId} failed: {Code}", job.Id, ex.Code);
            Complete(job, ScanJobStatus.Failed, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan job {JobId} failed", job.Id);
            Complete(job, ScanJobStatus.Failed, "scanner_error", "The scanner failed unexpectedly. See the highbridge log for details.");
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task<IReadOnlyList<ScannedPage>> StampAsync(ScanJob job, StampOptions stamp, IReadOnlyList<ScannedPage> pages, List<ReceivedPage> held, CancellationToken ct)
    {
        IReadOnlyList<ReceivedPage> received;
        lock (held)
        {
            var now = _time.GetUtcNow();
            received = held.Count == pages.Count ? held.ToArray() : pages.Select(p => new ReceivedPage(p, now)).ToArray();
        }

        string scanner = received.All(p => !string.IsNullOrWhiteSpace(p.Page.ScannerName))
            ? string.Empty
            : await ScannerNameAsync(job.Options.ScannerId, ct).ConfigureAwait(false);
        return await Task.Run(
            () => _provenance.StampAll(received, stamp, job.Origin, job.ScanId!, scanner, job.Options.Dpi, _time.LocalTimeZone),
            ct).ConfigureAwait(false);
    }

    private async Task<string> ScannerNameAsync(string? scannerId, CancellationToken ct)
    {
        try
        {
            var scanners = await _backend.ListScannersAsync(ct).ConfigureAwait(false);
            var match = scanners.FirstOrDefault(s => scannerId is null ? s.IsDefault : s.Id == scannerId);
            if (match is not null)
            {
                return match.Name;
            }
        }
        catch (ScannerException ex)
        {
            _logger.LogInformation("Could not look up the scanner name for the stamp: {Code}", ex.Code);
        }

        return scannerId ?? "unknown scanner";
    }

    private void Complete(ScanJob job, ScanJobStatus status, string? code = null, string? message = null)
    {
        if (job.Finish(status, _time.GetUtcNow(), code, message))
        {
            _logger.LogInformation(
                "Scan {JobId} finished: {Status} code={Code} pages={Pages}",
                job.Id,
                status,
                code ?? "",
                job.Pages.Count);
            JobFinished?.Invoke(job);
        }
    }
}
