using System.Text.Json;
using Highbridge.Barcodes;
using Highbridge.Devices;
using Highbridge.Jobs;
using Highbridge.Provenance;
using Highbridge.Scanning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Highbridge.Server;

public sealed class ScannerModule : IDeviceModule
{
    public string Key => DeviceOptions.Scanner;

    public string Title => "Scanner";

    public Type OptionsType => typeof(ScanOptions);

    public object CreateOptions() => new ScanOptions();

    public void PrepareOptions(object options, DeviceListing device)
    {
        if (options is ScanOptions scan && string.IsNullOrEmpty(scan.ScannerId))
        {
            scan.ScannerId = device.Id;
        }
    }

    public async Task<IReadOnlyList<DeviceListing>> ListAsync(HighbridgeRuntime runtime, CancellationToken cancellationToken)
    {
        var scanners = await runtime.ScannerBackend.ListScannersAsync(cancellationToken).ConfigureAwait(false);
        return scanners
            .OrderByDescending(scanner => scanner.IsDefault)
            .ThenBy(scanner => scanner.Name, StringComparer.OrdinalIgnoreCase)
            .Select(scanner => new DeviceListing(scanner.Id, scanner.Name, scanner.Manufacturer))
            .ToArray();
    }

    public void Map(WebApplication app, HighbridgeRuntime runtime)
    {
        var scanner = app.MapGroup("/scanner");

        scanner.MapGet("/", async (HttpContext ctx) =>
        {
            if (Hub.RequirePaired(ctx, runtime) is { } denied)
            {
                return denied;
            }

            try
            {
                var scanners = await runtime.ScannerBackend.ListScannersAsync(ctx.RequestAborted).ConfigureAwait(false);
                return Results.Ok(new ScannersResponse(scanners));
            }
            catch (ScannerException ex)
            {
                return Hub.Error(StatusCodes.Status503ServiceUnavailable, ex.Code, ex.Message);
            }
        });

        scanner.MapPost("/", async (HttpContext ctx) =>
        {
            if (Hub.RequirePaired(ctx, runtime) is { } denied)
            {
                return denied;
            }

            ScanOptions options;
            try
            {
                var request = await RequestBody.ReadAsync(ctx.Request, ctx.RequestAborted).ConfigureAwait(false);
                options = await DeviceOptions.ResolveScanAsync(runtime.Settings, runtime.ScannerBackend, request, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
            {
                return Hub.InvalidOptions("The request body is not valid scan options JSON.");
            }

            try
            {
                var job = runtime.Jobs.Start(Hub.OriginOf(ctx), options, ScanTrigger.Api);
                return Results.Accepted($"/scanner/{job.Id}", ScanJobResponse.From(job));
            }
            catch (JobRejectedException ex)
            {
                return Hub.Rejected(ex);
            }
        });

        scanner.MapGet("/{id}", (HttpContext ctx, string id) =>
        {
            if (Hub.RequirePaired(ctx, runtime) is { } denied)
            {
                return denied;
            }

            return runtime.Jobs.Claim(id, Hub.OriginOf(ctx)) is { } job
                ? Results.Ok(ScanJobResponse.From(job))
                : Hub.Error(StatusCodes.Status404NotFound, "not_found", "No such scan.");
        });

        scanner.MapGet("/{id}/documents/{index:int}", (HttpContext ctx, string id, int index) =>
        {
            if (Hub.RequirePaired(ctx, runtime) is { } denied)
            {
                return denied;
            }

            var document = runtime.Jobs.Get(id, Hub.OriginOf(ctx))?.Documents.FirstOrDefault(item => item.Index == index);
            return document is null
                ? Hub.Error(StatusCodes.Status404NotFound, "not_found", "No such document.")
                : Results.File(document.Data, document.MediaType);
        });

        scanner.MapDelete("/{id}", (HttpContext ctx, string id) =>
        {
            if (Hub.RequirePaired(ctx, runtime) is { } denied)
            {
                return denied;
            }

            return runtime.Jobs.Delete(id, Hub.OriginOf(ctx))
                ? Results.NoContent()
                : Hub.Error(StatusCodes.Status404NotFound, "not_found", "No such scan.");
        });
    }
}

public sealed record ScannersResponse(IReadOnlyList<ScannerInfo> Scanners);

/// <param name="Provenance">Only on a stamped document.</param>
/// <param name="BarcodeStatus">Only when the scan asked for barcodes, like <paramref name="Barcodes"/>.</param>
/// <param name="PageCount">How many pages were stacked into this file.</param>
public sealed record DocumentResponse(
    int Index,
    string MediaType,
    int Width,
    int Height,
    int Size,
    string Url,
    int PageCount,
    PageProvenance? Provenance = null,
    BarcodeStatus? BarcodeStatus = null,
    IReadOnlyList<BarcodeResult>? Barcodes = null);

/// <summary>The stamp a job actually gets, after policy.</summary>
public sealed record StampResponse(StampPosition Position, bool RequiredByPolicy, string ScanId);

/// <param name="Barcodes">Every document's barcodes in one list; only when the scan asked for barcodes.</param>
public sealed record ScanJobResponse(
    string Id,
    ScanJobStatus Status,
    ScanTrigger Trigger,
    OutputFormat Format,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    ErrorResponse? Error,
    IReadOnlyList<DocumentResponse> Documents,
    IReadOnlyList<BarcodeResult>? Barcodes = null)
{
    public StampResponse? Stamp { get; init; }

    /// <summary>Pages received from the scanner so far. Documents appear once they are stacked.</summary>
    public int PagesScanned { get; init; }

    /// <summary>False when the scanner did not use the feeder.</summary>
    public bool? UsedFeeder { get; init; }

    public static ScanJobResponse From(ScanJob job) => new(
        job.Id,
        job.Status,
        job.Trigger,
        job.Options.Format,
        job.CreatedAt,
        job.FinishedAt,
        job.ErrorCode is null ? null : new ErrorResponse(job.ErrorCode, job.ErrorMessage ?? job.ErrorCode),
        job.Documents.Select(document => WithBarcodes(new DocumentResponse(document.Index, document.MediaType, document.Width, document.Height, document.Data.Length, $"/scanner/{job.Id}/documents/{document.Index}", document.PageCount, document.Provenance), job.Barcodes)).ToArray(),
        job.Barcodes?.All)
    {
        Stamp = job.Stamp is { } stamp ? new StampResponse(stamp.Position, job.StampRequiredByPolicy, job.ScanId!) : null,
        PagesScanned = job.PagesScanned,
        UsedFeeder = job.UsedFeeder,
    };

    private static DocumentResponse WithBarcodes(DocumentResponse document, JobBarcodes? barcodes) =>
        barcodes?.ForPage(document.Index) is { } found ? document with { BarcodeStatus = found.Status, Barcodes = found.Barcodes } : document;
}
