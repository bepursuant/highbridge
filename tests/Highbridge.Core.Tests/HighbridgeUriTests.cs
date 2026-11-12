using Highbridge.Barcodes;
using Highbridge.Protocol;
using Highbridge.Scanning;

namespace Highbridge.Core.Tests;

public class HighbridgeUriTests
{
    private const string RequestId = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("highbridge://launch")]
    [InlineData("highbridge://launch/")]
    [InlineData("highbridge:launch")]
    [InlineData("HIGHBRIDGE://")]
    public void Parses_launch(string url)
    {
        Assert.True(HighbridgeUri.TryParse(url, out var command, out _));
        Assert.Equal(ProtocolAction.Launch, command!.Action);
    }

    [Fact]
    public void Parses_scan_with_all_options()
    {
        string url = $"highbridge://scan/?v=1&origin=https%3A%2F%2Fapp.example.com&request={RequestId}&scannerId=My%20Scanner&dpi=300&colorMode=gray&source=feeder&duplex=true&showDriverUi=1&maxPages=5&format=PNG";

        Assert.True(HighbridgeUri.TryParse(url, out var command, out string? error), error);
        Assert.Equal(ProtocolAction.Scan, command!.Action);
        Assert.Equal("https://app.example.com", command.Origin);
        Assert.Equal(RequestId, command.RequestId);
        Assert.Equal(
            new ScanOptions { ScannerId = "My Scanner", Dpi = 300, ColorMode = ColorMode.Gray, Source = PaperSource.Feeder, Duplex = true, ShowDriverUi = true, MaxPages = 5, Format = OutputFormat.Png },
            command.Options);
    }

    [Fact]
    public void Build_and_parse_round_trip()
    {
        var options = new ScanOptions { ScannerId = "Fujitsu fi-7160 & co", Dpi = 600, ColorMode = ColorMode.BlackWhite, Source = PaperSource.Flatbed, Duplex = false, MaxPages = 2 };
        string url = HighbridgeUri.BuildScan("https://app.example.com", RequestId, options);

        Assert.True(HighbridgeUri.TryParse(url, out var command, out string? error), error);
        Assert.Equal(options, command!.Options);
        Assert.Equal("https://app.example.com", command.Origin);
    }

    [Fact]
    public void Pdf_format_round_trips_and_png_stays_the_default()
    {
        var options = new ScanOptions { Source = PaperSource.Feeder, Duplex = true, OneSheet = true, Format = OutputFormat.Pdf };
        string url = HighbridgeUri.BuildScan("https://app.example.com", RequestId, options);

        Assert.Contains("format=pdf", url, StringComparison.Ordinal);
        Assert.True(HighbridgeUri.TryParse(url, out var command, out string? error), error);
        Assert.Equal(options, command!.Options);
        Assert.Equal(OutputFormat.Png, new ScanOptions().Format);
        Assert.True(OutputFormats.TryParse("PDF", out var parsed));
        Assert.Equal(OutputFormat.Pdf, parsed);
    }

    [Fact]
    public void One_sheet_round_trips_and_stays_off_when_omitted()
    {
        var options = new ScanOptions { Source = PaperSource.Feeder, Duplex = true, OneSheet = true };
        string url = HighbridgeUri.BuildScan("https://app.example.com", RequestId, options);

        Assert.Contains("oneSheet=true", url, StringComparison.Ordinal);
        Assert.True(HighbridgeUri.TryParse(url, out var command, out string? error), error);
        Assert.Equal(options, command!.Options);
        Assert.False(new ScanOptions().OneSheet);
    }

    [Fact]
    public void Barcode_options_round_trip_and_bad_ones_are_rejected()
    {
        var options = new ScanOptions { Barcodes = true, BarcodeFormats = BarcodeFormat.QrCode | BarcodeFormat.Code128 | BarcodeFormat.Ean13 };
        string url = HighbridgeUri.BuildScan("https://app.example.com", RequestId, options);

        Assert.Contains("barcodes=true", url, StringComparison.Ordinal);
        Assert.Contains("barcodeFormats=qrCode%2Ccode128%2Cean13", url, StringComparison.Ordinal);
        Assert.True(HighbridgeUri.TryParse(url, out var command, out string? error), error);
        Assert.Equal(options, command!.Options);

        string bare = $"highbridge://scan?origin=https%3A%2F%2Fapp.example.com&request={RequestId}&barcodeFormats=qrCode";
        Assert.False(HighbridgeUri.TryParse(bare, out _, out error));
        Assert.Contains("barcodes", error);

        string unknown = $"highbridge://scan?origin=https%3A%2F%2Fapp.example.com&request={RequestId}&barcodes=true&barcodeFormats=qrCode,upc";
        Assert.False(HighbridgeUri.TryParse(unknown, out _, out error));
        Assert.Contains("barcodeFormats", error);
    }

    [Fact]
    public void First_duplicate_parameter_wins()
    {
        string url = $"highbridge://scan?origin=https%3A%2F%2Fa.example&origin=https%3A%2F%2Fevil.example&request={RequestId}";
        Assert.True(HighbridgeUri.TryParse(url, out var command, out _));
        Assert.Equal("https://a.example", command!.Origin);
    }

    [Theory]
    [InlineData("https://example.com", "not a highbridge")]
    [InlineData("highbridge://format-disk", "unknown action")]
    [InlineData("highbridge://scan?v=2&origin=https%3A%2F%2Fa.example&request=0123456789abcdef", "unsupported protocol version")]
    [InlineData("highbridge://scan?request=0123456789abcdef", "missing 'origin'")]
    [InlineData("highbridge://scan?origin=https%3A%2F%2Fa.example", "missing 'request'")]
    [InlineData("highbridge://scan?origin=https%3A%2F%2Fa.example&request=0123456789abcdef&dpi=-1", "dpi must be a whole number")]
    [InlineData("highbridge://scan?origin=https%3A%2F%2Fa.example&request=0123456789abcdef&dpi=5000", "dpi must be between")]
    [InlineData("highbridge://scan?origin=https%3A%2F%2Fa.example&request=0123456789abcdef&colorMode=sepia", "unknown colorMode")]
    [InlineData("highbridge://scan?origin=https%3A%2F%2Fa.example&request=0123456789abcdef&source=1", "unknown source")]
    [InlineData("highbridge://scan?origin=https%3A%2F%2Fa.example&request=0123456789abcdef&duplex=maybe", "duplex must be true or false")]
    [InlineData("highbridge://scan?origin=https%3A%2F%2Fa.example&request=0123456789abcdef&oneSheet=maybe", "oneSheet must be true or false")]
    [InlineData("highbridge://scan?origin=https%3A%2F%2Fa.example&request=0123456789abcdef&format=tiff", "unknown format 'tiff'")]
    public void Rejects_invalid_urls(string url, string expectedError)
    {
        Assert.False(HighbridgeUri.TryParse(url, out _, out string? error));
        Assert.Contains(expectedError, error);
    }

    [Fact]
    public void Rejects_overlong_urls()
    {
        Assert.False(HighbridgeUri.TryParse("highbridge://scan?x=" + new string('a', 3000), out _, out _));
    }
}
