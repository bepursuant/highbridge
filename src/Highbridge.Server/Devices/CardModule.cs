using System.Text.Json;
using Highbridge.Cards;
using Highbridge.Devices;
using Highbridge.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Highbridge.Server;

public sealed class CardModule : IDeviceModule
{
    public string Key => DeviceOptions.Card;

    public string Title => "Card reader";

    public Type OptionsType => typeof(CardReadOptions);

    public object CreateOptions() => new CardReadOptions();

    public void PrepareOptions(object options, DeviceListing device)
    {
        if (options is CardReadOptions read && string.IsNullOrEmpty(read.ReaderId))
        {
            read.ReaderId = device.Id;
        }
    }

    public async Task<IReadOnlyList<DeviceListing>> ListAsync(HighbridgeRuntime runtime, CancellationToken cancellationToken)
    {
        var readers = await runtime.CardReads.ListReadersAsync(cancellationToken).ConfigureAwait(false);
        return readers
            .OrderBy(reader => CardReaderSlot.Describe(reader.Name), StringComparer.OrdinalIgnoreCase)
            .ThenBy(reader => reader.Name, StringComparer.OrdinalIgnoreCase)
            .Select(reader => new DeviceListing(reader.Id, reader.Name, CardReaderSlot.Describe(reader.Name)))
            .ToArray();
    }

    public void Map(WebApplication app, HighbridgeRuntime runtime)
    {
        var pcsc = app.MapGroup("/pcsc");

        pcsc.MapGet("/", async (HttpContext ctx) =>
        {
            if (Hub.RequireCard(ctx, runtime) is { } denied)
            {
                return denied;
            }

            try
            {
                var readers = await runtime.CardReads.ListReadersAsync(ctx.RequestAborted).ConfigureAwait(false);
                return Results.Ok(new CardReadersResponse(readers));
            }
            catch (CardReaderException ex)
            {
                return Hub.Error(StatusCodes.Status503ServiceUnavailable, ex.Code, ex.Message);
            }
        });

        pcsc.MapPost("/", async (HttpContext ctx) =>
        {
            if (Hub.RequireCard(ctx, runtime) is { } denied)
            {
                return denied;
            }

            CardReadOptions options;
            try
            {
                var request = await RequestBody.ReadAsync(ctx.Request, ctx.RequestAborted).ConfigureAwait(false);
                options = await DeviceOptions.ResolveCardAsync(runtime.Settings, runtime.CardReaderBackend, request, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
            {
                return Hub.InvalidOptions("The request body is not valid card read options JSON.");
            }

            try
            {
                var job = runtime.CardReads.Start(Hub.OriginOf(ctx), options, CardReadTrigger.Api);
                return Results.Accepted($"/pcsc/{job.Id}", CardReadJobResponse.From(job));
            }
            catch (JobRejectedException ex)
            {
                return Hub.Rejected(ex);
            }
        });

        pcsc.MapGet("/{id}", (HttpContext ctx, string id) =>
        {
            if (Hub.RequireCard(ctx, runtime) is { } denied)
            {
                return denied;
            }

            return runtime.CardReads.Claim(id, Hub.OriginOf(ctx)) is { } job
                ? Results.Ok(CardReadJobResponse.From(job))
                : Hub.Error(StatusCodes.Status404NotFound, "not_found", "No such card read.");
        });

        pcsc.MapDelete("/{id}", (HttpContext ctx, string id) =>
        {
            if (Hub.RequireCard(ctx, runtime) is { } denied)
            {
                return denied;
            }

            return runtime.CardReads.Delete(id, Hub.OriginOf(ctx))
                ? Results.NoContent()
                : Hub.Error(StatusCodes.Status404NotFound, "not_found", "No such card read.");
        });
    }
}

public sealed record CardReadersResponse(IReadOnlyList<CardReaderInfo> Readers);

public sealed record CardReadJobResponse(
    string Id,
    CardReadStatus Status,
    CardReadTrigger Trigger,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    ErrorResponse? Error,
    CardReaderRef? Reader,
    CardInterface? Interface,
    IReadOnlyList<CardApplication>? Applications,
    CardSelection? Selected)
{
    public static CardReadJobResponse From(CardReadJob job)
    {
        var result = job.Result;
        return new CardReadJobResponse(
            job.Id,
            job.Status,
            job.Trigger,
            job.CreatedAt,
            job.FinishedAt,
            job.ErrorCode is null ? null : new ErrorResponse(job.ErrorCode, job.ErrorMessage ?? job.ErrorCode),
            result?.Reader,
            result?.Interface,
            result?.Applications,
            result?.Selected);
    }
}
