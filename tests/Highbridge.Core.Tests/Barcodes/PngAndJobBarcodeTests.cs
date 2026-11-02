using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Highbridge.Barcodes;
using Highbridge.Imaging;
using Highbridge.Jobs;
using Highbridge.Scanning;

namespace Highbridge.Core.Tests.Barcodes;

public class PngDecoderTests
{
    [Fact]
    public void Round_trips_gray_and_color_images()
    {
        var gray = new byte[] { 0, 10, 250, 128, 255, 1 };
        var decoded = PngDecoder.DecodeLuminance(PngEncoder.Encode(3, 2, PngPixelFormat.Gray8, gray));
        Assert.Equal(gray, decoded.Pixels);

        byte[] rgb = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];
        var color = PngDecoder.DecodeLuminance(PngEncoder.Encode(2, 2, PngPixelFormat.Rgb24, rgb));
        Assert.Equal(new byte[] { 76, 149, 29, 255 }, color.Pixels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Reads_every_png_filter(int filter)
    {
        byte[] pixels = [10, 20, 30, 40, 50, 60, 70, 80];
        var png = FilteredPng(4, 2, pixels, filter);

        var decoded = PngDecoder.DecodeLuminance(png);

        Assert.Equal(pixels, decoded.Pixels);
    }

    [Fact]
    public void Rotation_maps_points_back()
    {
        var pixels = new byte[4 * 3];
        pixels[(1 * 4) + 2] = 99;
        var image = new LuminanceImage(4, 3, pixels);

        foreach (int degrees in new[] { 0, 90, 180, 270 })
        {
            var rotated = image.RotateClockwise(degrees);
            int index = Array.IndexOf(rotated.Pixels, (byte)99);
            var (x, y) = image.FromRotated(degrees, index % rotated.Width, index / rotated.Width);
            Assert.Equal((2, 1), ((int)x, (int)y));
        }
    }

    /// <summary>A gray PNG whose rows all use one filter, so each filter path is exercised.</summary>
    private static byte[] FilteredPng(int width, int height, byte[] pixels, int filter)
    {
        var raw = new byte[(width + 1) * height];
        for (int y = 0; y < height; y++)
        {
            raw[y * (width + 1)] = (byte)filter;
            for (int x = 0; x < width; x++)
            {
                byte value = pixels[(y * width) + x];
                byte left = x > 0 ? pixels[(y * width) + x - 1] : (byte)0;
                byte up = y > 0 ? pixels[((y - 1) * width) + x] : (byte)0;
                byte upLeft = x > 0 && y > 0 ? pixels[((y - 1) * width) + x - 1] : (byte)0;
                raw[(y * (width + 1)) + 1 + x] = filter switch
                {
                    0 => value,
                    1 => (byte)(value - left),
                    2 => (byte)(value - up),
                    3 => (byte)(value - ((left + up) >> 1)),
                    _ => (byte)(value - Paeth(left, up, upLeft)),
                };
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(length, PngEncoder.Crc32(typeBytes, data));
        output.Write(length);
    }
}

public class BarcodeJobTests
{
    private const string Origin = TestRuntime.Origin;

    [Fact]
    public async Task Requested_barcodes_come_back_per_page_and_as_one_list()
    {
        using var runtime = TestRuntime.Create(backend: new MockScannerBackend(new MockScannerOptions { PageDelay = TimeSpan.Zero, FeederSheets = 1 }));
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions
        {
            ScannerId = MockScannerBackend.BarcodesId,
            Dpi = 120,
            Barcodes = true,
            BarcodeFormats = BarcodeFormat.QrCode | BarcodeFormat.Code128,
            MaxPages = 1,
        }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ScanJobStatus.Completed, job.Status);
        var expected = MockBarcodeSheet.Contents(0).Where(c => c.Format is BarcodeFormat.QrCode or BarcodeFormat.Code128);
        Assert.Equal(expected.Select(c => (c.Format, c.Text)).Order(), job.Barcodes!.All.Select(b => (b.Format, b.Text)).Order());
        Assert.Equal(BarcodeStatus.Done, job.Barcodes.ForPage(0).Status);
        Assert.All(job.Barcodes.All, b => Assert.Equal(0, b.Page));
    }

    [Fact]
    public async Task Without_the_flag_no_barcode_work_is_done()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);
        var job = runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(job.Barcodes);
    }

    [Fact]
    public void Formats_without_the_flag_are_rejected()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);
        var ex = Assert.Throws<JobRejectedException>(() => runtime.Jobs.Start(
            Origin, new ScanOptions { BarcodeFormats = BarcodeFormat.QrCode }, ScanTrigger.Api));
        Assert.Equal("invalid_options", ex.Code);
    }

    [Fact]
    public async Task Detection_reads_the_page_as_delivered_and_does_not_block_the_scanner_callback()
    {
        var png = MockBarcodeSheet.RenderPng(0, 1, 120);
        BarcodeStatus? statusWhenCallbackReturned = null;
        var backend = new OnePageBackend(png, page =>
        {
            Array.Fill(page.Data, (byte)1);
        }, () =>
        {
            while (_job is null)
            {
                Thread.Sleep(1);
            }

            statusWhenCallbackReturned = _job.Barcodes!.ForPage(0).Status;
        });
        using var runtime = TestRuntime.Create(backend: backend);
        runtime.Origins.Pair(Origin);

        _job = runtime.Jobs.Start(Origin, new ScanOptions { Barcodes = true, Dpi = 120 }, ScanTrigger.Api);
        await _job.Finished.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(BarcodeStatus.Pending, statusWhenCallbackReturned);
        Assert.Contains(_job.Barcodes!.All, b => b.Text.Contains("highbridge.example", StringComparison.Ordinal));
    }

    private volatile ScanJob? _job;

    private sealed class OnePageBackend(byte[] png, Action<ScannedPage> afterCallback, Action observe) : IScannerBackend
    {
        public string Name => "probe";

        public Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScannerInfo>>([]);

        public Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken)
        {
            PngEncoder.TryReadSize(png, out int width, out int height);
            var page = new ScannedPage(0, "image/png", width, height, png.ToArray());
            onPage?.Invoke(page);
            afterCallback(page);
            observe();
            return Task.FromResult<IReadOnlyList<ScannedPage>>([page]);
        }
    }
}
