using System.Text.Json.Serialization;
using Highbridge.Cards;

namespace Highbridge.Jobs;

[JsonConverter(typeof(CamelCaseEnumConverter<CardReadStatus>))]
public enum CardReadStatus
{
    Queued,
    Reading,
    Completed,
    Failed,
    Canceled,
}

[JsonConverter(typeof(CamelCaseEnumConverter<CardReadTrigger>))]
public enum CardReadTrigger
{
    /// <summary>POST /v1/card-reads with a verified Origin header.</summary>
    Api,

    /// <summary>
    /// highbridge://read-card URL. The reader connects only after that origin claims the job over HTTP.
    /// </summary>
    ProtocolUrl,
}

public sealed class CardReadJob
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _claimed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal CardReadJob(string id, string origin, CardReadOptions options, CardReadTrigger trigger, DateTimeOffset createdAt)
    {
        Id = id;
        Origin = origin;
        Options = options;
        Trigger = trigger;
        CreatedAt = createdAt;
        if (trigger == CardReadTrigger.Api)
        {
            _claimed.TrySetResult();
        }
    }

    public string Id { get; }

    public string Origin { get; }

    public CardReadOptions Options { get; }

    public CardReadTrigger Trigger { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public CardReadStatus Status { get; private set; } = CardReadStatus.Queued;

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public CardReadResult? Result { get; private set; }

    public bool IsFinished => Status is CardReadStatus.Completed or CardReadStatus.Failed or CardReadStatus.Canceled;

    public Task Finished => _finished.Task;

    public bool IsClaimed => _claimed.Task.IsCompleted;

    internal Task Claimed => _claimed.Task;

    internal CancellationTokenSource Cancellation { get; } = new();

    internal void Claim() => _claimed.TrySetResult();

    internal void SetStatus(CardReadStatus status)
    {
        lock (_gate)
        {
            if (!IsFinished)
            {
                Status = status;
            }
        }
    }

    internal bool Finish(CardReadStatus status, DateTimeOffset at, CardReadResult? result = null, string? errorCode = null, string? errorMessage = null)
    {
        lock (_gate)
        {
            if (IsFinished)
            {
                return false;
            }

            Status = status;
            Result = result;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
            FinishedAt = at;
        }

        _finished.TrySetResult();
        return true;
    }
}
