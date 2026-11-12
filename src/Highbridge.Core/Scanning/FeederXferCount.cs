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
    /// Otherwise the value to set: images for a spec-counting feeder, one sheet for a
    /// sheet-counting feeder when <see cref="ScanOptions.OneSheet"/> is on.
    /// </summary>
    public static int? Value(ScanOptions options, bool countsSheets)
    {
        if (options.EffectivePageLimit() is not int images)
        {
            return null;
        }

        return options.OneSheet && countsSheets ? 1 : images;
    }

    [GeneratedRegex(@"\bfi-\d{3,4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FiSeries();
}
