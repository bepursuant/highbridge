using Highbridge.Imaging;

namespace Highbridge.Scanning;

public sealed class MockScannerOptions
{
    /// <summary>Sheets in the simulated document feeder.</summary>
    public int FeederSheets { get; init; } = 3;

    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Mock pages are rendered at no more than this resolution to keep them small.</summary>
    public int MaxRenderDpi { get; init; } = 100;
}

/// <summary>
/// Fake scanner for development, CI and demos. Produces synthetic A4 PNG pages and exposes a
/// "jammed" device to exercise error paths.
/// </summary>
public sealed class MockScannerBackend : IScannerBackend
{
    public const string FlatbedId = "mock-flatbed";
    public const string FeederId = "mock-feeder";
    public const string JammedId = "mock-jammed";

    /// <summary>A feeder whose pages are <see cref="Barcodes.MockBarcodeSheet"/> test sheets with rotated barcodes.</summary>
    public const string BarcodesId = "mock-barcodes";

    /// <summary>Barcode sheets are rendered at the requested resolution, up to this one.</summary>
    public const int MaxBarcodeSheetDpi = 300;

    private static readonly IReadOnlyList<ScannerInfo> Scanners =
    [
        new(FeederId, "highbridge mock feeder (duplex)", "highbridge", "mock", IsDefault: true),
        new(FlatbedId, "highbridge mock flatbed", "highbridge", "mock", IsDefault: false),
        new(JammedId, "highbridge mock jammed scanner", "highbridge", "mock", IsDefault: false),
        new(BarcodesId, "highbridge mock feeder with barcode test sheets", "highbridge", "mock", IsDefault: false),
    ];

    private readonly MockScannerOptions _options;
    private readonly object _sheets = new();
    private int _feederLeft;
    private int _barcodeLeft;

    public MockScannerBackend(MockScannerOptions? options = null)
    {
        _options = options ?? new MockScannerOptions();
        _feederLeft = _options.FeederSheets;
        _barcodeLeft = _options.FeederSheets;
    }

