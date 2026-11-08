using Highbridge.Cards;
using Highbridge.Scanning;
using Highbridge.Support;

namespace Highbridge.Core.Tests;

public class SupportReportTests
{
    [Fact]
    public void Redaction_hides_a_card_number_and_long_hex_and_keeps_an_aid()
    {
        string line = LogRedactor.Redact(
            "reader ACS PICC aid A0000000031010 pcsc 80100016 pan 4111 1111 1111 1234 track 4111111111111234D2512 blob " + new string('A', 40));

        Assert.DoesNotContain("4111", line, StringComparison.Ordinal);
        Assert.Contains("[redacted-number]", line, StringComparison.Ordinal);
        Assert.Contains("[redacted-hex]", line, StringComparison.Ordinal);
        Assert.Contains("A0000000031010", line, StringComparison.Ordinal);
        Assert.Contains("80100016", line, StringComparison.Ordinal);
        Assert.Contains("ACS PICC", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_keeps_a_job_id_a_port_and_a_timestamp()
    {
        const string line = "2026-11-07 18:23:01.123 -06:00 port 47116 job 0123456789abcdef0123456789abcdef wait 30000";

        Assert.Equal(line, LogRedactor.Redact(line));
    }

    [Fact]
    public async Task Inventory_lists_both_devices_and_names_the_slot()
    {
        var snapshot = await DeviceInventory.CollectAsync(
            new MockScannerBackend(),
            new NamedReaders("pcsc", "ACS ACR1281 1S Dual Reader PICC 0", "ACS ACR1281 1S Dual Reader ICC 0"),
            version: "0.1.0",
            port: 47116,
            cardReaderEnabled: true,
            timeout: TimeSpan.FromSeconds(2));

        string report = SupportReport.Format(snapshot);

        Assert.Contains("highbridge mock feeder (duplex)", report, StringComparison.Ordinal);
        Assert.Contains("Contactless", report, StringComparison.Ordinal);
        Assert.Contains("Contact", report, StringComparison.Ordinal);
        Assert.Contains("port 47116", report, StringComparison.Ordinal);
        Assert.Contains("enabled", report, StringComparison.Ordinal);
        Assert.Null(snapshot.ScannerError);
        Assert.Null(snapshot.ReaderError);
    }

    [Fact]
    public async Task A_hung_scanner_list_is_reported_without_dropping_the_readers()
    {
        var snapshot = await DeviceInventory.CollectAsync(
            new HungScanners(),
            new NamedReaders("pcsc", "Reader PICC 0"),
            version: "0.1.0",
            port: 47116,
            cardReaderEnabled: false,
            timeout: TimeSpan.FromMilliseconds(80));

        string report = SupportReport.Format(snapshot);

        Assert.Contains("The scanner list did not finish in time.", report, StringComparison.Ordinal);
        Assert.Contains("Reader PICC 0", report, StringComparison.Ordinal);
        Assert.Contains("disabled by policy", report, StringComparison.Ordinal);
        Assert.Empty(snapshot.Scanners);
    }

    [Fact]
    public async Task Startup_can_list_readers_without_opening_the_scanner()
    {
        var scanners = new HungScanners();
        var snapshot = await DeviceInventory.CollectAsync(
            scanners,
            new NamedReaders("pcsc", "Reader ICC 0"),
            version: "0.1.0",
            port: 47116,
            cardReaderEnabled: true,
            timeout: TimeSpan.FromMilliseconds(50),
            listScanners: false);

        string report = SupportReport.Format(snapshot);

        Assert.Contains("scanners (twain)  not listed", report, StringComparison.Ordinal);
        Assert.DoesNotContain("(none)", report.Split("card readers")[0], StringComparison.Ordinal);
        Assert.Contains("Reader ICC 0", report, StringComparison.Ordinal);
        Assert.Contains("Contact", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reader_error_cannot_carry_a_card_number_into_the_report()
    {
        var snapshot = await DeviceInventory.CollectAsync(
            new MockScannerBackend(),
            new FailingReaders("4111111111111234"),
            version: "0.1.0",
            port: 47116,
            cardReaderEnabled: true,
            timeout: TimeSpan.FromSeconds(2));

        string report = SupportReport.Format(snapshot);

        Assert.DoesNotContain("4111111111111234", report, StringComparison.Ordinal);
        Assert.Contains("[redacted-number]", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ACS ACR1281 1S Dual Reader ICC 0", "Contact")]
    [InlineData("ACS ACR1281 1S Dual Reader PICC 0", "Contactless")]
    [InlineData("ACS ACR1281 1S Dual Reader SAM 0", "SAM")]
    [InlineData("Identiv uTrust 4701 F CL Reader 0", "Contactless")]
    [InlineData("Generic Reader 0", "Other")]
    public void Slot_label_follows_the_reader_name(string name, string slot) =>
        Assert.Equal(slot, CardReaderSlot.Describe(name));

    private sealed class NamedReaders(string name, params string[] readers) : ICardReaderBackend
    {
        public string Name { get; } = name;

        public Task<IReadOnlyList<CardReaderInfo>> ListReadersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CardReaderInfo>>(readers.Select(reader => new CardReaderInfo(reader, reader, Name)).ToArray());

        public Task<CardReadResult> ReadAsync(CardReadOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FailingReaders(string message) : ICardReaderBackend
    {
        public string Name => "pcsc";

        public Task<IReadOnlyList<CardReaderInfo>> ListReadersAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);

        public Task<CardReadResult> ReadAsync(CardReadOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class HungScanners : IScannerBackend
    {
        public string Name => "twain";

        public async Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }

        public Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
