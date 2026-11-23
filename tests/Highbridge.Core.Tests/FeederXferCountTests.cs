using Highbridge.Scanning;

namespace Highbridge.Core.Tests;

public class FeederXferCountTests
{
    [Theory]
    [InlineData("PFU Limited", "PaperStream IP", "PaperStream IP fi-8170")]
    [InlineData("PFU", "fi Series", "fi-8170")]
    [InlineData("Fujitsu", "fi-7160", "Fujitsu fi-7160")]
    [InlineData("Ricoh", "PaperStream IP", "RICOH fi-8270")]
    public void Fi_series_drivers_count_sheets(string manufacturer, string family, string name)
    {
        Assert.True(FeederXferCount.CountsSheets(manufacturer, family, name));
    }

    [Theory]
    [InlineData("Ambir Technology", "nScan", "nScan 690gt")]
    [InlineData("Ambir", "nScan 690gt", "Ambir nScan 690gt")]
    [InlineData(null, null, "WIA-Ambir nScan 690gt")]
    public void The_Ambir_counts_images(string? manufacturer, string? family, string? name)
    {
        Assert.False(FeederXferCount.CountsSheets(manufacturer, family, name));
    }

    [Fact]
    public void Duplex_one_sheet_is_one_sheet_on_a_fi_series_feeder_and_two_images_on_the_Ambir()
    {
        var duplex = new ScanOptions { Source = PaperSource.Feeder, Duplex = true, MaxSheets = 1 };
        var front = new ScanOptions { Source = PaperSource.Feeder, MaxSheets = 1 };
        var whole = new ScanOptions { Source = PaperSource.Feeder, Duplex = true };

        Assert.Equal(1, FeederXferCount.Value(duplex, countsSheets: true));
        Assert.Equal(2, FeederXferCount.Value(duplex, countsSheets: false));
        Assert.Equal(1, FeederXferCount.Value(front, countsSheets: true));
        Assert.Equal(1, FeederXferCount.Value(front, countsSheets: false));
        Assert.Null(FeederXferCount.Value(whole, countsSheets: true));
        Assert.Null(FeederXferCount.Value(whole, countsSheets: false));
    }

    [Fact]
    public void Two_duplex_sheets_stay_two_on_a_sheet_counted_driver_and_become_four_images_on_the_Ambir()
    {
        var duplex = new ScanOptions { Source = PaperSource.Feeder, Duplex = true, MaxSheets = 2 };
        Assert.Equal(2, FeederXferCount.Value(duplex, countsSheets: true));
        Assert.Equal(4, FeederXferCount.Value(duplex, countsSheets: false));
        Assert.Null(FeederXferCount.Value(new ScanOptions { Source = PaperSource.Feeder, Duplex = true }, countsSheets: true));
        Assert.Null(FeederXferCount.Value(new ScanOptions { Source = PaperSource.Flatbed, MaxSheets = 2 }, countsSheets: false));
    }
}

public class WiaFeederTests
{
    [Theory]
    [InlineData("Samsung", "WIA-TigerType", "WIA-SCX-4600 Series")]
    [InlineData("Samsung", "TigerType", "SCX-4600 Series")]
    [InlineData(null, null, "WIA-Samsung C460")]
    [InlineData("Microsoft", "wiatwain.ds", "WIA device")]
    public void Wia_TigerType_scans_one_sheet_at_a_time(string? manufacturer, string? family, string? name)
    {
        Assert.True(WiaFeeder.ScanOneSheetAtATime(manufacturer, family, name));
    }

    [Theory]
    [InlineData("PFU Limited", "PaperStream IP", "PaperStream IP fi-8170")]
    [InlineData("Ricoh", "PaperStream IP", "RICOH fi-8270")]
    [InlineData("Ambir Technology", "nScan", "nScan 690gt")]
    [InlineData(null, null, "WIA-Ambir nScan 690gt")]
    [InlineData("Ambir", "nScan 690gt", "Ambir nScan 690gt")]
    public void Ricoh_and_Ambir_keep_their_own_transfer_count(string? manufacturer, string? family, string? name)
    {
        Assert.False(WiaFeeder.ScanOneSheetAtATime(manufacturer, family, name));
    }

    [Fact]
    public void An_empty_first_enable_waits_and_a_later_empty_enable_finishes_the_pages_already_scanned()
    {
        Assert.Equal(WiaFeeder.SheetStep.WaitForPaper, WiaFeeder.AfterEnable(0, 0, 0, 0, feederStillLoaded: true));
        Assert.Equal(WiaFeeder.SheetStep.FeederEmptied, WiaFeeder.AfterEnable(2, 2, 2, 0, feederStillLoaded: true));
    }

    [Fact]
    public void One_sheet_continues_while_paper_remains_and_stops_at_maxSheets_or_an_empty_feeder()
    {
        Assert.Equal(WiaFeeder.SheetStep.NextSheet, WiaFeeder.AfterEnable(0, 1, 0, 0, feederStillLoaded: true));
        Assert.Equal(WiaFeeder.SheetStep.NextSheet, WiaFeeder.AfterEnable(1, 3, 1, 0, feederStillLoaded: true));
        Assert.Equal(WiaFeeder.SheetStep.Finished, WiaFeeder.AfterEnable(0, 2, 0, 1, feederStillLoaded: true));
        Assert.Equal(WiaFeeder.SheetStep.Finished, WiaFeeder.AfterEnable(0, 1, 0, 0, feederStillLoaded: false));
        Assert.Equal(WiaFeeder.SheetStep.NextSheet, WiaFeeder.AfterEnable(0, 2, 0, 3, feederStillLoaded: true));
        Assert.Equal(WiaFeeder.SheetStep.Finished, WiaFeeder.AfterEnable(2, 4, 1, 2, feederStillLoaded: true));
    }
}
