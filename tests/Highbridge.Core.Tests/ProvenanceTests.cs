using System.Text.Json;
using Highbridge.Configuration;
using Highbridge.Consent;
using Highbridge.Imaging;
using Highbridge.Jobs;
using Highbridge.Protocol;
using Highbridge.Provenance;
using Highbridge.Scanning;
using Highbridge.Security;

namespace Highbridge.Core.Tests;

public class ProvenanceStamperTests
{
    private static readonly PageProvenance Record = new()
    {
        ScanId = "HB-7K3M-9QX2",
        ScannedAt = new DateTimeOffset(2026, 10, 29, 16, 36, 5, TimeSpan.FromHours(-4)),
        ScannedAtUtc = new DateTimeOffset(2026, 10, 29, 20, 36, 5, TimeSpan.Zero),
        Workstation = "PC-0042",
        User = "CONTOSO\\José",
        Scanner = "Canon DR-C225",
        Origin = "https://app.example.com",
        Page = 1,
        PageCount = 3,
        OriginalSha256 = string.Empty,
        Position = StampPosition.Bottom,
        BandHeight = 0,
        Software = "highbridge test",
    };

    [Theory]
    [InlineData(StampPosition.Bottom)]
    [InlineData(StampPosition.Top)]
    public void Extends_the_canvas_and_copies_the_scanned_pixels_unchanged(StampPosition position)
    {
        byte[] original = ColorPage(400, 300, dpi: 150);

        var stamped = ProvenanceStamper.Stamp(original, Record with { Position = position }, fallbackDpi: 200);

        var before = PngDecoder.Decode(original);
        var after = PngDecoder.Decode(stamped.Png);
        int band = stamped.Provenance.BandHeight;
        Assert.True(band > 40);
        Assert.Equal((400, 300 + band), (after.Width, after.Height));
        Assert.Equal((stamped.Width, stamped.Height), (after.Width, after.Height));
        Assert.Equal(3, after.Channels);
        Assert.Equal(150, after.Dpi);

        int offset = position == StampPosition.Top ? band : 0;
        var a = new byte[400 * 3];
        var b = new byte[400 * 3];
        for (int y = 0; y < 300; y++)
        {
            before.ReadRow(y, a);
            after.ReadRow(y + offset, b);
            Assert.Equal(a, b);
        }

        // The band is pure black and white, with a full-width rule next to the page.
        int bandTop = position == StampPosition.Top ? 0 : 300;
        int ruleRow = position == StampPosition.Top ? band - 1 : 300;
        after.ReadRow(ruleRow, b);
        Assert.All(b, v => Assert.Equal(0, v));
        int black = 0;
        for (int y = bandTop; y < bandTop + band; y++)
        {
            after.ReadRow(y, b);
            Assert.All(b, v => Assert.True(v is 0 or 255));
            black += b.Count(v => v == 0);
        }

        Assert.True(black > 400 * 3 * 2, "the band has text");
    }

    [Fact]
    public void Embeds_the_record_and_hash_as_png_text_and_verifies()
    {
        var stamped = ProvenanceStamper.Stamp(ColorPage(400, 300, dpi: 200), Record, 200);
        var text = PngEncoder.ReadText(stamped.Png).ToDictionary(t => t.Keyword);

        Assert.Equal("highbridge test", text["Software"].Text);
        Assert.Equal("2026-10-29T20:36:05Z", text["Creation Time"].Text);
        Assert.Equal("Canon DR-C225", text["Source"].Text);
        Assert.True(text["Source"].Utf8);
        Assert.Contains("USER : CONTOSO\\José", text["Description"].Text);
        Assert.Contains("TIMESTAMP     : 2026-10-29 16:36:05 UTC-04:00", text["Description"].Text);
        Assert.Contains("HIGHBRIDGE ID : HB-7K3M-9QX2", text["Description"].Text);
        Assert.Contains("SCANNER       : Canon DR-C225", text["Description"].Text);
        Assert.Contains("SHA-256 HASH  : " + stamped.Provenance.OriginalSha256[..32] + "...", text["Description"].Text);
        Assert.DoesNotContain("20:36:05", text["Description"].Text);
        Assert.DoesNotContain("https://app.example.com", text["Description"].Text);
        Assert.Contains("PAGE 1 OF 3", text["Description"].Text);

        var embedded = JsonSerializer.Deserialize<PageProvenance>(text[ProvenanceStamper.MetadataKeyword].Text, HighbridgeJson.Options);
        Assert.Equal(stamped.Provenance, embedded);
        Assert.Matches("^[0-9a-f]{64}$", stamped.Provenance.OriginalSha256);
        Assert.Equal(PageProvenance.PixelHashMethod, stamped.Provenance.HashMethod);

        var check = ProvenanceVerifier.Verify(stamped.Png);
        Assert.True(check.Valid, check.Problem);
        Assert.Equal(stamped.Provenance.OriginalSha256, check.ComputedSha256);
    }

