using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Highbridge.Cards;
using Highbridge.Configuration;
using Highbridge.Consent;
using Highbridge.Jobs;
using Highbridge.Protocol;
using Highbridge.Scanning;

namespace Highbridge.Server.Tests;

public sealed class CardApiTests : IAsyncLifetime
{
    private const string Site = "https://app.example.com";
    private const string OtherSite = "https://other.example.com";

    private HighbridgeRuntime _runtime = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private HighbridgePolicy _policy = new();

    public Task InitializeAsync() => StartAsync();

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _runtime.Dispose();
    }

    [Fact]
    public async Task Status_names_the_scanner_and_the_card_reader()
    {
        var status = await GetJson<StatusResponse>("/v1/status", Site);

        Assert.Equal("mock", status.Backend);
        Assert.Equal("scanner", status.Devices[0].Kind);
        Assert.Equal("mock", status.Devices[0].Backend);
        Assert.Equal("cardReader", status.Devices[1].Kind);
        Assert.Equal("mock", status.Devices[1].Backend);
    }

    [Fact]
    public async Task Paired_site_reads_the_mock_card_through_http()
    {
        await Pair();
        var readers = await GetJson<CardReadersResponse>("/v1/card-readers", Site);
        Assert.Contains(readers.Readers, reader => reader.Id == MockCardReaderBackend.SuccessId);
        Assert.DoesNotContain("present", await Body("/v1/card-readers", Site), StringComparison.OrdinalIgnoreCase);

        var created = await Post("/v1/card-reads", Site, """{"interface":"contact"}""");
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = await Json<CardReadJobResponse>(created);
        job = await Poll(job.Id, Site);

        Assert.Equal(CardReadStatus.Completed, job.Status);
        Assert.Equal(MockCardReaderBackend.VisaPan, job.Selected!.Pan);
        Assert.Equal("1234", job.Selected.PanLast4);
        Assert.DoesNotContain(MockCardReaderBackend.VisaPan, job.Selected.ToString(), StringComparison.Ordinal);
        Assert.Equal("2028-12", job.Selected.Expiry);
        Assert.Equal("DOE/JOHN", job.Selected.CardholderName);
        Assert.Equal(2, job.Applications!.Count);
        Assert.Equal(CardInterface.Contact, job.Interface);
        Assert.Null(job.Applications[1].PreferredName);

        Assert.Equal(HttpStatusCode.NoContent, (await _http.SendAsync(Request(HttpMethod.Delete, $"/v1/card-reads/{job.Id}", Site))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Request(HttpMethod.Get, $"/v1/card-reads/{job.Id}", Site))).StatusCode);
    }

    [Theory]
    [InlineData(MockCardReaderBackend.NoCardId, "no_card")]
    [InlineData(MockCardReaderBackend.EmptyId, "no_payment_apps")]
    public async Task Failure_readers_finish_as_failed_jobs(string readerId, string code)
    {
        await Pair();
        var created = await Json<CardReadJobResponse>(await Post("/v1/card-reads", Site, $$"""{"readerId":"{{readerId}}"}"""));
        var job = await Poll(created.Id, Site);

        Assert.Equal(CardReadStatus.Failed, job.Status);
        Assert.Equal(code, job.Error!.Error);
        Assert.Null(job.Selected);
    }

    [Fact]
    public async Task Aid_selects_the_second_application()
    {
        await Pair();
        var created = await Json<CardReadJobResponse>(await Post("/v1/card-reads", Site, """{"aid":"A0000000041010"}"""));
        var job = await Poll(created.Id, Site);

        Assert.Equal(MockCardReaderBackend.MastercardPan, job.Selected!.Pan);
        Assert.Equal("5678", job.Selected.PanLast4);
        Assert.Equal("2027-06", job.Selected.Expiry);
    }

    [Fact]
    public async Task Another_origin_cannot_collect_the_read()
    {
        await Pair();
        var created = await Json<CardReadJobResponse>(await Post("/v1/card-reads", Site, "{}"));
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(Request(HttpMethod.Post, "/v1/pair", OtherSite))).StatusCode);

        var stolen = await _http.SendAsync(Request(HttpMethod.Get, $"/v1/card-reads/{created.Id}", OtherSite));
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
        Assert.Equal("not_found", (await Json<ErrorResponse>(stolen)).Error);
    }

    [Fact]
    public async Task Old_approval_is_asked_again_and_an_apdu_is_rejected()
    {
        _runtime.Settings.Update(s => s.PairedOrigins.Add(new PairedOrigin { Origin = Site, ApprovalVersion = 3 }));
        var rejected = await Post("/v1/card-reads", Site, "{}");
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal("approval_required", (await Json<ErrorResponse>(rejected)).Error);

        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(Request(HttpMethod.Post, "/v1/pair", Site))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post("/v1/card-reads", Site, "{}")).StatusCode);

        var apdu = await Post("/v1/card-reads", Site, """{"apdu":"00A40400"}""");
        Assert.Equal(HttpStatusCode.BadRequest, apdu.StatusCode);
        Assert.Equal("invalid_options", (await Json<ErrorResponse>(apdu)).Error);
    }

    [Fact]
    public async Task Disabled_reader_blocks_card_routes_and_leaves_scans_alone()
    {
        await Restart(new HighbridgePolicy { CardReader = new CardReaderPolicy { Mode = CardReaderMode.Disabled } });
        await Pair();

        var blocked = await _http.SendAsync(Request(HttpMethod.Get, "/v1/card-readers", Site));
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("card_reader_disabled", (await Json<ErrorResponse>(blocked)).Error);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/v1/card-reads", Site, "{}")).StatusCode);

        var scan = await Post("/v1/scans", Site, """{"source":"flatbed","maxPages":1}""");
        Assert.Equal(HttpStatusCode.Accepted, scan.StatusCode);
    }

    [Fact]
    public async Task Protocol_read_starts_only_after_the_owning_origin_claims_it()
    {
        await Pair();
        string id = DeviceJobRules.NewId();
        var started = _runtime.HandleProtocolUrl(HighbridgeUri.BuildReadCard(Site, id, new CardReadOptions()));
        Assert.Equal(ProtocolOutcome.CardReadStarted, started.Outcome);
        Assert.False(_runtime.CardReads.Get(id, Site)!.IsClaimed);

        var job = await Poll(id, Site);
        Assert.Equal(CardReadStatus.Completed, job.Status);
        Assert.Equal(CardReadTrigger.ProtocolUrl, job.Trigger);
        Assert.Equal(MockCardReaderBackend.VisaPan, job.Selected!.Pan);
        Assert.Equal("1234", job.Selected.PanLast4);
    }

    private async Task StartAsync()
    {
        _runtime = new HighbridgeRuntime(
            new MockScannerBackend(new MockScannerOptions { PageDelay = TimeSpan.FromMilliseconds(1), FeederSheets = 1 }),
            new MockCardReaderBackend(),
            new FixedConsentService(allow: true),
            new SettingsStore(path: null),
            _policy);
        _app = HighbridgeServer.Build(_runtime, b => b.WebHost.UseTestServer());
        await _app.StartAsync();
        _http = _app.GetTestClient();
        _http.BaseAddress = new Uri($"http://127.0.0.1:{HighbridgeRuntime.FixedPort}");
    }

    private async Task Restart(HighbridgePolicy policy)
    {
        await DisposeAsync();
        _policy = policy;
        await StartAsync();
    }

    private async Task Pair() =>
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(Request(HttpMethod.Post, "/v1/pair", Site))).StatusCode);

    private async Task<string> Body(string path, string origin) =>
        await (await _http.SendAsync(Request(HttpMethod.Get, path, origin))).Content.ReadAsStringAsync();

    private Task<HttpResponseMessage> Post(string path, string origin, string json)
    {
        var request = Request(HttpMethod.Post, path, origin);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return _http.SendAsync(request);
    }

    private async Task<CardReadJobResponse> Poll(string id, string origin)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            var job = await GetJson<CardReadJobResponse>($"/v1/card-reads/{id}", origin);
            if (job.FinishedAt is not null)
            {
                return job;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("card read did not finish");
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string origin)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", origin);
        return request;
    }

    private async Task<T> GetJson<T>(string path, string origin)
    {
        var response = await _http.SendAsync(Request(HttpMethod.Get, path, origin));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Json<T>(response);
    }

    private static async Task<T> Json<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(HighbridgeJson.Options))!;
}
