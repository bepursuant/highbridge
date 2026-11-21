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