    [Fact]
    public void Hash_is_of_the_original_pixels_whatever_the_band()
    {
        byte[] original = ColorPage(300, 200, dpi: 200);

        var bottom = ProvenanceStamper.Stamp(original, Record, 200);
        var top = ProvenanceStamper.Stamp(original, Record with { Position = StampPosition.Top, Page = 2 }, 200);

        Assert.Equal(bottom.Provenance.OriginalSha256, top.Provenance.OriginalSha256);
        Assert.NotEqual(bottom.Provenance.OriginalSha256, ProvenanceStamper.Stamp(ColorPage(300, 200, 200, seed: 2), Record, 200).Provenance.OriginalSha256);
    }

    [Fact]
    public void Verifier_detects_an_edited_page()
    {
        var stamped = ProvenanceStamper.Stamp(ColorPage(200, 120, dpi: 100), Record, 100);
        var image = PngDecoder.Decode(stamped.Png);
        byte[] edited = PngEncoder.Encode(image.Width, image.Height, PngPixelFormat.Rgb24, (y, row) =>
        {
            image.ReadRow(y, row);
            if (y == 10)
            {
                row[30] ^= 0x40;
            }
        }, 100, PngEncoder.ReadText(stamped.Png));

        var check = ProvenanceVerifier.Verify(edited);

        Assert.False(check.Valid);
        Assert.Contains("does not match", check.Problem);
        Assert.False(ProvenanceVerifier.Verify(ColorPage(10, 10, 100)).Valid);
    }

    [Fact]
    public void Black_and_white_pages_stay_one_bit_and_gray_stays_gray()
    {
        var bw = new byte[64 * 40];
        for (int i = 0; i < bw.Length; i++)
        {
            bw[i] = (byte)(i % 7 == 0 ? 0 : 255);
        }

        var stampedBw = ProvenanceStamper.Stamp(PngEncoder.Encode(64, 40, PngPixelFormat.Bilevel, bw), Record, 200);
        Assert.Equal(1, stampedBw.Png[24]);
        Assert.True(ProvenanceVerifier.Verify(stampedBw.Png).Valid);

        var gray = bw.Select((v, i) => (byte)(v == 0 ? 0 : 100 + (i % 50))).ToArray();
        var stampedGray = ProvenanceStamper.Stamp(PngEncoder.Encode(64, 40, PngPixelFormat.Gray8, gray), Record, 200);
        Assert.Equal((8, 0), (stampedGray.Png[24], stampedGray.Png[25]));
        Assert.True(ProvenanceVerifier.Verify(stampedGray.Png).Valid);
    }

    [Fact]
    public void Text_size_tracks_resolution_and_narrow_pages_wrap()
    {
        var at100 = ProvenanceStamper.Stamp(ColorPage(850, 50, dpi: 100), Record, 100).Provenance.BandHeight;
        var at300 = ProvenanceStamper.Stamp(ColorPage(2550, 50, dpi: 300), Record, 300).Provenance.BandHeight;
        Assert.InRange(at300 / (double)at100, 2.0, 3.5);

        var narrow = ProvenanceStamper.Stamp(ColorPage(200, 50, dpi: 100), Record, 100);
        Assert.True(ProvenanceVerifier.Verify(narrow.Png).Valid);
        int wideRows = ProvenanceStamper.Arrange(ProvenanceStamper.BandLines(Record), 80).Count;
        int narrowRows = ProvenanceStamper.Arrange(ProvenanceStamper.BandLines(Record), 16).Count;
        Assert.True(narrowRows > wideRows);
    }

