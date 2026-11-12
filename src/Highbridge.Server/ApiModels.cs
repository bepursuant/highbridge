using Highbridge.Barcodes;
using Highbridge.Cards;
using Highbridge.Jobs;
using Highbridge.Provenance;
using Highbridge.Scanning;

namespace Highbridge.Server;

public sealed record DeviceStatus(string Kind, string Backend);

public sealed record StatusResponse(
    string App,
    string Version,
    int ApiVersion,
    string Backend,
    IReadOnlyList<DeviceStatus> Devices,
    bool Paired,
    bool PairingAllowed);

public sealed record PairResponse(bool Paired);

public sealed record ScannersResponse(IReadOnlyList<ScannerInfo> Scanners);

public sealed record CardReadersResponse(IReadOnlyList<CardReaderInfo> Readers);

public sealed record ErrorResponse(string Error, string Message);

/// <param name="Provenance">Only on stamped pages.</param>
/// <param name="BarcodeStatus">Only when the scan asked for barcodes, like <paramref name="Barcodes"/>.</param>
public sealed record PageResponse(
    int Index,
    string MediaType,
    int Width,
    int Height,
    int Size,
    string Url,
    PageProvenance? Provenance = null,
    BarcodeStatus? BarcodeStatus = null,
    IReadOnlyList<BarcodeResult>? Barcodes = null);

/// <summary>The stamp a job actually gets, after policy.</summary>
public sealed record StampResponse(StampPosition Position, bool RequiredByPolicy, string ScanId);

/// <param name="Barcodes">Every page's barcodes in one list; only when the scan asked for barcodes.</param>
public sealed record ScanJobResponse(
    string Id,
    ScanJobStatus Status,
    ScanTrigger Trigger,
    OutputFormat Format,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    ErrorResponse? Error,
    IReadOnlyList<PageResponse> Pages,
    IReadOnlyList<BarcodeResult>? Barcodes = null)
{
    public StampResponse? Stamp { get; init; }

    /// <summary>Pages received so far; ahead of <see cref="Pages"/> while a stamped scan runs.</summary>
    public int PagesScanned { get; init; }

    /// <summary>One-sheet jobs only. False when the scanner did not use the feeder.</summary>
    public bool? UsedFeeder { get; init; }

    public static ScanJobResponse From(ScanJob job) => new(
        job.Id,
        job.Status,
        job.Trigger,
        job.Options.Format,
        job.CreatedAt,
        job.FinishedAt,
        job.ErrorCode is null ? null : new ErrorResponse(job.ErrorCode, job.ErrorMessage ?? job.ErrorCode),
        job.Pages.Select(p => WithBarcodes(new PageResponse(p.Index, p.MediaType, p.Width, p.Height, p.Data.Length, $"/v1/scans/{job.Id}/pages/{p.Index}", p.Provenance), job.Barcodes)).ToArray(),
        job.Barcodes?.All)
    {
        Stamp = job.Stamp is { } stamp ? new StampResponse(stamp.Position, job.StampRequiredByPolicy, job.ScanId!) : null,
        PagesScanned = job.PagesScanned,
        UsedFeeder = job.UsedFeeder,
    };

    private static PageResponse WithBarcodes(PageResponse page, JobBarcodes? barcodes) =>
        barcodes?.ForPage(page.Index) is { } found ? page with { BarcodeStatus = found.Status, Barcodes = found.Barcodes } : page;
}

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
