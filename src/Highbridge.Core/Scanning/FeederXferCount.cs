using System.Text.RegularExpressions;

namespace Highbridge.Scanning;

/// <summary>
/// The number to put in CAP_XFERCOUNT for one job. The TWAIN spec counts images, and that is
/// what the Ambir nScan driver does: a duplex sheet is 2. PaperStream IP (the Ricoh fi-8170
/// and the rest of the PFU/Fujitsu fi series) counts sheets, and duplex still returns both
/// sides of each sheet. Asking that driver for 2 feeds a second sheet; aborting it with
/// DAT_PENDINGXFERS / MSG_RESET crashes the driver.
/// </summary>
public static partial class FeederXferCount
{
    /// <summary>
    /// True when this TWAIN identity counts CAP_XFERCOUNT in sheets. The strings are the
    /// source's manufacturer, product family, and product name.
    /// </summary>
    public static bool CountsSheets(string? manufacturer, string? productFamily, string? productName)
    {
        string id = $"{manufacturer} {productFamily} {productName}";
        if (id.Contains("PaperStream", StringComparison.OrdinalIgnoreCase)
            || id.Contains("PFU", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Fujitsu", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return FiSeries().IsMatch(id);
    }

    /// <summary>
    /// Null leaves CAP_XFERCOUNT unset (the source scans until the feeder is empty).
    /// A flatbed is one shot and does not use this capability.
    /// Otherwise the value to set: <see cref="ScanOptions.MaxSheets"/> for a sheet-counting
    /// feeder (never a page count), or that many sheets times the number of sides for an
    /// image-counting feeder.
    /// </summary>
    public static int? Value(ScanOptions options, bool countsSheets)
    {
        if (options.Source == PaperSource.Flatbed || options.MaxSheets <= 0)
        {
            return null;
        }

        if (countsSheets)
        {
            return options.MaxSheets;
        }

        return options.MaxSheets * (options.Duplex ? 2 : 1);
    }

    [GeneratedRegex(@"\bfi-\d{3,4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FiSeries();
}