    [Fact]
    public void Band_text_is_upper_case_ascii_and_wraps_long_values()
    {
        Assert.Equal("CONTOSO\\JOSE ?", ProvenanceStamper.ForBand("contoso\\José 東"));
        Assert.Equal("A?B", ProvenanceStamper.ForBand("a\tb"));

        var lines = ProvenanceStamper.Wrap([["Origin " + new string('x', 70)], ["a", "b"]], 24);
        Assert.All(lines, l => Assert.True(l.Length <= 24));
        Assert.Equal("A   B", lines[^1]);
        Assert.Equal("ORIGIN " + new string('X', 70), string.Concat(lines.SkipLast(1)));
    }

    [Fact]
    public void Page_count_sits_on_the_right_and_the_band_shows_one_timestamp()
    {
        var lines = ProvenanceStamper.BandLines(Record);
        int colonAt = "HIGHBRIDGE ID".Length + 1;
        int valueAt = colonAt + 2;
        Assert.All(lines, line => Assert.Equal(colonAt, line.Segments[0].IndexOf(':')));
        Assert.Equal(valueAt, lines[0].Segments[0].IndexOf("HB-7K3M-9QX2", StringComparison.Ordinal));
        Assert.Equal(valueAt, lines[1].Segments[0].IndexOf("2026-10-29", StringComparison.Ordinal));
        Assert.Equal(valueAt, lines[2].Segments[0].IndexOf("PC-0042", StringComparison.Ordinal));
        Assert.Equal(valueAt, lines[3].Segments[0].IndexOf("Canon DR-C225", StringComparison.Ordinal));
        Assert.StartsWith("SHA-256", lines[4].Segments[0]);
        Assert.Contains("USER : CONTOSO\\José", lines[2].Segments[0]);

        var wide = ProvenanceStamper.Arrange(lines, 80);
        var shared = wide.Single(line => line.Right is not null);
        Assert.Equal("PAGE 1 OF 3", shared.Right);
        Assert.StartsWith("HIGHBRIDGE ID :", shared.Left);

        var narrow = ProvenanceStamper.Arrange(lines, 20);
        var ownLine = narrow.Single(line => line.Right is not null);
        Assert.Equal(string.Empty, ownLine.Left);
        Assert.Equal("PAGE 1 OF 3", ownLine.Right);

        var stamped = ProvenanceStamper.Stamp(ColorPage(900, 80, dpi: 100), Record, 100);
        var image = PngDecoder.Decode(stamped.Png);
        int scale = 2;
        int margin = 100 / 4;
        int rightEdge = image.Width - margin - scale - 1;
        var row = new byte[image.Width * image.Channels];
        int rowsAtEdge = 0;
        for (int y = image.Height - stamped.Provenance.BandHeight; y < image.Height; y++)
        {
            image.ReadRow(y, row);
            int rightmost = -1;
            for (int x = 0; x < image.Width; x++)
            {
                if (row[x * image.Channels] < 128)
                {
                    rightmost = x;
                }
            }

            if (rightmost == rightEdge)
            {
                rowsAtEdge++;
            }
        }

        Assert.True(rowsAtEdge >= scale, "page count reaches the right margin");
    }

    internal static byte[] ColorPage(int width, int height, int dpi, int seed = 1)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 3];
        random.NextBytes(pixels);
        return PngEncoder.Encode(width, height, PngPixelFormat.Rgb24, pixels, dpi);
    }
}

public class ProvenanceJobTests
{
    private const string Origin = TestRuntime.Origin;
    private static readonly ProvenanceIdentity Identity = new("WS-01", "CONTOSO\\jsmith");

