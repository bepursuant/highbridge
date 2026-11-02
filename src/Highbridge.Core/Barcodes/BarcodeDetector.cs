using System.Diagnostics;
using Highbridge.Imaging;
using ZXing;
using ZXing.Common;

namespace Highbridge.Barcodes;

public enum BarcodeDetectionOutcome
{
    /// <summary>Every rotation was searched.</summary>
    Complete,

    /// <summary>The time budget ran out before every rotation was searched.</summary>
    Partial,
}

public sealed record BarcodeDetection(IReadOnlyList<BarcodeResult> Barcodes, BarcodeDetectionOutcome Outcome, TimeSpan Elapsed, int Passes);

/// <summary>
/// Finds barcodes on one page with ZXing.Net, in ZXing's "try harder" mode (denser sampling, which
/// also tolerates moderate skew). The page is searched turned 0, 90, 180 and 270 degrees:
/// <list type="bullet">
/// <item>0 degrees: every requested format, plus inverted (light-on-dark) codes. QR, Data Matrix and
/// Aztec are found at any angle in this pass. PDF417 gets its own reader on the whole page, which
/// tries the four rotations internally.</item>
/// <item>90, 180, 270 degrees: the 1D formats, which ZXing reads only along pixel rows.</item>
/// </list>
/// Results are mapped back to page coordinates and de-duplicated. Thread-safe; calls are independent.
/// </summary>
public static class BarcodeDetector
{
    public static readonly int[] Rotations = [0, 90, 180, 270];

    /// <summary>Formats ZXing finds at any angle without turning the page.</summary>
    public const BarcodeFormat AnyAngle = BarcodeFormat.QrCode | BarcodeFormat.DataMatrix | BarcodeFormat.Aztec | BarcodeFormat.Pdf417;

    /// <summary>
    /// Larger pages (beyond 300 dpi on A4, Letter or Legal) are shrunk before searching, which bounds
    /// time and memory; barcodes at that resolution have modules several pixels wide anyway.
    /// </summary>
    public const long MaxPixels = 12_000_000;

    public static BarcodeDetection Detect(LuminanceImage page, int pageIndex, BarcodeFormat formats, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        int scale = 1;
        while ((long)page.Width * page.Height / ((long)scale * scale) > MaxPixels)
        {
            scale++;
        }

        var image = page.Downscale(scale);
        var found = new List<BarcodeResult>();
        int passes = 0;
        var outcome = BarcodeDetectionOutcome.Complete;
        foreach (int rotation in Rotations)
        {
            var passFormats = rotation == 0 ? formats & ~(BarcodeFormat.Pdf417 | CenterSeeking) : formats & ~AnyAngle;
            bool pdf417 = rotation == 0 && (formats & BarcodeFormat.Pdf417) != 0;
            var tiled = rotation == 0 ? formats & CenterSeeking : BarcodeFormat.None;
            if (passFormats == BarcodeFormat.None && !pdf417 && tiled == BarcodeFormat.None)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // A pass costs about as much as the previous one; don't start one that would overrun.
            if (passes > 0 && clock.Elapsed + (clock.Elapsed / passes) > budget)
            {
                outcome = BarcodeDetectionOutcome.Partial;
                break;
            }

            var rotated = image.RotateClockwise(rotation);
            var source = new GrayLuminanceSource(rotated);
            var results = new List<Result>();
            if (pdf417)
            {
                results.AddRange(DecodePdf417(source));
            }

            if (tiled != BarcodeFormat.None)
            {
                results.AddRange(DecodeTiles(source, tiled));
            }

            if (passFormats != BarcodeFormat.None)
            {
                var reader = CreateReader(passFormats, tryInverted: rotation == 0);
                results.AddRange(reader.DecodeMultiple(source) ?? []);
            }

            passes++;
            foreach (var result in results)
            {
                // ZXing's EAN reader reports a UPC-A it happens to see even when it was not requested.
                if (ToBarcode(result, image, rotation, pageIndex, scale) is { } barcode
                    && (formats & barcode.Format) != 0
                    && !IsDuplicate(found, barcode))
                {
                    found.Add(barcode);
                }
            }
        }

        found.RemoveAll(b => IsInsideLongerLinearCode(b, found));
        return new BarcodeDetection(found, outcome, clock.Elapsed, passes);
    }

