using Highbridge.Configuration;
using Highbridge.Consent;
using Highbridge.Hosting;
using Highbridge.Imaging;
using Highbridge.Jobs;
using Highbridge.Protocol;
using Highbridge.Scanning;
using Highbridge.Security;

namespace Highbridge.Core.Tests;

public class RuntimeAndRegistryTests
{
    private const string Origin = TestRuntime.Origin;

    [Fact]
    public void Launch_url_is_accepted()
    {
        using var runtime = TestRuntime.Create();
        Assert.Equal(ProtocolOutcome.Launched, runtime.HandleProtocolUrl("highbridge://launch").Outcome);
    }

    [Fact]
    public void Protocol_scan_for_unpaired_origin_is_rejected_and_never_prompts()
    {
        var consent = new FixedConsentService(allow: true);
        using var runtime = TestRuntime.Create(consent);

        var result = runtime.HandleProtocolUrl(HighbridgeUri.BuildScan(Origin, ScanJobManager.NewId(), new ScanOptions()));

        Assert.Equal(ProtocolOutcome.OriginNotPaired, result.Outcome);
        Assert.Empty(runtime.Jobs.List());
        Assert.Equal(0, consent.PairingRequests);
    }

    [Fact]
    public async Task Protocol_scan_for_paired_origin_runs_under_the_requested_id()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);
        string id = ScanJobManager.NewId();

        var result = runtime.HandleProtocolUrl(HighbridgeUri.BuildScan("https://APP.example.com:443", id, new ScanOptions { Source = PaperSource.Flatbed }));

        Assert.Equal(ProtocolOutcome.ScanStarted, result.Outcome);
        var job = runtime.Jobs.Claim(id, Origin);
        Assert.NotNull(job);
        Assert.Equal(ScanTrigger.ProtocolUrl, job.Trigger);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(job.Pages);
    }

    [Fact]
    public void Garbage_protocol_url_is_reported_invalid()
    {
        using var runtime = TestRuntime.Create();
        Assert.Equal(ProtocolOutcome.Invalid, runtime.HandleProtocolUrl("highbridge://rm-rf").Outcome);
    }

    [Fact]
    public void Policy_origins_count_as_paired_and_pairings_persist_to_disk()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var policy = new HighbridgePolicy { AllowedOrigins = ["https://Corp.Example.com/"] };
            var registry = new OriginRegistry(new SettingsStore(path), policy);

            Assert.True(registry.IsPaired("https://corp.example.com"));
            Assert.False(registry.IsPaired(Origin));

            registry.Pair(Origin);

            var reloaded = new OriginRegistry(new SettingsStore(path), new HighbridgePolicy());
            Assert.True(reloaded.IsPaired(Origin));

            reloaded.Unpair(Origin);
            Assert.False(new OriginRegistry(new SettingsStore(path), new HighbridgePolicy()).IsPaired(Origin));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_settings_file_falls_back_to_defaults()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ not json");
            var store = new SettingsStore(path);
            Assert.Empty(store.Snapshot().PairedOrigins);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Files_from_v0_1_with_port_and_scan_prompt_settings_still_load_and_the_port_stays_fixed()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string settingsPath = Path.Combine(dir, "settings.json");
            string policyPath = Path.Combine(dir, "policy.json");
            File.WriteAllText(settingsPath, """{"port": 1234, "pairedOrigins": [{"origin": "https://app.example.com", "pairedAt": "2026-10-01T00:00:00Z", "autoApproveScans": true}]}""");
            File.WriteAllText(policyPath, """{"port": 4321, "alwaysConfirmScans": true, "allowedOrigins": ["https://corp.example.com"]}""");

            using var runtime = new HighbridgeRuntime(
                TestRuntime.FastMock(),
                new FixedConsentService(allow: false),
                new SettingsStore(settingsPath),
                HighbridgePolicy.Load(policyPath));

            Assert.Equal(HighbridgeRuntime.FixedPort, runtime.Port);
            Assert.True(runtime.Origins.IsPaired(Origin));
            Assert.True(runtime.Origins.IsPaired("https://corp.example.com"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Denial_cooldown_expires()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new OriginRegistry(new SettingsStore(null), new HighbridgePolicy(), time);

        registry.RecordDenial(Origin);
        Assert.True(registry.IsInDenialCooldown(Origin));

        time.Advance(OriginRegistry.DenialCooldown + TimeSpan.FromSeconds(1));
        Assert.False(registry.IsInDenialCooldown(Origin));
    }

    [Theory]
    [InlineData(ColorMode.Color, 2)]
    [InlineData(ColorMode.Gray, 0)]
    [InlineData(ColorMode.BlackWhite, 0)]
    public async Task Mock_pages_are_valid_a4_pngs(ColorMode mode, byte pngColorType)
    {
        var backend = new MockScannerBackend(new MockScannerOptions { PageDelay = TimeSpan.Zero });
        var pages = await backend.ScanAsync(new ScanOptions { Dpi = 300, ColorMode = mode, ScannerId = MockScannerBackend.FlatbedId }, null, CancellationToken.None);

        var page = Assert.Single(pages);
        Assert.True(PngEncoder.TryReadSize(page.Data, out int w, out int h));
        Assert.Equal((827, 1169), (w, h)); // rendered at the 100 dpi mock cap
        Assert.Equal((w, h), (page.Width, page.Height));
        Assert.Equal(pngColorType, page.Data[25]);
    }

    [Fact]
    public async Task Single_instance_channel_forwards_arguments()
    {
        string pipe = $"highbridge-test-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var channel = new SingleInstanceChannel(pipe);
        channel.Start(args =>
        {
            received.TrySetResult(args);
            return Task.CompletedTask;
        });

        Assert.True(await SingleInstanceChannel.TrySendAsync(["highbridge://launch", "x y"], pipe, TimeSpan.FromSeconds(5)));
        Assert.Equal(["highbridge://launch", "x y"], await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Single_instance_channel_can_be_disposed_twice()
    {
        var channel = new SingleInstanceChannel($"highbridge-test-{Guid.NewGuid():N}");
        channel.Start(_ => Task.CompletedTask);
        await channel.DisposeAsync();
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Sending_without_a_running_instance_fails_fast()
    {
        Assert.False(await SingleInstanceChannel.TrySendAsync(["highbridge://launch"], $"highbridge-none-{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(200)));
    }
}
