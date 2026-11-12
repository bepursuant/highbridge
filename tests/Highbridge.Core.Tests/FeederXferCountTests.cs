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
        var duplex = new ScanOptions { Source = PaperSource.Feeder, Duplex = true, OneSheet = true };
        var front = new ScanOptions { Source = PaperSource.Feeder, OneSheet = true };
        var whole = new ScanOptions { Source = PaperSource.Feeder, Duplex = true };

        Assert.Equal(1, FeederXferCount.Value(duplex, countsSheets: true));
        Assert.Equal(2, FeederXferCount.Value(duplex, countsSheets: false));
        Assert.Equal(1, FeederXferCount.Value(front, countsSheets: true));
        Assert.Equal(1, FeederXferCount.Value(front, countsSheets: false));
        Assert.Null(FeederXferCount.Value(whole, countsSheets: true));
        Assert.Null(FeederXferCount.Value(whole, countsSheets: false));
    }
}
