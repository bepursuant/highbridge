using System.Text.Json;
using Highbridge.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Highbridge.Server;

public sealed record DeviceListing(string Id, string Name, string? Detail);

/// <summary>
/// One kind of device. It owns its options POCO, its HTTP routes, and the defaults the tray edits.
/// A later device adds a module. It does not add a branch inside the scanner or card jobs.
/// </summary>
public interface IDeviceModule
{
    string Key { get; }

    string Title { get; }

    Type OptionsType { get; }

    object CreateOptions();

    void PrepareOptions(object options, DeviceListing device);

    Task<IReadOnlyList<DeviceListing>> ListAsync(HighbridgeRuntime runtime, CancellationToken cancellationToken);

    void Map(WebApplication app, HighbridgeRuntime runtime);
}

public static class DeviceModules
{
    public static IReadOnlyList<IDeviceModule> All { get; } = [new ScannerModule(), new CardModule()];
}

static class RequestBody
{
    public static async Task<JsonElement> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is 0 or null && !request.HasJsonContentType())
        {
            return default;
        }

        using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }
}

static class Hub
{
    public const string OriginItemKey = "highbridge.origin";

    public static string OriginOf(HttpContext ctx) => (string)ctx.Items[OriginItemKey]!;

    public static IResult Rejected(JobRejectedException ex) =>
        Error(ex.Code switch
        {
            "too_many_jobs" => StatusCodes.Status429TooManyRequests,
            "approval_required" or "stamp_disabled" or "card_reader_disabled" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        }, ex.Code, ex.Message);

    public static IResult? RequirePaired(HttpContext ctx, HighbridgeRuntime runtime) =>
        runtime.Origins.IsPaired(OriginOf(ctx))
            ? null
            : Error(StatusCodes.Status403Forbidden, "origin_not_paired", "Pair this site first (POST /pair).");

    public static IResult? RequireCard(HttpContext ctx, HighbridgeRuntime runtime)
    {
        if (!runtime.Policy.CardReaderEnabled)
        {
            return Error(StatusCodes.Status403Forbidden, "card_reader_disabled", CardReadManager.DisabledMessage);
        }

        string origin = OriginOf(ctx);
        if (!runtime.Origins.IsPaired(origin))
        {
            return Error(StatusCodes.Status403Forbidden, "origin_not_paired", "Pair this site first (POST /pair).");
        }

        return runtime.Origins.ApprovalCoversIdentity(origin)
            ? null
            : Error(StatusCodes.Status403Forbidden, "approval_required", CardReadManager.ApprovalMessage);
    }

    public static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorResponse(code, message), HighbridgeJson.Options, statusCode: status);

    public static IResult InvalidOptions(string message) =>
        Error(StatusCodes.Status400BadRequest, "invalid_options", message);
}
