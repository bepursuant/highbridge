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

    /// <summary>Scanner id from <c>/v1/scanners</c>; null means the default scanner.</summary>
    public string? ScannerId { get; init; }

    public int Dpi { get; init; } = 200;

    public ColorMode ColorMode { get; init; } = ColorMode.Color;

    public PaperSource Source { get; init; } = PaperSource.Auto;

    public bool Duplex { get; init; }

    /// <summary>
    /// One feeder sheet per job. With <see cref="Duplex"/>, that is the front and the back and
    /// nothing after them; further sheets stay in the feeder for the next job. An empty feeder
    /// waits instead of failing. Off, the job takes the whole feeder, as before. A flatbed is
    /// always one shot.
    /// </summary>
    public bool OneSheet { get; init; }

    /// <summary>Show the vendor's TWAIN driver dialog instead of scanning with these settings.</summary>
    public bool ShowDriverUi { get; init; }

    /// <summary>0 means no limit.</summary>
    public int MaxPages { get; init; }

    public OutputFormat Format { get; init; } = OutputFormat.Png;

    /// <summary>Add a provenance band to each page; null means no stamp (unless policy requires one).</summary>
    public StampOptions? Stamp { get; init; }

    /// <summary>Find barcodes on every page; see <see cref="Highbridge.Barcodes.BarcodeDetector"/>.</summary>
    public bool Barcodes { get; init; }

    /// <summary>Barcode formats to look for; null means <see cref="BarcodeFormatSet.Default"/>. Needs <see cref="Barcodes"/>.</summary>
    [JsonConverter(typeof(BarcodeFormatListConverter))]
    public BarcodeFormat? BarcodeFormats { get; init; }

    /// <summary>Returns a list of validation problems; empty when the options are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Dpi is < MinDpi or > MaxDpi)
        {
            errors.Add($"dpi must be between {MinDpi} and {MaxDpi}.");
        }

        if (MaxPages is < 0 or > MaxPageLimit)
        {
            errors.Add($"maxPages must be between 0 and {MaxPageLimit}.");
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
    /// How many images this job may take. Null means no limit. <see cref="OneSheet"/> caps a
    /// feeder scan at one sheet (two images when duplex, otherwise one). <see cref="MaxPages"/>,
    /// when set, can only make that cap smaller.
    /// </summary>
    public int? EffectivePageLimit()
    {
        int? sheet = OneSheet ? Duplex ? 2 : 1 : null;
        if (MaxPages <= 0)
        {
            return sheet;
        }

        return sheet is int cap ? Math.Min(MaxPages, cap) : MaxPages;
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
    /// When <paramref name="options"/> asks for one sheet from the feeder, blocks until
    /// CAP_FEEDERLOADED reports paper or the token is canceled. Returns false when this scan
    /// does not use the feeder, and does not wait. An empty feeder does not throw.
    /// </summary>
    Task<bool> WaitForFeederAsync(ScanOptions options, CancellationToken cancellationToken) =>
        Task.FromResult(options.Source != PaperSource.Flatbed);
}
