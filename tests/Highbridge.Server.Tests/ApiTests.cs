using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PdfSharp.Pdf.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Highbridge.Barcodes;
using Highbridge.Cards;
using Highbridge.Configuration;
using Highbridge.Consent;
using Highbridge.Imaging;
using Highbridge.Scanning;

namespace Highbridge.Server.Tests;

public sealed class ApiTests : IAsyncLifetime
{
    private const string Site = "https://app.example.com";
    private const string OtherSite = "https://other.example.com";

    private HighbridgeRuntime _runtime = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    private FixedConsentService Consent { get; set; } = new(allow: true);

    private HighbridgePolicy Policy { get; set; } = new();

    public Task InitializeAsync() => StartAsync();

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _runtime.Dispose();
    }

    [Fact]
    public async Task Rejects_requests_with_a_foreign_host_header()
    {
        var request = Request(HttpMethod.Get, "/v1/status", Site);
        request.Headers.Host = "attacker.example:47116";

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("http://insecure.example.com")]
    [InlineData("file://")]
    public async Task Rejects_missing_or_insecure_origins_without_cors(string? origin)
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, "/v1/status", origin));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("origin_not_allowed", (await Error(response)).Error);
    }

    [Fact]
    public async Task Answers_cors_and_private_network_preflight_for_https_origins()
    {
        var request = Request(HttpMethod.Options, "/v1/scans", Site);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        request.Headers.Add("Access-Control-Request-Private-Network", "true");

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Site, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Private-Network").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Status_is_readable_by_unpaired_sites_but_scanning_is_not()
    {
        var status = await GetJson<StatusResponse>("/v1/status", Site);
        Assert.Equal("highbridge", status.App);
        Assert.Equal("mock", status.Backend);
        Assert.False(status.Paired);

        var scanners = await _http.SendAsync(Request(HttpMethod.Get, "/v1/scanners", Site));
        Assert.Equal(HttpStatusCode.Forbidden, scanners.StatusCode);
        Assert.Equal(Site, scanners.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("origin_not_paired", (await Error(scanners)).Error);

        var scan = await _http.SendAsync(Request(HttpMethod.Post, "/v1/scans", Site));
        Assert.Equal("origin_not_paired", (await Error(scan)).Error);
    }

    [Fact]
    public async Task Full_flow_pair_scan_poll_download_delete()
    {
        var pair = await _http.SendAsync(Request(HttpMethod.Post, "/v1/pair", Site));
        Assert.Equal(HttpStatusCode.OK, pair.StatusCode);
        Assert.True((await GetJson<StatusResponse>("/v1/status", Site)).Paired);

        var scanners = await GetJson<ScannersResponse>("/v1/scanners", Site);
        Assert.Contains(scanners.Scanners, s => s.Id == MockScannerBackend.FeederId && s.IsDefault);

        var create = Request(HttpMethod.Post, "/v1/scans", Site);
        create.Content = JsonContent.Create(new { dpi = 150, colorMode = "gray", source = "feeder", duplex = true });
        var created = await _http.SendAsync(create);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = await Json<ScanJobResponse>(created);
        Assert.Equal($"/v1/scans/{job.Id}", created.Headers.Location!.OriginalString);

        job = await PollUntilFinished(job.Id, Site);
        Assert.Equal(Jobs.ScanJobStatus.Completed, job.Status);
        Assert.Equal(OutputFormat.Png, job.Format);
        Assert.Equal(4, job.Pages.Count);

        var page = await _http.SendAsync(Request(HttpMethod.Get, job.Pages[1].Url, Site));
        Assert.Equal("image/png", page.Content.Headers.ContentType!.MediaType);
        byte[] png = await page.Content.ReadAsByteArrayAsync();
        Assert.True(PngEncoder.TryReadSize(png, out int w, out int h));
        Assert.Equal((job.Pages[1].Width, job.Pages[1].Height), (w, h));
        Assert.Equal(job.Pages[1].Size, png.Length);

        Assert.Equal(HttpStatusCode.NoContent, (await _http.SendAsync(Request(HttpMethod.Delete, $"/v1/scans/{job.Id}", Site))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Request(HttpMethod.Get, $"/v1/scans/{job.Id}", Site))).StatusCode);
    }

    [Fact]
    public async Task Paired_site_scans_repeatedly_with_only_the_one_pairing_prompt()
    {
        await _http.SendAsync(Request(HttpMethod.Post, "/v1/pair", Site));

        for (int i = 0; i < 3; i++)
        {
            var job = await Json<ScanJobResponse>(await _http.SendAsync(Request(HttpMethod.Post, "/v1/scans", Site)));
            Assert.Equal(Jobs.ScanJobStatus.Completed, (await PollUntilFinished(job.Id, Site)).Status);
        }

        Assert.Equal(1, Consent.PairingRequests);
    }

    [Fact]
    public async Task Protocol_scan_starts_when_the_owning_site_polls_for_it()
    {
        _runtime.Origins.Pair(Site);
        _runtime.Origins.Pair(OtherSite);
        string id = Jobs.ScanJobManager.NewId();
        _runtime.HandleProtocolUrl(Protocol.HighbridgeUri.BuildScan(Site, id, new ScanOptions { Source = PaperSource.Flatbed }));

        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Request(HttpMethod.Get, $"/v1/scans/{id}", OtherSite))).StatusCode);
        await Task.Delay(50);
        Assert.False(_runtime.Jobs.Get(id, Site)!.IsClaimed);

        var job = await PollUntilFinished(id, Site);

        Assert.Equal(Jobs.ScanJobStatus.Completed, job.Status);
        Assert.Equal(Jobs.ScanTrigger.ProtocolUrl, job.Trigger);
        Assert.Single(job.Pages);
    }

    [Fact]
    public async Task Another_paired_site_cannot_see_someone_elses_scan()
    {
        _runtime.Origins.Pair(Site);
        _runtime.Origins.Pair(OtherSite);
        var created = await _http.SendAsync(Request(HttpMethod.Post, "/v1/scans", Site));
        var job = await Json<ScanJobResponse>(created);

        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Request(HttpMethod.Get, $"/v1/scans/{job.Id}", OtherSite))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Request(HttpMethod.Get, $"/v1/scans/{job.Id}/pages/0", OtherSite))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Request(HttpMethod.Delete, $"/v1/scans/{job.Id}", OtherSite))).StatusCode);
    }

    [Fact]
    public async Task Declined_pairing_is_remembered_for_a_while()
    {
        await RestartAsync(consent: new FixedConsentService(allow: false));

        var first = await _http.SendAsync(Request(HttpMethod.Post, "/v1/pair", Site));
        var second = await _http.SendAsync(Request(HttpMethod.Post, "/v1/pair", Site));

        Assert.Equal("pairing_denied", (await Error(first)).Error);
        Assert.Equal("pairing_denied", (await Error(second)).Error);
        Assert.Equal(1, Consent.PairingRequests);
    }

    [Fact]
    public async Task Policy_can_disable_pairing_and_preapprove_origins()
    {
        await RestartAsync(policy: new HighbridgePolicy { AllowPairing = false, AllowedOrigins = [OtherSite] });

        var pair = await _http.SendAsync(Request(HttpMethod.Post, "/v1/pair", Site));
        Assert.Equal("pairing_disabled", (await Error(pair)).Error);
        Assert.Equal(0, Consent.PairingRequests);

        var status = await GetJson<StatusResponse>("/v1/status", OtherSite);
        Assert.True(status.Paired);
        Assert.False(status.PairingAllowed);
    }

    [Fact]
    public async Task Policy_can_reject_http_localhost_pages()
    {
        await RestartAsync(policy: new HighbridgePolicy { AllowInsecureLoopbackOrigins = false });
        var response = await _http.SendAsync(Request(HttpMethod.Get, "/v1/status", "http://localhost:47200"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Barcode_request_returns_per_page_and_flat_results()
    {
        _runtime.Origins.Pair(Site);
        var create = Request(HttpMethod.Post, "/v1/scans", Site);
        create.Content = JsonContent.Create(new { scannerId = MockScannerBackend.BarcodesId, dpi = 120, barcodes = true, maxPages = 1 });

        // A full-page search can outlast the product's 3s below-normal budget on a busy CI runner.
        var job = await PollUntilFinished((await Json<ScanJobResponse>(await _http.SendAsync(create))).Id, Site, TimeSpan.FromSeconds(25));

        Assert.Equal(Jobs.ScanJobStatus.Completed, job.Status);
        Assert.Equal(BarcodeStatus.Done, job.Pages[0].BarcodeStatus);
        var barcodes = Assert.IsAssignableFrom<IReadOnlyList<BarcodeResult>>(job.Barcodes);
        Assert.Equal(barcodes.Select(b => b.Text).Order(), job.Pages[0].Barcodes!.Select(b => b.Text).Order());
        Assert.Contains(barcodes, b => b.Format == BarcodeFormat.Code128 && b.Rotation == 90 && b.Orientation == 270);
        Assert.All(barcodes, b => Assert.True(b.Points.Count >= 2));
    }

    [Fact]
    public async Task A_scan_without_barcodes_omits_them()
    {
        _runtime.Origins.Pair(Site);
        var job = await PollUntilFinished((await Json<ScanJobResponse>(await _http.SendAsync(Request(HttpMethod.Post, "/v1/scans", Site)))).Id, Site);

        Assert.Null(job.Barcodes);
        Assert.All(job.Pages, p => Assert.Null(p.BarcodeStatus));
    }

    [Theory]
    [InlineData("{\"dpi\": 5}")]
    [InlineData("{\"barcodeFormats\": [\"qrCode\"]}")]
    [InlineData("{\"barcodes\": true, \"barcodeFormats\": [\"upc\"]}")]
    [InlineData("{\"colorMode\": \"sepia\"}")]
    [InlineData("{\"colorMode\": 1}")]
    [InlineData("{\"format\": \"tiff\"}")]
    [InlineData("not json")]
    public async Task Invalid_scan_options_are_rejected(string body)
    {
        _runtime.Origins.Pair(Site);
        var request = Request(HttpMethod.Post, "/v1/scans", Site);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_options", (await Error(response)).Error);
    }

    [Fact]
    public async Task Scanner_failures_surface_as_job_errors()
    {
        _runtime.Origins.Pair(Site);
        var create = Request(HttpMethod.Post, "/v1/scans", Site);
        create.Content = JsonContent.Create(new { scannerId = MockScannerBackend.JammedId });
        var job = await Json<ScanJobResponse>(await _http.SendAsync(create));

        job = await PollUntilFinished(job.Id, Site);

        Assert.Equal(Jobs.ScanJobStatus.Failed, job.Status);
        Assert.Equal("paper_jam", job.Error!.Error);
    }

    [Fact]
    public async Task Stamped_scan_returns_provenance_and_embeds_it_in_each_page()
    {
        _runtime.Origins.Pair(Site);
        var create = Request(HttpMethod.Post, "/v1/scans", Site);
        create.Content = JsonContent.Create(new { source = "feeder", stamp = new { position = "top" } });
        var created = await Json<ScanJobResponse>(await _http.SendAsync(create));
        Assert.Equal(Provenance.StampPosition.Top, created.Stamp!.Position);
        Assert.False(created.Stamp.RequiredByPolicy);

        var job = await PollUntilFinished(created.Id, Site);

        Assert.Equal(Jobs.ScanJobStatus.Completed, job.Status);
        Assert.Equal(2, job.PagesScanned);
        foreach (var info in job.Pages)
        {
            var provenance = info.Provenance!;
            Assert.Equal((Site, created.Stamp.ScanId, job.Pages.Count), (provenance.Origin, provenance.ScanId, provenance.PageCount));
            Assert.Equal(Environment.MachineName, provenance.Workstation);
            Assert.EndsWith("\\" + Environment.UserName, provenance.User);

            byte[] png = await (await _http.SendAsync(Request(HttpMethod.Get, info.Url, Site))).Content.ReadAsByteArrayAsync();
            var check = Provenance.ProvenanceVerifier.Verify(png);
            Assert.True(check.Valid, check.Problem);
            Assert.Equal(provenance, check.Provenance);
        }
    }

    [Theory]
    [InlineData("{\"stamp\": {\"position\": \"top\", \"text\": \"Signed by the CEO\"}}")]
    [InlineData("{\"stamp\": {\"user\": \"someone else\"}}")]
    [InlineData("{\"stamp\": {\"position\": \"left\"}}")]
    [InlineData("{\"stamp\": true}")]
    public async Task Pages_cannot_supply_provenance_values(string body)
    {
        _runtime.Origins.Pair(Site);
        var request = Request(HttpMethod.Post, "/v1/scans", Site);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_options", (await Error(response)).Error);
    }

    [Fact]
    public async Task Site_approved_under_old_wording_is_asked_again_before_its_first_stamped_scan()
    {
        _runtime.Settings.Update(s => s.PairedOrigins.Add(new PairedOrigin { Origin = Site }));
        Assert.True((await GetJson<StatusResponse>("/v1/status", Site)).Paired);

        var stamped = Request(HttpMethod.Post, "/v1/scans", Site);
        stamped.Content = JsonContent.Create(new { stamp = new { } });
        var rejected = await _http.SendAsync(stamped);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal("approval_required", (await Error(rejected)).Error);

        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(Request(HttpMethod.Post, "/v1/pair", Site))).StatusCode);
        Assert.Equal(1, Consent.PairingRequests);
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(Request(HttpMethod.Post, "/v1/pair", Site))).StatusCode);
        Assert.Equal(1, Consent.PairingRequests);

        var retry = Request(HttpMethod.Post, "/v1/scans", Site);
        retry.Content = JsonContent.Create(new { stamp = new { } });
        Assert.Equal(HttpStatusCode.Accepted, (await _http.SendAsync(retry)).StatusCode);
    }

    [Fact]
    public async Task Policy_required_stamp_applies_to_scans_that_did_not_ask()
    {
        await RestartAsync(policy: new HighbridgePolicy { ProvenanceStamp = new Provenance.ProvenanceStampPolicy { Mode = Provenance.StampMode.Required } });
        _runtime.Origins.Pair(Site);

        var created = await Json<ScanJobResponse>(await _http.SendAsync(Request(HttpMethod.Post, "/v1/scans", Site)));
        var job = await PollUntilFinished(created.Id, Site);

        Assert.True(job.Stamp!.RequiredByPolicy);
        Assert.All(job.Pages, p => Assert.Equal(Provenance.StampPosition.Bottom, p.Provenance!.Position));
    }

    [Fact]
    public async Task Pdf_scan_returns_one_document_per_job_and_png_pages_still_download()
    {
        _runtime.Origins.Pair(Site);
        var mock = (MockScannerBackend)_runtime.Backend;
        mock.LoadSheets(2);
        try
        {
            var first = await FinishedPdfSheet();
            var second = await FinishedPdfSheet();

            Assert.Equal(OutputFormat.Pdf, first.Format);
            Assert.Equal(2, first.PagesScanned);
            Assert.Equal("application/pdf", Assert.Single(first.Pages).MediaType);
            byte[] firstPdf = await Download(first.Pages[0].Url);
            byte[] secondPdf = await Download(second.Pages[0].Url);
            Assert.Equal("%PDF"u8.ToArray(), firstPdf[..4]);
            Assert.Equal(2, PdfPageCount(firstPdf));
            Assert.Equal(2, PdfPageCount(secondPdf));
            Assert.NotEqual(firstPdf, secondPdf);

            var pngRequest = Request(HttpMethod.Post, "/v1/scans", Site);
            pngRequest.Content = JsonContent.Create(new { source = "flatbed", scannerId = MockScannerBackend.FlatbedId });
            var png = await PollUntilFinished((await Json<ScanJobResponse>(await _http.SendAsync(pngRequest))).Id, Site);
            Assert.Equal(OutputFormat.Png, png.Format);
            Assert.Equal("image/png", Assert.Single(png.Pages).MediaType);
            byte[] pngBytes = await Download(png.Pages[0].Url);
            Assert.True(PngEncoder.TryReadSize(pngBytes, out _, out _));
        }
        finally
        {
            mock.LoadSheets(2);
        }
    }

    private async Task<ScanJobResponse> FinishedPdfSheet()
    {
        var create = Request(HttpMethod.Post, "/v1/scans", Site);
        create.Content = JsonContent.Create(new { source = "feeder", duplex = true, oneSheet = true, format = "pdf" });
        return await PollUntilFinished((await Json<ScanJobResponse>(await _http.SendAsync(create))).Id, Site);
    }

    private async Task<byte[]> Download(string url)
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, url, Site));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static int PdfPageCount(byte[] pdf)
    {
        using var document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    [Fact]
    public async Task One_sheet_returns_front_and_back_and_waits_when_the_feeder_is_empty()
    {
        _runtime.Origins.Pair(Site);
        var first = await FinishedOneSheet();
        var second = await FinishedOneSheet();

        Assert.Equal(Jobs.ScanJobStatus.Completed, first.Status);
        Assert.Equal(2, first.Pages.Count);
        Assert.True(first.UsedFeeder);
        Assert.Equal(2, second.Pages.Count);

        var create = Request(HttpMethod.Post, "/v1/scans", Site);
        create.Content = JsonContent.Create(new { source = "feeder", duplex = true, oneSheet = true });
        var third = await Json<ScanJobResponse>(await _http.SendAsync(create));
        await Task.Delay(150);
        var pending = await GetJson<ScanJobResponse>($"/v1/scans/{third.Id}", Site);
        Assert.Null(pending.FinishedAt);
        Assert.Equal(Jobs.ScanJobStatus.Scanning, pending.Status);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.SendAsync(Request(HttpMethod.Delete, $"/v1/scans/{third.Id}", Site))).StatusCode);
    }

    private async Task<ScanJobResponse> FinishedOneSheet()
    {
        var create = Request(HttpMethod.Post, "/v1/scans", Site);
        create.Content = JsonContent.Create(new { source = "feeder", duplex = true, oneSheet = true });
        var created = await Json<ScanJobResponse>(await _http.SendAsync(create));
        return await PollUntilFinished(created.Id, Site);
    }

    private async Task StartAsync()
    {
        // Normal priority and a longer page budget than production. The product still uses a
        // below-normal thread and a 3s budget; on a shared CI runner that budget expires before
        // every rotation is searched and the page comes back Partial.
        _runtime = new HighbridgeRuntime(
            new MockScannerBackend(new MockScannerOptions { PageDelay = TimeSpan.FromMilliseconds(1), FeederSheets = 2 }),
            new MockCardReaderBackend(),
            Consent,
            new SettingsStore(path: null),
            Policy,
            barcodeWorker: new BarcodeWorker(pageBudget: TimeSpan.FromSeconds(20), priority: ThreadPriority.Normal));
        _app = HighbridgeServer.Build(_runtime, b => b.WebHost.UseTestServer());
        await _app.StartAsync();
        _http = _app.GetTestClient();
        _http.BaseAddress = new Uri($"http://127.0.0.1:{HighbridgeRuntime.FixedPort}");
    }

    private async Task RestartAsync(FixedConsentService? consent = null, HighbridgePolicy? policy = null)
    {
        await DisposeAsync();
        Consent = consent ?? Consent;
        Policy = policy ?? Policy;
        await StartAsync();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? origin)
    {
        var request = new HttpRequestMessage(method, path);
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return request;
    }

    private async Task<T> GetJson<T>(string path, string origin)
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, path, origin));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json<T>(response);
    }

    private async Task<ScanJobResponse> PollUntilFinished(string id, string origin, TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(5);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < wait)
        {
            var job = await GetJson<ScanJobResponse>($"/v1/scans/{id}", origin);
            if (job.FinishedAt is not null)
            {
                return job;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("scan did not finish");
    }

    private static async Task<T> Json<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(HighbridgeJson.Options))!;

    private static Task<ErrorResponse> Error(HttpResponseMessage response) => Json<ErrorResponse>(response);
}
