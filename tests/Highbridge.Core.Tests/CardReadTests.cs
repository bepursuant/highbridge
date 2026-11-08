using Highbridge.Cards;
using Highbridge.Configuration;
using Highbridge.Jobs;
using Highbridge.Protocol;
using Highbridge.Scanning;
using Highbridge.Security;

namespace Highbridge.Core.Tests;

public class CardReadTests
{
    private const string Origin = TestRuntime.Origin;

    [Fact]
    public void Shared_job_rules_stay_on_the_scan_manager()
    {
        Assert.Equal(DeviceJobRules.DefaultRetention, ScanJobManager.DefaultRetention);
        Assert.Equal(DeviceJobRules.DefaultClaimTimeout, ScanJobManager.DefaultClaimTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), DeviceJobRules.DefaultRetention);
        Assert.Equal(TimeSpan.FromSeconds(30), DeviceJobRules.DefaultClaimTimeout);
        Assert.True(DeviceJobRules.IsValidRequestId(DeviceJobRules.NewId()));
        Assert.False(DeviceJobRules.IsValidRequestId("short"));
    }

    [Fact]
    public async Task Mock_reader_returns_two_apps_and_the_failure_readers_fail()
    {
        var backend = new MockCardReaderBackend();
        var readers = await backend.ListReadersAsync(CancellationToken.None);
        Assert.Equal(3, readers.Count);
        Assert.DoesNotContain(readers, reader => reader.GetType().GetProperties().Any(p => p.Name.Contains("Present", StringComparison.OrdinalIgnoreCase)));

        var read = await backend.ReadAsync(new CardReadOptions(), CancellationToken.None);
        Assert.Equal(MockCardReaderBackend.SuccessId, read.Reader.Id);
        Assert.Equal(CardInterface.Contact, read.Interface);
        Assert.Equal(2, read.Applications.Count);
        Assert.Equal(MockCardReaderBackend.VisaPan, read.Selected.Pan);
        Assert.Equal("1234", read.Selected.PanLast4);
        Assert.DoesNotContain(MockCardReaderBackend.VisaPan, read.ToString(), StringComparison.Ordinal);
        Assert.Equal("2028-12", read.Selected.Expiry);
        Assert.Equal("DOE/JOHN", read.Selected.CardholderName);
        Assert.Null(read.Applications[1].PreferredName);

        var debit = await backend.ReadAsync(new CardReadOptions { Aid = "a0000000041010", Interface = CardInterface.Contactless }, CancellationToken.None);
        Assert.Equal(MockCardReaderBackend.MastercardPan, debit.Selected.Pan);
        Assert.Equal("5678", debit.Selected.PanLast4);
        Assert.Equal(CardInterface.Contactless, debit.Interface);

        var noCard = await Assert.ThrowsAsync<CardReaderException>(() => backend.ReadAsync(new CardReadOptions { ReaderId = MockCardReaderBackend.NoCardId }, CancellationToken.None));
        Assert.Equal("no_card", noCard.Code);
        var empty = await Assert.ThrowsAsync<CardReaderException>(() => backend.ReadAsync(new CardReadOptions { ReaderId = MockCardReaderBackend.EmptyId }, CancellationToken.None));
        Assert.Equal("no_payment_apps", empty.Code);
    }

    [Fact]
    public async Task The_next_read_waits_until_the_card_has_left_the_slot()
    {
        var backend = new MockCardReaderBackend { CardPresent = true };
        using var runtime = TestRuntime.Create(cardReader: backend);
        runtime.Origins.Pair(Origin);

        var first = runtime.CardReads.Start(Origin, new CardReadOptions { Interface = CardInterface.Auto }, CardReadTrigger.Api);
        await first.Finished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CardReadStatus.Completed, first.Status);
        Assert.Equal(MockCardReaderBackend.VisaPan, first.Result!.Selected.Pan);
        Assert.DoesNotContain(MockCardReaderBackend.VisaPan, first.ToString(), StringComparison.Ordinal);

        var next = runtime.CardReads.Start(Origin, new CardReadOptions { Interface = CardInterface.Auto, WaitForRemoval = true }, CardReadTrigger.Api);
        await Task.Delay(80);
        Assert.Equal(CardReadStatus.Reading, next.Status);
        Assert.Null(next.Result);

        backend.CardPresent = false;
        await next.Finished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CardReadStatus.Completed, next.Status);
        Assert.Equal(MockCardReaderBackend.VisaPan, next.Result!.Selected.Pan);
        Assert.DoesNotContain(MockCardReaderBackend.VisaPan, next.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_a_finished_read_wipes_the_card_number()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);

        var job = runtime.CardReads.Start(Origin, new CardReadOptions(), CardReadTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(MockCardReaderBackend.VisaPan, job.Result!.Selected.Pan);
        Assert.True(runtime.CardReads.Delete(job.Id, Origin));
        Assert.Equal("", job.Result.Selected.Pan);
        Assert.Equal("1234", job.Result.Selected.PanLast4);
        Assert.DoesNotContain(MockCardReaderBackend.VisaPan, job.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unclaimed_protocol_read_never_touches_the_reader()
    {
        var backend = new CountingCards();
        var origins = new OriginRegistry(new SettingsStore(null), new HighbridgePolicy());
        origins.Pair(Origin);
        using var jobs = new CardReadManager(backend, origins, new HighbridgePolicy(), claimTimeout: TimeSpan.FromMilliseconds(50));

        var job = jobs.Start(Origin, new CardReadOptions(), CardReadTrigger.ProtocolUrl, DeviceJobRules.NewId());
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CardReadStatus.Canceled, job.Status);
        Assert.Equal("not_claimed", job.ErrorCode);
        Assert.Equal(0, backend.Reads);
    }

    [Fact]
    public async Task A_card_read_does_not_wait_on_a_scan()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = TestRuntime.Create(cardReader: new GatedCards(started, release));
        runtime.Origins.Pair(Origin);

        var read = runtime.CardReads.Start(Origin, new CardReadOptions(), CardReadTrigger.Api);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var scan = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Flatbed, MaxPages = 1 }, ScanTrigger.Api);
        await scan.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Completed, scan.Status);
        Assert.Equal(CardReadStatus.Reading, read.Status);
        release.TrySetResult();
        await read.Finished.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CardReadStatus.Completed, read.Status);
        Assert.Equal("1234", read.Result!.Selected.PanLast4);
    }

    [Fact]
    public void Disabled_policy_blocks_card_reads_and_leaves_scans_alone()
    {
        var policy = new HighbridgePolicy { CardReader = new CardReaderPolicy { Mode = CardReaderMode.Disabled } };
        using var runtime = TestRuntime.Create(policy: policy);
        runtime.Origins.Pair(Origin);

        var rejected = Assert.Throws<JobRejectedException>(() => runtime.CardReads.Start(Origin, new CardReadOptions(), CardReadTrigger.Api));
        Assert.Equal("card_reader_disabled", rejected.Code);
        var scan = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Flatbed, MaxPages = 1 }, ScanTrigger.Api);
        Assert.Equal(ScanJobStatus.Queued, scan.Status);
    }

    [Fact]
    public void An_older_approval_must_be_renewed_before_a_card_read()
    {
        using var runtime = TestRuntime.Create();
        runtime.Settings.Update(s => s.PairedOrigins.Add(new PairedOrigin { Origin = Origin, ApprovalVersion = 3 }));

        var rejected = Assert.Throws<JobRejectedException>(() => runtime.CardReads.Start(Origin, new CardReadOptions(), CardReadTrigger.Api));
        Assert.Equal("approval_required", rejected.Code);
        Assert.Contains("full card number", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_card_url_round_trips_and_rejects_an_apdu()
    {
        var options = new CardReadOptions { ReaderId = "ACS 122", Interface = CardInterface.Contactless, Aid = "A0000000031010", WaitForCardMs = 1000 };
        string url = HighbridgeUri.BuildReadCard(Origin, DeviceJobRules.NewId(), options);

        Assert.StartsWith("highbridge://read-card?", url, StringComparison.Ordinal);
        Assert.True(HighbridgeUri.TryParse(url, out var command, out string? error), error);
        Assert.Equal(ProtocolAction.ReadCard, command!.Action);
        Assert.Equal(options.ReaderId, command.CardRead!.ReaderId);
        Assert.Equal(CardInterface.Contactless, command.CardRead.Interface);
        Assert.Equal(options.Aid, command.CardRead.NormalizedAid);
        Assert.Equal(1000, command.CardRead.WaitForCardMs);

        Assert.False(HighbridgeUri.TryParse(url + "&apdu=00A40400", out _, out error));
        Assert.Contains("apdu", error);
    }

    private sealed class CountingCards : ICardReaderBackend
    {
        private readonly MockCardReaderBackend _inner = new();
        private int _reads;

        public int Reads => _reads;

        public string Name => _inner.Name;

        public Task<IReadOnlyList<CardReaderInfo>> ListReadersAsync(CancellationToken cancellationToken) =>
            _inner.ListReadersAsync(cancellationToken);

        public Task<CardReadResult> ReadAsync(CardReadOptions options, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return _inner.ReadAsync(options, cancellationToken);
        }

        public Task WaitForCardRemovalAsync(CardReadOptions options, CancellationToken cancellationToken) =>
            _inner.WaitForCardRemovalAsync(options, cancellationToken);
    }

    private sealed class GatedCards(TaskCompletionSource started, TaskCompletionSource release) : ICardReaderBackend
    {
        public string Name => "mock";

        public Task<IReadOnlyList<CardReaderInfo>> ListReadersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CardReaderInfo>>([]);

        public async Task<CardReadResult> ReadAsync(CardReadOptions options, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return await new MockCardReaderBackend().ReadAsync(options, cancellationToken);
        }
    }
}
