using System.Text.Json.Serialization;
using Highbridge.Barcodes;
using Highbridge.Provenance;

namespace Highbridge.Scanning;

[JsonConverter(typeof(CamelCaseEnumConverter<ColorMode>))]
public enum ColorMode
{
    Color,
    Gray,
    BlackWhite,
}

[JsonConverter(typeof(CamelCaseEnumConverter<PaperSource>))]
public enum PaperSource
{
    Auto,
    Flatbed,
    Feeder,
}

/// <summary>
/// What the client downloads. Only <see cref="Png"/> (one image per page) exists today. A
/// document format such as PDF would be a new value here plus a case in <see cref="OutputFormats"/>.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<OutputFormat>))]
public enum OutputFormat
{
    Png,
}

public static class OutputFormats
{
    public static string MediaType(OutputFormat format) => format switch
    {
        OutputFormat.Png => "image/png",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static bool TryParse(string? value, out OutputFormat format)
    {
        switch (value?.ToLowerInvariant())
        {
            case "png":
                format = OutputFormat.Png;
                return true;
            default:
                format = default;
                return false;
        }
    }
}

public sealed record ScanOptions
{
    public const int MinDpi = 50;
    public const int MaxDpi = 1200;
    public const int MaxPageLimit = 500;

    /// <summary>Scanner id from <c>GET /scanner</c>; null means the default scanner.</summary>
    public string? ScannerId { get; set; }

    public int Dpi { get; set; } = 200;

    public ColorMode ColorMode { get; set; } = ColorMode.Color;

    public PaperSource Source { get; set; } = PaperSource.Auto;

    public bool Duplex { get; set; }

    /// <summary>Show the vendor's TWAIN driver dialog instead of scanning with these settings.</summary>
    public bool ShowDriverUi { get; set; }

    /// <summary>
    /// 0 means one document holds every page. A positive value closes the current document and
    /// opens another once that many pages are in it. The front and back of one sheet are two pages.
    /// </summary>
    public int MaxPagesPerDocument { get; set; }

    /// <summary>
    /// 0 means no limit. A positive value stops the feeder after that many sheets. An image-counted
    /// driver is asked for this many sheets times the number of sides. A sheet-counted driver
    /// (PaperStream / fi series) is asked for this many sheets, and duplex still returns both sides.
    /// A flatbed is one shot either way.
    /// </summary>
    public int MaxSheets { get; set; }

    /// <summary>
    /// Clockwise turn applied to each page before stacking, stamping, or barcode search.
    /// 0, 90, 180, or 270.
    /// </summary>
    public int Orientation { get; set; }

    public OutputFormat Format { get; set; } = OutputFormat.Png;

    /// <summary>Add a provenance band to each page; null means no stamp (unless policy requires one).</summary>
    public StampOptions? Stamp { get; set; }

    /// <summary>Find barcodes on each finished document; see <see cref="Highbridge.Barcodes.BarcodeDetector"/>.</summary>
    public bool Barcodes { get; set; }

    /// <summary>Barcode formats to look for; null means <see cref="BarcodeFormatSet.Default"/>. Needs <see cref="Barcodes"/>.</summary>
    [JsonConverter(typeof(BarcodeFormatListConverter))]
    public BarcodeFormat? BarcodeFormats { get; set; }

    /// <summary>Returns a list of validation problems; empty when the options are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Dpi is < MinDpi or > MaxDpi)
        {
            errors.Add($"dpi must be between {MinDpi} and {MaxDpi}.");
        }

        if (MaxPagesPerDocument is < 0 or > MaxPageLimit)
        {
            errors.Add($"maxPagesPerDocument must be between 0 and {MaxPageLimit}.");
        }

        if (MaxSheets is < 0 or > MaxPageLimit)
        {
            errors.Add($"maxSheets must be between 0 and {MaxPageLimit}.");
        }

        if (Orientation is not (0 or 90 or 180 or 270))
        {
            errors.Add("orientation must be 0, 90, 180, or 270.");
        }

        if (ScannerId is { Length: > 256 })
        {
            errors.Add("scannerId is too long.");
        }

