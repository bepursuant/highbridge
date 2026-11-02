using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Highbridge.Consent;
using Highbridge.Jobs;
using Highbridge.Scanning;

namespace Highbridge.Server;

public static class HighbridgeServer
{
    public const int ApiVersion = 1;
    private const string OriginItemKey = "highbridge.origin";
    private const long MaxRequestBodyBytes = 64 * 1024;

    public static string AppVersion { get; } =
        typeof(HighbridgeServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>
    /// Builds the loopback-only API host. <paramref name="configure"/> runs before Build() (tests use
    /// it to swap in TestServer; hosts use it to configure logging).
    /// </summary>
    public static WebApplication Build(HighbridgeRuntime runtime, Action<WebApplicationBuilder>? configure = null)
    {
        // Explicit content root: when Windows launches us as a URL handler the working directory can be System32.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "Highbridge",
            ContentRootPath = AppContext.BaseDirectory,
        });
        int port = runtime.Port;
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port);
            k.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            k.AddServerHeader = false;
        });
        builder.Services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.AddSingleton(runtime);
        configure?.Invoke(builder);

        var app = builder.Build();
        app.Use((ctx, next) => GuardAsync(ctx, next, runtime, port));
        MapEndpoints(app, runtime);
        return app;
    }

    /// <summary>
    /// Host check (DNS rebinding), Origin validation and per-request CORS. Runs before every endpoint.
    /// </summary>
    private static async Task GuardAsync(HttpContext ctx, Func<Task> next, HighbridgeRuntime runtime, int port)
    {
        if (!IsAllowedHost(ctx.Request.Host, port))
        {
            await WriteError(ctx, StatusCodes.Status421MisdirectedRequest, "invalid_host", "Requests must target 127.0.0.1.");
            return;
        }

        string? rawOrigin = ctx.Request.Headers.Origin;
        if (!runtime.Origins.TryNormalize(rawOrigin, out string origin))
        {
            // No CORS headers: an origin we never serve cannot read this response.
            await WriteError(ctx, StatusCodes.Status403Forbidden, "origin_not_allowed", "highbridge only serves https web pages.");
            return;
        }

        var headers = ctx.Response.Headers;
        headers.AccessControlAllowOrigin = rawOrigin;
        headers.Vary = "Origin";
        headers.CacheControl = "no-store";
        headers["X-Content-Type-Options"] = "nosniff";

        if (HttpMethods.IsOptions(ctx.Request.Method))
        {
            headers.AccessControlAllowMethods = "GET, POST, DELETE";
            headers.AccessControlAllowHeaders = "Content-Type";
            headers.AccessControlMaxAge = "600";
            if (string.Equals(ctx.Request.Headers["Access-Control-Request-Private-Network"], "true", StringComparison.OrdinalIgnoreCase))
            {
                // Legacy Private Network Access preflight (superseded by Local Network Access); harmless to answer.
                headers["Access-Control-Allow-Private-Network"] = "true";
            }

            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        ctx.Items[OriginItemKey] = origin;
        await next();
    }

    private static bool IsAllowedHost(HostString host, int port) =>
        host.HasValue && host.Port == port && Highbridge.Security.OriginRules.IsLoopbackHost(host.Host is "::1" ? "[::1]" : host.Host);

    private static void MapEndpoints(WebApplication app, HighbridgeRuntime runtime)
    {
        var pairingPrompt = new SemaphoreSlim(1, 1);
        var v1 = app.MapGroup("/v1");

        v1.MapGet("/status", (HttpContext ctx) =>
        {
            string origin = OriginOf(ctx);
            return Results.Ok(new StatusResponse("highbridge", AppVersion, ApiVersion, runtime.Backend.Name, runtime.Origins.IsPaired(origin), runtime.Origins.AllowPairing));
        });

        v1.MapPost("/pair", async (HttpContext ctx) =>
        {
            string origin = OriginOf(ctx);
            if (IsApproved(runtime, origin) || (runtime.Origins.IsPaired(origin) && !runtime.Origins.AllowPairing))
            {
                return Results.Ok(new PairResponse(true));
            }

            if (!runtime.Origins.AllowPairing)
            {
                return Error(StatusCodes.Status403Forbidden, "pairing_disabled", "Your administrator only allows pre-approved sites.");
            }

            if (runtime.Origins.IsInDenialCooldown(origin))
            {
                return Error(StatusCodes.Status403Forbidden, "pairing_denied", "The user declined access for this site. Try again later.");
            }

            // One prompt at a time so a page cannot stack dialogs.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            timeout.CancelAfter(Highbridge.Security.OriginRegistry.PairingPromptTimeout);
            try
            {
                await pairingPrompt.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                return Error(StatusCodes.Status409Conflict, "busy", "Another site is waiting for approval.");
            }

            try
            {
                if (IsApproved(runtime, origin))
                {
                    return Results.Ok(new PairResponse(true));
                }

                bool approved;
                try
                {
                    approved = await runtime.Consent.RequestPairingAsync(new PairingRequest(origin), timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    approved = false;
                }

                if (!approved)
                {
                    runtime.Origins.RecordDenial(origin);
                    return Error(StatusCodes.Status403Forbidden, "pairing_denied", "The user declined access for this site.");
                }

                runtime.Origins.Pair(origin);
                return Results.Ok(new PairResponse(true));
            }
            finally
            {
                pairingPrompt.Release();
            }
        });

        v1.MapGet("/scanners", async (HttpContext ctx) =>
        {
            if (RequirePaired(ctx, runtime) is { } denied)
            {
                return denied;
            }

            try
            {
                var scanners = await runtime.Backend.ListScannersAsync(ctx.RequestAborted);
                return Results.Ok(new ScannersResponse(scanners));
            }
            catch (ScannerException ex)
            {
                return Error(StatusCodes.Status503ServiceUnavailable, ex.Code, ex.Message);
            }
        });

        v1.MapPost("/scans", async (HttpContext ctx) =>
        {
            if (RequirePaired(ctx, runtime) is { } denied)
            {
                return denied;
            }

            ScanOptions options;
            try
            {
                options = ctx.Request.ContentLength is 0 or null && !ctx.Request.HasJsonContentType()
                    ? new ScanOptions()
                    : await ctx.Request.ReadFromJsonAsync<ScanOptions>(HighbridgeJson.Options, ctx.RequestAborted) ?? new ScanOptions();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_options", "The request body is not valid scan options JSON.");
            }

            try
            {
                var job = runtime.Jobs.Start(OriginOf(ctx), options, ScanTrigger.Api);
                return Results.Accepted($"/v1/scans/{job.Id}", ScanJobResponse.From(job));
            }
            catch (JobRejectedException ex)
            {
                return Error(ex.Code switch
                {
                    "too_many_jobs" => StatusCodes.Status429TooManyRequests,
                    "approval_required" or "stamp_disabled" => StatusCodes.Status403Forbidden,
                    _ => StatusCodes.Status400BadRequest,
                }, ex.Code, ex.Message);
            }
        });

        v1.MapGet("/scans/{id}", (HttpContext ctx, string id) =>
        {
            if (RequirePaired(ctx, runtime) is { } denied)
            {
                return denied;
            }

            return runtime.Jobs.Claim(id, OriginOf(ctx)) is { } job
                ? Results.Ok(ScanJobResponse.From(job))
                : Error(StatusCodes.Status404NotFound, "not_found", "No such scan.");
        });

        v1.MapGet("/scans/{id}/pages/{index:int}", (HttpContext ctx, string id, int index) =>
        {
            if (RequirePaired(ctx, runtime) is { } denied)
            {
                return denied;
            }

            var page = runtime.Jobs.Get(id, OriginOf(ctx))?.Pages.FirstOrDefault(p => p.Index == index);
            return page is null
                ? Error(StatusCodes.Status404NotFound, "not_found", "No such page.")
                : Results.File(page.Data, page.MediaType);
        });

        v1.MapDelete("/scans/{id}", (HttpContext ctx, string id) =>
        {
            if (RequirePaired(ctx, runtime) is { } denied)
            {
                return denied;
            }

            return runtime.Jobs.Delete(id, OriginOf(ctx))
                ? Results.NoContent()
                : Error(StatusCodes.Status404NotFound, "not_found", "No such scan.");
        });

        app.MapFallback(() => Error(StatusCodes.Status404NotFound, "not_found", "Unknown endpoint."));
    }

    /// <summary>
    /// Paired with the current approval wording. A site approved under older wording is asked
    /// again, so the user sees that stamped scans include their user and computer names.
    /// </summary>
    private static bool IsApproved(HighbridgeRuntime runtime, string origin) =>
        runtime.Origins.IsPaired(origin) && runtime.Origins.ApprovalCoversIdentity(origin);

    private static string OriginOf(HttpContext ctx) => (string)ctx.Items[OriginItemKey]!;

    private static IResult? RequirePaired(HttpContext ctx, HighbridgeRuntime runtime) =>
        runtime.Origins.IsPaired(OriginOf(ctx))
            ? null
            : Error(StatusCodes.Status403Forbidden, "origin_not_paired", "Pair this site first (POST /v1/pair).");

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorResponse(code, message), HighbridgeJson.Options, statusCode: status);

    private static Task WriteError(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new ErrorResponse(code, message), HighbridgeJson.Options);
    }
}

public static class HighbridgeServerLogging
{
    /// <summary>Keeps ASP.NET Core's per-request logging quiet in the tray app.</summary>
    public static void UseQuietLogging(this WebApplicationBuilder builder)
    {
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    }
}
