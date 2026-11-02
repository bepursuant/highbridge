using Highbridge.Configuration;
using Highbridge.Consent;
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
        Assert.Equal(4, job.Pages.Count);
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
        using var runtime = TestRuntime.Create(backend: backend);
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
    public async Task Max_pages_limits_the_feeder()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder, Duplex = true, MaxPages = 3 }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, job.Pages.Count);
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
        using var runtime = TestRuntime.Create(backend: backend);
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
    public async Task Scans_run_one_at_a_time()
    {
        var backend = new ConcurrencyProbe();
        using var runtime = TestRuntime.Create(backend: backend);
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
            var page = new ScannedPage(0, "image/png", 1, 1, [1]);
            onPage?.Invoke(page);
            return [page];
        }
    }
}
