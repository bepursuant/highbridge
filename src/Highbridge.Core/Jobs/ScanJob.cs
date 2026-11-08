using System.Text.Json.Serialization;
using Highbridge.Barcodes;
using Highbridge.Provenance;
using Highbridge.Scanning;

namespace Highbridge.Jobs;

[JsonConverter(typeof(CamelCaseEnumConverter<ScanJobStatus>))]
public enum ScanJobStatus
{
    Queued,
    Scanning,
    Completed,
    Failed,
    Canceled,
}

[JsonConverter(typeof(CamelCaseEnumConverter<ScanTrigger>))]
public enum ScanTrigger
{
    /// <summary>POST /v1/scans with a verified Origin header.</summary>
    Api,

    /// <summary>
    /// highbridge://scan URL. The origin in the URL is unverified, so the scanner only starts once
    /// that origin claims the job over HTTP (see <see cref="ScanJobManager.Claim"/>).
    /// </summary>
    ProtocolUrl,
}

public sealed class ScanJob
{
    private readonly object _gate = new();
    private readonly List<ScannedPage> _pages = [];
    private int _heldPages;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _claimed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ScanJob(string id, string origin, ScanOptions options, ScanTrigger trigger, DateTimeOffset createdAt)
    {
        Id = id;
        Origin = origin;
        Options = options;
        Trigger = trigger;
        CreatedAt = createdAt;
        if (trigger == ScanTrigger.Api)
        {
            _claimed.TrySetResult();
        }

        if (options.Barcodes)
        {
            Barcodes = new JobBarcodes(options.BarcodeFormats ?? BarcodeFormatSet.Default);
        }
    }

    /// <summary>Barcodes found on the pages; null when the scan did not ask for them.</summary>
    public JobBarcodes? Barcodes { get; }

    public string Id { get; }

    public string Origin { get; }

    public ScanOptions Options { get; }

    public ScanTrigger Trigger { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public ScanJobStatus Status { get; private set; } = ScanJobStatus.Queued;

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool IsFinished => Status is ScanJobStatus.Completed or ScanJobStatus.Failed or ScanJobStatus.Canceled;

    /// <summary>Completes when the job reaches a terminal status.</summary>
    public Task Finished => _finished.Task;

    /// <summary>True once the owning origin has asked for the job over HTTP (always true for API jobs).</summary>
    public bool IsClaimed => _claimed.Task.IsCompleted;

    internal Task Claimed => _claimed.Task;

    internal CancellationTokenSource Cancellation { get; } = new();

    /// <summary>The effective stamp (after policy); same as <c>Options.Stamp</c>.</summary>
    public StampOptions? Stamp => Options.Stamp;

    public bool StampRequiredByPolicy { get; internal init; }

    /// <summary>Short id printed on stamped pages; made by highbridge, unlike <see cref="Id"/>.</summary>
    public string? ScanId { get; internal init; }

    /// <summary>
    /// Set for a one-sheet job. False when the scan did not use the feeder, so a session must not
    /// loop it. Null on an ordinary job.
    /// </summary>
    public bool? UsedFeeder { get; internal set; }

    /// <summary>
    /// Pages received from the scanner so far. Stamped jobs hold their pages until the scan ends
    /// (the band says "page N of M"), so this can be ahead of <see cref="Pages"/>.
    /// </summary>
    public int PagesScanned
    {
        get
        {
            lock (_gate)
            {
                return _pages.Count + _heldPages;
            }
        }
    }

    public IReadOnlyList<ScannedPage> Pages
    {
        get
        {
            lock (_gate)
            {
                return _pages.ToArray();
            }
        }
    }

    internal void Claim() => _claimed.TrySetResult();

    internal void AddPage(ScannedPage page)
    {
        lock (_gate)
        {
            _pages.Add(page);
        }
    }

    internal void HoldPage()
    {
        lock (_gate)
        {
            _heldPages++;
        }
    }

    internal void ReleaseHeldPages(IEnumerable<ScannedPage> pages)
    {
        lock (_gate)
        {
            _pages.AddRange(pages);
            _heldPages = 0;
        }
    }

    internal void SetStatus(ScanJobStatus status)
    {
        lock (_gate)
        {
            if (!IsFinished)
            {
                Status = status;
            }
        }
    }

    /// <returns>False when the job had already finished.</returns>
    internal bool Finish(ScanJobStatus status, DateTimeOffset at, string? errorCode = null, string? errorMessage = null)
    {
        lock (_gate)
        {
            if (IsFinished)
            {
                return false;
            }

            Status = status;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
            FinishedAt = at;
        }

        _finished.TrySetResult();
        return true;
    }
}