        if (!Enum.IsDefined(ColorMode))
        {
            errors.Add("colorMode is not supported.");
        }

        if (!Enum.IsDefined(Source))
        {
            errors.Add("source is not supported.");
        }

        if (!Enum.IsDefined(Format))
        {
            errors.Add("format is not supported.");
        }

        if (Stamp is not null && !Enum.IsDefined(Stamp.Position))
        {
            errors.Add("stamp.position must be top or bottom.");
        }

        errors.AddRange(BarcodeFormatSet.Validate(Barcodes, BarcodeFormats));
        return errors;
    }

    /// <summary>
    /// How many images this scan may return. Null means the feeder runs until it is empty.
    /// A flatbed is one image. A limited feeder is <see cref="MaxSheets"/> times the number of sides.
    /// This is not the value to put in CAP_XFERCOUNT for a sheet-counted driver; see <see cref="FeederXferCount"/>.
    /// </summary>
    public int? ImageLimit()
    {
        if (Source == PaperSource.Flatbed)
        {
            return 1;
        }

        if (MaxSheets <= 0)
        {
            return null;
        }

        return MaxSheets * (Duplex ? 2 : 1);
    }
}

public sealed record ScannerInfo(string Id, string Name, string? Manufacturer, string Backend, bool IsDefault);

public sealed class ScannedPage
{
    public ScannedPage(int index, string mediaType, int width, int height, byte[] data)
    {
        Index = index;
        MediaType = mediaType;
        Width = width;
        Height = height;
        Data = data;
    }

    public int Index { get; }

    public string MediaType { get; }

    public int Width { get; }

    public int Height { get; }

    public byte[] Data { get; }

    /// <summary>The scanner's own name for itself (TWAIN source name), when the backend knows it.</summary>
    public string? ScannerName { get; init; }

    /// <summary>Set on stamped pages: the record printed in the band and embedded in the PNG.</summary>
    public PageProvenance? Provenance { get; private init; }

    /// <summary>A copy with the stamped image. Keep every other per-page property when adding one.</summary>
    public ScannedPage Stamped(byte[] data, int width, int height, PageProvenance provenance) =>
        new(Index, MediaType, width, height, data) { ScannerName = ScannerName, Provenance = provenance };
}

/// <summary>One PNG made by stacking a scan's pages top to bottom. The job returns these, not loose pages.</summary>
public sealed class ScannedDocument
{
    public ScannedDocument(int index, string mediaType, int width, int height, byte[] data, int pageCount)
    {
        Index = index;
        MediaType = mediaType;
        Width = width;
        Height = height;
        Data = data;
        PageCount = pageCount;
    }

    public int Index { get; }

    public string MediaType { get; }

    public int Width { get; }

    public int Height { get; }

    public byte[] Data { get; }

    /// <summary>How many pages were stacked into this file.</summary>
    public int PageCount { get; }

    public string? ScannerName { get; init; }

    public PageProvenance? Provenance { get; private init; }

    public ScannedDocument Stamped(byte[] data, int width, int height, PageProvenance provenance) =>
        new(Index, MediaType, width, height, data, PageCount) { ScannerName = ScannerName, Provenance = provenance };
}

/// <summary>A scanner-side failure with a message that is safe to show to the web page.</summary>
public sealed class ScannerException : Exception
{
    public ScannerException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface IScannerBackend
{
    /// <summary>Short identifier such as "twain" or "mock".</summary>
    string Name { get; }

    Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Scans all pages for one job. <paramref name="onPage"/> is invoked as each page arrives so
    /// callers can expose partial progress.
    /// </summary>
    Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken);

    /// <summary>
    /// When this scan uses the feeder, blocks until paper is loaded or the token is canceled.
    /// Returns false when the scan does not use the feeder (a flatbed, or a source with no feeder).
    /// An empty feeder does not throw.
    /// </summary>
    Task<bool> WaitForFeederAsync(ScanOptions options, CancellationToken cancellationToken) =>
        Task.FromResult(options.Source != PaperSource.Flatbed);
}