    /// <summary>
    /// ZXing's Data Matrix and Aztec detectors grow a white rectangle out from the middle of the
    /// image, so they only find a symbol that covers the image's center.
    /// </summary>
    private const BarcodeFormat CenterSeeking = BarcodeFormat.DataMatrix | BarcodeFormat.Aztec;

    /// <summary>Upper bound on regions tried per page, so a busy page can't take long.</summary>
    private const int MaxCandidateRegions = 48;

    /// <summary>
    /// Finds compact blocks of medium ink density (Data Matrix and Aztec are about half black) and
    /// runs the reader on a crop centered on each, which is where ZXing's detector starts.
    /// </summary>
    private static IEnumerable<Result> DecodeTiles(GrayLuminanceSource source, BarcodeFormat formats)
    {
        var reader = CreateReader(formats, tryInverted: false);
        foreach (var (left, top, width, height) in CandidateRegions(new HybridBinarizer(source).BlackMatrix))
        {
            if (reader.Decode(source.crop(left, top, width, height)) is { } result)
            {
                var points = result.ResultPoints.Where(p => p is not null).Select(p => new ResultPoint(p.X + left, p.Y + top)).ToArray();
                yield return new Result(result.Text, result.RawBytes, points, result.BarcodeFormat);
            }
        }
    }

