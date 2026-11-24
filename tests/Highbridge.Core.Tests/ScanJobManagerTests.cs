using Highbridge.Configuration;
using Highbridge.Consent;
using Highbridge.Imaging;
using Highbridge.Jobs;
using Highbridge.Scanning;
using Highbridge.Security;

namespace Highbridge.Core.Tests;

public class ScanJobManagerTests
{
    private const string Origin = TestRuntime.Origin;

    [Fact]
    public async Task Completed_scan_returns_pages_to_the_owning_origin_only()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);
        runtime.Origins.Pair("https://other.example");

        var job = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder, Duplex = true }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Completed, job.Status);
        Assert.Equal(4, job.PagesScanned);
        var document = Assert.Single(job.Documents);
        Assert.Equal(4, document.PageCount);
        Assert.Same(job, runtime.Jobs.Get(job.Id, Origin));
        Assert.Null(runtime.Jobs.Get(job.Id, "https://other.example"));
        Assert.False(runtime.Jobs.Delete(job.Id, "https://other.example"));
    }

    [Fact]
    public void Unpaired_origin_cannot_start_a_scan()
    {
        using var runtime = TestRuntime.Create();
        var ex = Assert.Throws<JobRejectedException>(() => runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api));
        Assert.Equal("origin_not_paired", ex.Code);
    }

    [Fact]
    public async Task Scans_from_a_paired_origin_run_without_any_prompt()
    {
        var consent = new FixedConsentService(allow: false);
        using var runtime = TestRuntime.Create(consent);
        runtime.Origins.Pair(Origin);

        var jobs = Enumerable.Range(0, 3).Select(_ => runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api)).ToArray();
        await Task.WhenAll(jobs.Select(j => j.Finished)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(jobs, j => Assert.Equal(ScanJobStatus.Completed, j.Status));
        Assert.Equal(0, consent.PairingRequests);
    }

    [Fact]
    public async Task Protocol_scan_starts_the_scanner_only_after_its_origin_claims_it()
    {
        var backend = new CountingBackend(TestRuntime.FastMock());
        using var runtime = TestRuntime.Create(scannerBackend: backend);
        runtime.Origins.Pair(Origin);
        runtime.Origins.Pair("https://other.example");

        var job = runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.ProtocolUrl, ScanJobManager.NewId());
        await Task.Delay(100);
        Assert.Null(runtime.Jobs.Claim(job.Id, "https://other.example"));
        await Task.Delay(100);

        Assert.Equal(ScanJobStatus.Queued, job.Status);
        Assert.False(job.IsClaimed);
        Assert.Equal(0, backend.Scans);

        Assert.Same(job, runtime.Jobs.Claim(job.Id, Origin));
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Completed, job.Status);
        Assert.Equal(1, backend.Scans);
    }

    [Fact]
    public async Task Unclaimed_protocol_scan_is_dropped_without_touching_the_scanner()
    {
        var backend = new CountingBackend(TestRuntime.FastMock());
        var origins = new OriginRegistry(new SettingsStore(null), new HighbridgePolicy());
        origins.Pair(Origin);
        using var jobs = new ScanJobManager(backend, origins, claimTimeout: TimeSpan.FromMilliseconds(50));

        var job = jobs.Start(Origin, new ScanOptions(), ScanTrigger.ProtocolUrl, ScanJobManager.NewId());
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Canceled, job.Status);
        Assert.Equal("not_claimed", job.ErrorCode);
        Assert.Equal(0, backend.Scans);
    }

    [Fact]
    public async Task Unclaimed_protocol_scan_does_not_block_api_scans()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);

        var pending = runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.ProtocolUrl, ScanJobManager.NewId());
        var api = runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api);
        await api.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Completed, api.Status);
        Assert.Equal(ScanJobStatus.Queued, pending.Status);
    }

    [Fact]
    public async Task Scanner_errors_are_reported_with_their_code()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { ScannerId = MockScannerBackend.JammedId }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Failed, job.Status);
        Assert.Equal("paper_jam", job.ErrorCode);
    }

    [Fact]
    public async Task The_feeder_is_scanned_to_the_end_and_sheets_split_documents_like_pages()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);

        var bySheet = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder, Duplex = true, MaxSheets = 1 }, ScanTrigger.Api);
        await bySheet.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(4, bySheet.PagesScanned);
        Assert.Equal(2, bySheet.Documents.Count);
        Assert.All(bySheet.Documents, document => Assert.Equal(2, document.PageCount));

        var byPage = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder, Duplex = true, MaxPagesPerDocument = 2 }, ScanTrigger.Api);
        await byPage.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(4, byPage.PagesScanned);
        Assert.Equal(2, byPage.Documents.Count);
        Assert.All(byPage.Documents, document => Assert.Equal(2, document.PageCount));
        Assert.True(byPage.Documents[1].Height > byPage.Documents[0].Height / 2);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has spaces in it!!")]
    [InlineData("../../../../etc/passwd/x")]
    public void Rejects_bad_request_ids(string id)
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);
        var ex = Assert.Throws<JobRejectedException>(() => runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.ProtocolUrl, id));
        Assert.Equal("invalid_request_id", ex.Code);
    }

    [Fact]
    public void Rejects_duplicate_request_ids()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);
        string id = ScanJobManager.NewId();
        runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.ProtocolUrl, id);
        var ex = Assert.Throws<JobRejectedException>(() => runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.ProtocolUrl, id));
        Assert.Equal("duplicate_request_id", ex.Code);
    }

    [Fact]
    public void Rejects_invalid_options()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);
        var ex = Assert.Throws<JobRejectedException>(() => runtime.Jobs.Start(Origin, new ScanOptions { Dpi = 10 }, ScanTrigger.Api));
        Assert.Equal("invalid_options", ex.Code);
    }

    [Fact]
    public async Task Delete_cancels_a_scan_that_has_not_started()
    {
        var backend = new CountingBackend(TestRuntime.FastMock());
        using var runtime = TestRuntime.Create(scannerBackend: backend);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.ProtocolUrl, ScanJobManager.NewId());
        Assert.True(runtime.Jobs.Delete(job.Id, Origin));
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);

        Assert.Equal(ScanJobStatus.Canceled, job.Status);
        Assert.Null(runtime.Jobs.Get(job.Id, Origin));
        Assert.Equal(0, backend.Scans);
    }

    [Fact]
    public async Task Finished_jobs_expire_after_the_retention_period()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var runtime = TestRuntime.Create(time: time);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        time.Advance(ScanJobManager.DefaultRetention - TimeSpan.FromSeconds(1));
        runtime.Jobs.PurgeExpired();
        Assert.NotNull(runtime.Jobs.Get(job.Id, Origin));

        time.Advance(TimeSpan.FromSeconds(2));
        runtime.Jobs.PurgeExpired();
        Assert.Null(runtime.Jobs.Get(job.Id, Origin));
    }

    [Fact]
    public async Task A_feeder_scan_takes_every_loaded_sheet()
    {
        var mock = new MockScannerBackend(new MockScannerOptions { PageDelay = TimeSpan.Zero, FeederSheets = 2 });
        using var runtime = TestRuntime.Create(scannerBackend: mock);
        runtime.Origins.Pair(Origin);

        var whole = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder, Duplex = true }, ScanTrigger.Api);
        await whole.Finished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, Assert.Single(whole.Documents).PageCount);
        Assert.True(whole.UsedFeeder);

        var split = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder, Duplex = true, MaxSheets = 1 }, ScanTrigger.Api);
        await split.Finished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, split.PagesScanned);
        Assert.Equal(2, split.Documents.Count);
        Assert.All(split.Documents, document => Assert.Equal(2, document.PageCount));
    }

    [Fact]
    public async Task An_empty_feeder_waits_instead_of_failing_a_one_sheet_scan()
    {
        var mock = new MockScannerBackend(new MockScannerOptions { PageDelay = TimeSpan.Zero, FeederSheets = 0 });
        using var runtime = TestRuntime.Create(scannerBackend: mock);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder, Duplex = true, MaxSheets = 1 }, ScanTrigger.Api);
        await WaitUntilScanning(job);

        mock.LoadSheets(1);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ScanJobStatus.Completed, job.Status);
        Assert.Equal(2, Assert.Single(job.Documents).PageCount);
    }

    [Fact]
    public async Task An_empty_WIA_feeder_waits_and_then_scans()
    {
        var backend = new EmptyThenPageBackend();
        using var runtime = TestRuntime.Create(scannerBackend: backend);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ScanJobStatus.Completed, job.Status);
        Assert.True(job.UsedFeeder);
        Assert.Equal(2, backend.Scans);
        Assert.Equal(1, Assert.Single(job.Documents).PageCount);
    }

    [Fact]
    public async Task Cancelling_a_one_sheet_wait_ends_the_job()
    {
        var mock = new MockScannerBackend(new MockScannerOptions { PageDelay = TimeSpan.Zero, FeederSheets = 0 });
        using var runtime = TestRuntime.Create(scannerBackend: mock);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder, MaxSheets = 1 }, ScanTrigger.Api);
        await Task.Delay(40);
        Assert.True(runtime.Jobs.Delete(job.Id, Origin));
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ScanJobStatus.Canceled, job.Status);
        Assert.Equal("canceled", job.ErrorCode);
    }

    [Fact]
    public async Task A_flatbed_one_sheet_scan_is_one_page_and_does_not_wait()
    {
        var mock = new MockScannerBackend(new MockScannerOptions { PageDelay = TimeSpan.Zero, FeederSheets = 0 });
        using var runtime = TestRuntime.Create(scannerBackend: mock);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(
            Origin,
            new ScanOptions { Source = PaperSource.Flatbed, ScannerId = MockScannerBackend.FlatbedId, Duplex = true, MaxSheets = 1 },
            ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ScanJobStatus.Completed, job.Status);
        Assert.Equal(1, Assert.Single(job.Documents).PageCount);
        Assert.False(job.UsedFeeder);
    }

    [Fact]
    public async Task Scans_run_one_at_a_time()
    {
        var backend = new ConcurrencyProbe();
        using var runtime = TestRuntime.Create(scannerBackend: backend);
        runtime.Origins.Pair(Origin);

        var jobs = Enumerable.Range(0, 4).Select(_ => runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api)).ToArray();
        await Task.WhenAll(jobs.Select(j => j.Finished)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(jobs, j => Assert.Equal(ScanJobStatus.Completed, j.Status));
        Assert.Equal(1, backend.MaxConcurrent);
    }

    [Fact]
    public async Task A_scan_that_never_finishes_fails_with_scan_timeout()
    {
        var backend = new HangingBackend();
        var origins = new OriginRegistry(new SettingsStore(null), new HighbridgePolicy());
        origins.Pair(Origin);
        using var jobs = new ScanJobManager(backend, origins, scanTimeout: TimeSpan.FromMilliseconds(50));

        var job = jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Failed, job.Status);
        Assert.Equal("scan_timeout", job.ErrorCode);
        await backend.Canceled.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The scan task sets Scanning only after it takes the scan lock. A fixed sleep loses that race
    /// when the runner is busy, so wait until the job is actually waiting on the feeder.
    /// </summary>
    private static async Task WaitUntilScanning(ScanJob job)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (job.Status == ScanJobStatus.Queued && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(ScanJobStatus.Scanning, job.Status);
        Assert.False(job.IsFinished);
    }

    private sealed class EmptyThenPageBackend : IScannerBackend
    {
        public int Scans { get; private set; }

        public string Name => "wia-empty";

        public Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScannerInfo>>([]);

        public Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken)
        {
            Scans++;
            if (Scans == 1)
            {
                return Task.FromResult<IReadOnlyList<ScannedPage>>([]);
            }

            var page = new ScannedPage(0, "image/png", 1, 1, PngEncoder.Encode(1, 1, PngPixelFormat.Gray8, [255]));
            onPage?.Invoke(page);
            return Task.FromResult<IReadOnlyList<ScannedPage>>([page]);
        }
    }

    private sealed class HangingBackend : IScannerBackend
    {
        private readonly TaskCompletionSource _canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Canceled => _canceled.Task;

        public string Name => "hanging";

        public Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScannerInfo>>([]);

        public async Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken)
        {
            // Ignores cancellation, like a driver stuck inside a call.
            cancellationToken.Register(() => _canceled.TrySetResult());
            await new TaskCompletionSource().Task;
            return [];
        }
    }

    private sealed class ConcurrencyProbe : IScannerBackend
    {
        private int _current;

        public int MaxConcurrent { get; private set; }

        public string Name => "probe";

        public Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScannerInfo>>([new ScannerInfo("probe", "Probe", null, "probe", true)]);

        public async Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken)
        {
            int now = Interlocked.Increment(ref _current);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            await Task.Delay(20, cancellationToken);
            Interlocked.Decrement(ref _current);
            var page = new ScannedPage(0, "image/png", 1, 1, PngEncoder.Encode(1, 1, PngPixelFormat.Gray8, [255]));
            onPage?.Invoke(page);
            return [page];
        }
    }
}