    [Fact]
    public async Task Stamps_every_page_with_values_from_highbridge()
    {
        var time = new ZonedTimeProvider(new DateTimeOffset(2026, 10, 29, 20, 36, 5, 250, TimeSpan.Zero), TimeSpan.FromHours(-4));
        using var runtime = Runtime(time: time);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder, Stamp = new StampOptions() }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Completed, job.Status);
        Assert.Matches("^HB-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", job.ScanId);
        Assert.Equal(2, job.Pages.Count);
        for (int i = 0; i < 2; i++)
        {
            var p = job.Pages[i].Provenance!;
            Assert.Equal((i + 1, 2), (p.Page, p.PageCount));
            Assert.Equal(job.ScanId, p.ScanId);
            Assert.Equal(("WS-01", "CONTOSO\\jsmith", Origin), (p.Workstation, p.User, p.Origin));
            Assert.Equal("highbridge mock feeder (duplex)", p.Scanner);
            Assert.Equal(new DateTimeOffset(2026, 10, 29, 16, 36, 5, TimeSpan.FromHours(-4)), p.ScannedAt);
            Assert.Equal(TimeSpan.FromHours(-4), p.ScannedAt.Offset);
            Assert.Equal(TimeSpan.Zero, p.ScannedAtUtc.Offset);
            Assert.True(ProvenanceVerifier.Verify(job.Pages[i].Data).Valid);
            Assert.Equal(1169 + p.BandHeight, job.Pages[i].Height);
        }
    }

    [Fact]
    public async Task Unstamped_scans_carry_no_identity()
    {
        using var runtime = Runtime();
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(job.Stamp);
        Assert.Null(job.ScanId);
        Assert.All(job.Pages, p => Assert.Null(p.Provenance));
        Assert.All(job.Pages, p => Assert.Empty(PngEncoder.ReadText(p.Data)));
    }

    [Fact]
    public async Task Policy_can_require_the_stamp_and_force_its_position()
    {
        var policy = new HighbridgePolicy { ProvenanceStamp = new ProvenanceStampPolicy { Mode = StampMode.Required, Position = StampPosition.Top } };
        using var runtime = Runtime(policy);
        runtime.Settings.Update(s => s.PairedOrigins.Add(new PairedOrigin { Origin = Origin }));

        var job = runtime.Jobs.Start(Origin, new ScanOptions { Stamp = new StampOptions { Position = StampPosition.Bottom } }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));
        var unrequested = runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api);
        await unrequested.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(job.StampRequiredByPolicy);
        Assert.All(job.Pages.Concat(unrequested.Pages), p => Assert.Equal(StampPosition.Top, p.Provenance!.Position));
    }

    [Fact]
    public void Policy_can_disable_the_stamp()
    {
        using var runtime = Runtime(new HighbridgePolicy { ProvenanceStamp = new ProvenanceStampPolicy { Mode = StampMode.Disabled } });
        runtime.Origins.Pair(Origin);

        var ex = Assert.Throws<JobRejectedException>(() => runtime.Jobs.Start(Origin, new ScanOptions { Stamp = new StampOptions() }, ScanTrigger.Api));

        Assert.Equal("stamp_disabled", ex.Code);
        Assert.Null(runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api).Stamp);
    }

    [Fact]
    public void Sites_approved_before_the_identity_wording_must_approve_again()
    {
        using var runtime = Runtime(new HighbridgePolicy { AllowedOrigins = ["https://admin.example.com"] });
        runtime.Settings.Update(s => s.PairedOrigins.Add(new PairedOrigin { Origin = Origin, PairedAt = DateTimeOffset.UnixEpoch }));
        var stamped = new ScanOptions { Stamp = new StampOptions() };

        Assert.Equal("approval_required", Assert.Throws<JobRejectedException>(() => runtime.Jobs.Start(Origin, stamped, ScanTrigger.Api)).Code);
        Assert.NotNull(runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api));
        Assert.NotNull(runtime.Jobs.Start("https://admin.example.com", stamped, ScanTrigger.Api).Stamp);

        runtime.Origins.Pair(Origin);
        Assert.Equal(OriginRegistry.CurrentApprovalVersion, runtime.Origins.ListUserPairings().Single().ApprovalVersion);
        Assert.Equal(DateTimeOffset.UnixEpoch, runtime.Origins.ListUserPairings().Single().PairedAt);
        Assert.NotNull(runtime.Jobs.Start(Origin, stamped, ScanTrigger.Api).Stamp);
    }

    [Fact]
    public async Task Holds_pages_until_the_whole_scan_is_stamped()
    {
        var backend = new GatedBackend();
        using var runtime = Runtime(backend: backend);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { Stamp = new StampOptions() }, ScanTrigger.Api);
        await backend.FirstPageDelivered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, job.PagesScanned);
        Assert.Empty(job.Pages);

        backend.Release.SetResult();
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, job.PagesScanned);
        Assert.Equal([1, 2], job.Pages.Select(p => p.Provenance!.Page));
        Assert.All(job.Pages, p => Assert.Equal("gated scanner", p.Provenance!.Scanner));
    }

    [Fact]
    public async Task A_page_that_cannot_be_stamped_fails_the_scan_without_releasing_pages()
    {
        using var runtime = Runtime(backend: new GatedBackend(pageData: [1, 2, 3], released: true));
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { Stamp = new StampOptions() }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Failed, job.Status);
        Assert.Equal("stamp_failed", job.ErrorCode);
        Assert.Empty(job.Pages);
    }

    [Fact]
    public void Protocol_urls_carry_only_the_stamp_position()
    {
        string url = HighbridgeUri.BuildScan(Origin, ScanJobManager.NewId(), new ScanOptions { Stamp = new StampOptions { Position = StampPosition.Top } });
        Assert.Contains("&stamp=top", url);
        Assert.True(HighbridgeUri.TryParse(url, out var command, out _));
        Assert.Equal(StampPosition.Top, command!.Options.Stamp!.Position);

        Assert.DoesNotContain("stamp", HighbridgeUri.BuildScan(Origin, ScanJobManager.NewId(), new ScanOptions()));
        Assert.True(HighbridgeUri.TryParse("highbridge://scan?v=1&origin=https%3A%2F%2Fa.example&request=0123456789abcdef&stamp=BOTTOM", out command, out _));
        Assert.Equal(StampPosition.Bottom, command!.Options.Stamp!.Position);
        Assert.False(HighbridgeUri.TryParse("highbridge://scan?v=1&origin=https%3A%2F%2Fa.example&request=0123456789abcdef&stamp=Signed+by+CEO", out _, out string? error));
        Assert.Contains("stamp", error);
    }

    [Fact]
    public void Policy_file_reads_the_stamp_settings()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"provenanceStamp": {"mode": "required", "position": "top"}}""");
            var policy = HighbridgePolicy.Load(path);
            Assert.Equal(StampMode.Required, policy.ProvenanceStamp!.Mode);
            Assert.Equal(StampPosition.Top, policy.ProvenanceStamp.Position);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HighbridgeRuntime Runtime(HighbridgePolicy? policy = null, TimeProvider? time = null, IScannerBackend? backend = null) =>
        new(
            backend ?? TestRuntime.FastMock(),
            new FixedConsentService(allow: true),
            new SettingsStore(path: null),
            policy ?? new HighbridgePolicy(),
            time,
            identity: Identity);

    private sealed class ZonedTimeProvider(DateTimeOffset utcNow, TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Test", offset, "Test", "Test");
    }

    /// <summary>Delivers one page, waits for <see cref="Release"/>, then delivers a second.</summary>
    private sealed class GatedBackend(byte[]? pageData = null, bool released = false) : IScannerBackend
    {
        public TaskCompletionSource FirstPageDelivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = released ? CompletedSource() : new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "gated";

        public Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScannerInfo>>([new ScannerInfo("gated", "gated scanner", null, Name, IsDefault: true)]);

        public async Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken)
        {
            var pages = new List<ScannedPage>();
            for (int i = 0; i < 2; i++)
            {
                byte[] data = pageData ?? ProvenanceStamperTests.ColorPage(120, 80, 100, seed: i);
                var page = new ScannedPage(i, "image/png", 120, 80, data);
                pages.Add(page);
                onPage?.Invoke(page);
                if (i == 0)
                {
                    FirstPageDelivered.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }
            }

            return pages;
        }

        private static TaskCompletionSource CompletedSource()
        {
            var source = new TaskCompletionSource();
            source.SetResult();
            return source;
        }
    }
}