    private static List<(int Left, int Top, int Width, int Height)> CandidateRegions(BitMatrix black)
    {
        int w = black.Width, h = black.Height;
        // About 0.6 mm on a letter/A4 page at any resolution: a few modules of a typical symbol.
        int cell = Math.Max(3, Math.Min(w, h) / 350);
        int cw = w / cell, ch = h / cell;
        var inky = new bool[cw * ch];
        int area = cell * cell;
        for (int cy = 0; cy < ch; cy++)
        {
            for (int cx = 0; cx < cw; cx++)
            {
                int count = 0;
                for (int y = cy * cell; y < (cy + 1) * cell; y++)
                {
                    for (int x = cx * cell; x < (cx + 1) * cell; x++)
                    {
                        if (black[x, y])
                        {
                            count++;
                        }
                    }
                }

                inky[(cy * cw) + cx] = count >= area / 5 && count <= area * 4 / 5;
            }
        }

        var regions = new List<(int Left, int Top, int Width, int Height, int Cells)>();
        var seen = new bool[inky.Length];
        var queue = new Queue<int>();
        int maxSide = Math.Min(cw, ch) / 3;
        for (int start = 0; start < inky.Length; start++)
        {
            if (!inky[start] || seen[start])
            {
                continue;
            }

            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0, cells = 0;
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.TryDequeue(out int i))
            {
                int x = i % cw, y = i / cw;
                (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
                cells++;
                // 8-connected with a one-cell gap, so the light modules inside a symbol don't split it.
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < cw && ny < ch && inky[(ny * cw) + nx] && !seen[(ny * cw) + nx])
                        {
                            seen[(ny * cw) + nx] = true;
                            queue.Enqueue((ny * cw) + nx);
                        }
                    }
                }
            }

            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            bool compact = bw >= 3 && bh >= 3 && bw <= maxSide && bh <= maxSide && Math.Max(bw, bh) <= 4 * Math.Min(bw, bh) && cells * 3 >= bw * bh;
            if (compact)
            {
                regions.Add((x0, y0, bw, bh, cells));
            }
        }

        return regions
            .OrderByDescending(r => r.Cells)
            .Take(MaxCandidateRegions)
            .Select(r =>
            {
                // Centered crop with room for the quiet zone and the detector's own search.
                int size = Math.Max(r.Width, r.Height) * cell * 2;
                int centerX = ((2 * r.Left) + r.Width) * cell / 2, centerY = ((2 * r.Top) + r.Height) * cell / 2;
                int left = Math.Clamp(centerX - (size / 2), 0, w - 1), top = Math.Clamp(centerY - (size / 2), 0, h - 1);
                return (left, top, Math.Min(size, w - left), Math.Min(size, h - top));
            })
            .ToList();
    }

    /// <summary>
    /// A UPC-E or EAN-8 read (short codes with a weak check) lying along a longer 1D code is a
    /// misread of part of that code; ZXing produces these on skewed Code 128 in "try harder" mode.
    /// </summary>
    private static bool IsInsideLongerLinearCode(BarcodeResult candidate, List<BarcodeResult> all)
    {
        if (candidate.Points.Count != 2 || candidate.Format is not (BarcodeFormat.UpcE or BarcodeFormat.Ean8))
        {
            return false;
        }

        var (a0, a1) = (candidate.Points[0], candidate.Points[1]);
        double candidateLength = Distance(a0, a1);
        foreach (var other in all)
        {
            if (ReferenceEquals(other, candidate) || other.Points.Count != 2 || other.Format == candidate.Format)
            {
                continue;
            }

            var (b0, b1) = (other.Points[0], other.Points[1]);
            double length = Distance(b0, b1);
            if (length <= candidateLength)
            {
                continue;
            }

            // Position of the candidate's ends along, and distance from, the longer code's axis.
            double ux = (b1.X - b0.X) / length, uy = (b1.Y - b0.Y) / length;
            bool inside = true;
            foreach (var p in new[] { a0, a1 })
            {
                double along = ((p.X - b0.X) * ux) + ((p.Y - b0.Y) * uy);
                double across = Math.Abs(((p.X - b0.X) * -uy) + ((p.Y - b0.Y) * ux));
                inside &= along >= -0.05 * length && along <= 1.05 * length && across <= 0.2 * length;
            }

            if (inside)
            {
                return true;
            }
        }

        return false;
    }

    private static double Distance(BarcodePoint a, BarcodePoint b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>
    /// ZXing's PDF417 detector turns the image itself (0, 180, 270, 90) and reports points in the
    /// turned image, with the turn in the ORIENTATION metadata. Run on the whole page (not inside
    /// the multi-barcode reader's crops) so the points can be turned back exactly.
    /// </summary>
    private static IEnumerable<Result> DecodePdf417(GrayLuminanceSource source)
    {
        var hints = new Dictionary<DecodeHintType, object> { [DecodeHintType.TRY_HARDER] = true };
        var results = new ZXing.PDF417.PDF417Reader().decodeMultiple(new BinaryBitmap(new HybridBinarizer(source)), hints) ?? [];
        int w = source.Width, h = source.Height;
        foreach (var result in results)
        {
            int turn = result.ResultMetadata?.TryGetValue(ResultMetadataType.ORIENTATION, out var o) == true && o is int i ? i : 0;
            var points = result.ResultPoints.Where(p => p is not null).Select(p => turn switch
            {
                90 => new ResultPoint(w - 1 - p.Y, p.X),
                180 => new ResultPoint(w - 1 - p.X, h - 1 - p.Y),
                270 => new ResultPoint(p.Y, h - 1 - p.X),
                _ => p,
            }).ToArray();
            yield return new Result(result.Text, result.RawBytes, points, result.BarcodeFormat);
        }
    }

    private static BarcodeReaderGeneric CreateReader(BarcodeFormat formats, bool tryInverted) => new()
    {
        // highbridge turns the page itself; ZXing's own 90-degree retry would repeat that work.
        AutoRotate = false,
        Options = new DecodingOptions
        {
            TryHarder = true,
            TryInverted = tryInverted,
            PossibleFormats = BarcodeFormatSet.ToZXing(formats),
        },
    };

    private static BarcodeResult? ToBarcode(Result result, LuminanceImage image, int rotation, int pageIndex, int scale)
    {
        var format = BarcodeFormatSet.FromZXing(result.BarcodeFormat);
        if (format == BarcodeFormat.None || string.IsNullOrEmpty(result.Text))
        {
            return null;
        }

        float offset = (scale - 1) / 2f;
        var points = (result.ResultPoints ?? [])
            .Where(p => p is not null)
            .Select(p => image.FromRotated(rotation, p.X, p.Y))
            .Select(p => new BarcodePoint(MathF.Round((p.X * scale) + offset, 1), MathF.Round((p.Y * scale) + offset, 1)))
            .ToArray();
        return new BarcodeResult(pageIndex, format, result.Text, rotation, Orientation(format, points), points);
    }

    /// <summary>Reading direction from the points ZXing reports, which differ per format.</summary>
    private static int? Orientation(BarcodeFormat format, BarcodePoint[] p)
    {
        (BarcodePoint From, BarcodePoint To)? axis = format switch
        {
            // 1D: start and end of the scan line, in reading order.
            _ when p.Length == 2 => (p[0], p[1]),
            // Finder patterns: bottom-left, top-left, top-right.
            BarcodeFormat.QrCode when p.Length >= 3 => (p[1], p[2]),
            // Corners: top-left, bottom-left, top-right, bottom-right (+ inner corners).
            BarcodeFormat.Pdf417 when p.Length >= 4 => (p[0], p[2]),
            // Corners: top-left, bottom-left, bottom-right, top-right of the L-shaped finder.
            BarcodeFormat.DataMatrix when p.Length == 4 => (p[1], p[2]),
            _ => null,
        };
        if (axis is not { } a || (a.From.X == a.To.X && a.From.Y == a.To.Y))
        {
            return null;
        }

        double degrees = Math.Atan2(a.To.Y - a.From.Y, a.To.X - a.From.X) * 180 / Math.PI;
        return ((int)Math.Round(degrees) + 360) % 360;
    }

    /// <summary>Same format and text, and centers closer than the barcode's own size: the same physical code.</summary>
    private static bool IsDuplicate(List<BarcodeResult> found, BarcodeResult candidate)
    {
        var (cx, cy, size) = Extent(candidate.Points);
        foreach (var other in found)
        {
            if (other.Format != candidate.Format || other.Text != candidate.Text)
            {
                continue;
            }

            var (ox, oy, otherSize) = Extent(other.Points);
            double limit = Math.Max(Math.Max(size, otherSize), 1);
            if (candidate.Points.Count == 0 || other.Points.Count == 0 || Math.Abs(cx - ox) + Math.Abs(cy - oy) <= limit)
            {
                return true;
            }
        }

        return false;
    }

    private static (double X, double Y, double Size) Extent(IReadOnlyList<BarcodePoint> points)
    {
        if (points.Count == 0)
        {
            return (0, 0, 0);
        }

        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        return ((minX + maxX) / 2, (minY + maxY) / 2, Math.Max(maxX - minX, maxY - minY));
    }

    /// <summary>
    /// Hands (a region of) the luminance buffer to ZXing without copying the whole page. Rotation is
    /// done by highbridge, so ZXing's own rotation support stays off.
    /// </summary>
    private sealed class GrayLuminanceSource : LuminanceSource
    {
        private readonly LuminanceImage _image;
        private readonly int _left;
        private readonly int _top;

        public GrayLuminanceSource(LuminanceImage image)
            : this(image, 0, 0, image.Width, image.Height)
        {
        }

        private GrayLuminanceSource(LuminanceImage image, int left, int top, int width, int height)
            : base(width, height)
        {
            _image = image;
            _left = left;
            _top = top;
        }

        public override bool CropSupported => true;

        public override byte[] Matrix
        {
            get
            {
                if (Width == _image.Width && Height == _image.Height)
                {
                    return _image.Pixels;
                }

                var matrix = new byte[Width * Height];
                for (int y = 0; y < Height; y++)
                {
                    Array.Copy(_image.Pixels, ((_top + y) * _image.Width) + _left, matrix, y * Width, Width);
                }

                return matrix;
            }
        }

        public override byte[] getRow(int y, byte[] row)
        {
            if (row is null || row.Length < Width)
            {
                row = new byte[Width];
            }

            Array.Copy(_image.Pixels, ((_top + y) * _image.Width) + _left, row, 0, Width);
            return row;
        }

        public override LuminanceSource crop(int left, int top, int width, int height) =>
            new GrayLuminanceSource(_image, _left + left, _top + top, width, height);
    }
}
