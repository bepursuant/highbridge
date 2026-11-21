using System.Text.Json;
using Highbridge.Devices;
using Highbridge.Jobs;
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
        var scanners = await runtime.Backend.ListScannersAsync(cancellationToken).ConfigureAwait(false);
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
                var scanners = await runtime.Backend.ListScannersAsync(ctx.RequestAborted).ConfigureAwait(false);
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
                options = await DeviceOptions.ResolveScanAsync(runtime.Settings, runtime.Backend, request, ctx.RequestAborted).ConfigureAwait(false);
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