    /// <summary>Refills the simulated feeder. A one-sheet scan waiting on an empty feeder then continues.</summary>
    public void LoadSheets(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_sheets)
        {
            _feederLeft = count;
            _barcodeLeft = count;
        }
    }

    public string Name => "mock";

    public Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Scanners);

    public async Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken)
    {
        string scannerId = options.ScannerId ?? FeederId;
        if (!Scanners.Any(s => s.Id == scannerId))
        {
            throw new ScannerException("scanner_not_found", $"Scanner '{scannerId}' was not found.");
        }

        if (scannerId == JammedId)
        {
            await Task.Delay(_options.PageDelay, cancellationToken).ConfigureAwait(false);
            throw new ScannerException("paper_jam", "The scanner reported a paper jam.");
        }

        bool useFeeder = UsesFeeder(options);
        bool duplex = useFeeder && options.Duplex;
        // One sheet consumes a sheet so the next job is the next sheet. Off, every scan still
        // sees the configured stack, which is what the older tests and the one-shot button do.
        int sheets = useFeeder ? (options.OneSheet ? TakeOneSheet(scannerId) : _options.FeederSheets) : 0;
        int pageCount = useFeeder ? sheets * (duplex ? 2 : 1) : 1;
        if (options.EffectivePageLimit() is int limit)
        {
            pageCount = Math.Min(pageCount, limit);
        }

        var pages = new List<ScannedPage>(pageCount);
        for (int i = 0; i < pageCount; i++)
        {
            await Task.Delay(_options.PageDelay, cancellationToken).ConfigureAwait(false);
            var page = scannerId == BarcodesId
                ? Encode(i, Barcodes.MockBarcodeSheet.Render(i, pageCount, Math.Min(options.Dpi, MaxBarcodeSheetDpi)), options, Math.Min(options.Dpi, MaxBarcodeSheetDpi))
                : RenderPage(i, pageCount, options, useFeeder, duplex);
            pages.Add(page);
            onPage?.Invoke(page);
        }

        return pages;
    }

    public async Task<bool> WaitForFeederAsync(ScanOptions options, CancellationToken cancellationToken)
    {
        if (!UsesFeeder(options))
        {
            return false;
        }

        if (!options.OneSheet)
        {
            return true;
        }

        string scannerId = options.ScannerId ?? FeederId;
        while (SheetsLeft(scannerId) <= 0)
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private bool UsesFeeder(ScanOptions options)
    {
        string scannerId = options.ScannerId ?? FeederId;
        return options.Source == PaperSource.Feeder
            || (options.Source == PaperSource.Auto && scannerId is FeederId or BarcodesId);
    }

    private int SheetsLeft(string scannerId)
    {
        lock (_sheets)
        {
            return scannerId == BarcodesId ? _barcodeLeft : _feederLeft;
        }
    }

    private int TakeOneSheet(string scannerId)
    {
        lock (_sheets)
        {
            if (scannerId == BarcodesId)
            {
                if (_barcodeLeft <= 0)
                {
                    return 0;
                }

                _barcodeLeft--;
                return 1;
            }

            if (_feederLeft <= 0)
            {
                return 0;
            }

            _feederLeft--;
            return 1;
        }
    }

    private ScannedPage RenderPage(int index, int total, ScanOptions options, bool feeder, bool duplex)
    {
        int dpi = Math.Min(options.Dpi, _options.MaxRenderDpi);
        int width = (int)Math.Round(8.27 * dpi);
        int height = (int)Math.Round(11.69 * dpi);
        int unit = Math.Max(1, dpi / 50);
        int margin = dpi / 2;

        var canvas = new Canvas(width, height);
        (byte, byte, byte) ink = (40, 44, 52);
        (byte, byte, byte) accent = (37, 99, 235);
        (byte, byte, byte) faint = (203, 213, 225);

        canvas.StrokeRect(margin / 2, margin / 2, width - margin, height - margin, unit, faint);
        canvas.FillRect(margin, margin, width - (2 * margin), dpi / 2, accent);
        canvas.DrawText("HIGHBRIDGE MOCK SCAN", margin + unit * 6, margin + (dpi / 4) - (unit * 7), unit * 2, (255, 255, 255));

        string side = duplex ? (index % 2 == 0 ? " FRONT" : " BACK") : string.Empty;
        string title = $"PAGE {index + 1} OF {total}{side}";
        int titleScale = Math.Max(1, Math.Min(unit * 5, (width - (2 * margin)) / Canvas.MeasureText(title, 1)));
        canvas.DrawText(title, (width - Canvas.MeasureText(title, titleScale)) / 2, margin + dpi, titleScale, ink);

        int lineTop = margin + (dpi * 2);
        int lineHeight = Math.Max(4, dpi / 12);
        var random = new Random(index + 1);
        for (int y = lineTop; y < height - (margin * 3); y += lineHeight * 3)
        {
            int lineWidth = (int)((width - (2 * margin)) * (0.55 + (random.NextDouble() * 0.45)));
            canvas.FillRect(margin, y, lineWidth, lineHeight, faint);
        }

        string mode = options.ColorMode switch
        {
            ColorMode.Gray => "GRAY",
            ColorMode.BlackWhite => "BW",
            _ => "COLOR",
        };
        string footer = $"{options.Dpi} DPI  {mode}  {(feeder ? "FEEDER" : "FLATBED")}";
        canvas.DrawText(footer, margin, height - (margin * 2), unit * 2, ink);
        return Encode(index, canvas, options, dpi);
    }

    private static ScannedPage Encode(int index, Canvas canvas, ScanOptions options, int dpi)
    {
        bool grayscale = options.ColorMode != ColorMode.Color;
        bool threshold = options.ColorMode == ColorMode.BlackWhite;
        byte[] data = options.Format switch
        {
            OutputFormat.Png => canvas.ToPng(grayscale, threshold, dpi),
            _ => throw new ScannerException("unsupported_format", $"The mock scanner cannot produce {options.Format}."),
        };
        string scannerId = options.ScannerId ?? FeederId;
        return new ScannedPage(index, OutputFormats.MediaType(options.Format), canvas.Width, canvas.Height, data)
        {
            ScannerName = Scanners.First(s => s.Id == scannerId).Name,
        };
    }
}
