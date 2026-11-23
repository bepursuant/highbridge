using System.Text.Json.Serialization;
using Highbridge.Barcodes;
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
    /// <summary>POST /scanner with a verified Origin header.</summary>
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
    private readonly List<ScannedDocument> _documents = [];
    private int _pagesScanned;
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

    /// <summary>Barcodes found on the documents; null when the scan did not ask for them.</summary>
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

    /// <summary>Short id embedded in every document of this scan; made by highbridge, unlike <see cref="Id"/>.</summary>
    public string ScanId { get; internal init; } = "";

    /// <summary>False when the scan did not use the feeder, so a session must not loop it.</summary>
    public bool? UsedFeeder { get; internal set; }

    /// <summary>Pages received from the scanner so far. Documents are published after they are stacked.</summary>
    public int PagesScanned
    {
        get
        {
            lock (_gate)
            {
                return _pagesScanned;
            }
        }
    }

    public IReadOnlyList<ScannedDocument> Documents
    {
        get
        {
            lock (_gate)
            {
                return _documents.ToArray();
            }
        }
    }

    internal void Claim() => _claimed.TrySetResult();

    internal void NotePage()
    {
        lock (_gate)
        {
            _pagesScanned++;
        }
    }

    internal void SetDocuments(IEnumerable<ScannedDocument> documents)
    {
        lock (_gate)
        {
            _documents.Clear();
            _documents.AddRange(documents);
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
