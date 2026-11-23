using System.Text.Json;
using System.Text.Json.Nodes;
using Highbridge.Cards;
using Highbridge.Configuration;
using Highbridge.Devices;
using Highbridge.Scanning;

namespace Highbridge.Core.Tests;

public class DeviceOptionsTests
{
    [Fact]
    public async Task An_omitted_scan_field_keeps_the_saved_default_and_a_sent_field_wins()
    {
        var settings = new SettingsStore(path: null);
        var saved = JsonSerializer.SerializeToNode(new ScanOptions
        {
            Dpi = 300,
            ColorMode = ColorMode.Gray,
            Duplex = true,
            MaxSheets = 1,
        }, HighbridgeJson.Options)!.AsObject();
        settings.SaveDeviceOptions(DeviceOptions.Scanner, MockScannerBackend.FeederId, saved);

        using var request = JsonDocument.Parse("""{"dpi":600,"duplex":false}""");
        var options = await DeviceOptions.ResolveScanAsync(settings, new MockScannerBackend(new MockScannerOptions()), request.RootElement, CancellationToken.None);

        Assert.Equal(600, options.Dpi);
        Assert.False(options.Duplex);
        Assert.Equal(ColorMode.Gray, options.ColorMode);
        Assert.Equal(1, options.MaxSheets);
        Assert.Equal(MockScannerBackend.FeederId, options.ScannerId);
    }

    [Fact]
    public async Task An_empty_card_request_uses_the_saved_reader_default()
    {
        var settings = new SettingsStore(path: null);
        settings.SaveDeviceOptions(DeviceOptions.Card, "mock-reader", new JsonObject
        {
            ["interface"] = "contactless",
            ["waitForCardMs"] = 5000,
        });

        var options = await DeviceOptions.ResolveCardAsync(settings, new FakeReaders(), default, CancellationToken.None);

        Assert.Equal("mock-reader", options.ReaderId);
        Assert.Equal(CardInterface.Contactless, options.Interface);
        Assert.Equal(5000, options.WaitForCardMs);
    }

    private sealed class FakeReaders : ICardReaderBackend
    {
        public string Name => "fake";

        public Task<IReadOnlyList<CardReaderInfo>> ListReadersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CardReaderInfo>>([new CardReaderInfo("mock-reader", "Mock", Name)]);

        public Task<CardReadResult> ReadAsync(CardReadOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
