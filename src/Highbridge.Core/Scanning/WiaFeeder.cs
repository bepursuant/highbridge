namespace Highbridge.Scanning;

/// <summary>
/// How a WIA-TWAIN feeder (Windows' wiatwain.ds, including the TigerType minidriver on the
/// Samsung C460) has to be driven. It is not the Ricoh sheet count and not the Ambir image count.
/// </summary>
internal static class WiaFeeder
{
    /// <summary>Thrown by the TWAIN backend when a WIA feeder enable finds no paper. The job keeps waiting.</summary>
    internal const string EmptyFeederCode = "feeder_empty";

    internal enum SheetStep
    {
        /// <summary>The first enable returned nothing. Wait for paper and try again. Do not fail the job.</summary>
        WaitForPaper,

        /// <summary>A later enable returned nothing. The sheets already scanned are the whole feeder.</summary>
        FeederEmptied,

        /// <summary>This sheet filled <see cref="ScanOptions.MaxSheets"/>, or the feeder reports no more paper.</summary>
        Finished,

        /// <summary>More paper is waiting. Enable again for one more sheet. Do not keep this enable open.</summary>
        NextSheet,
    }

    /// <summary>
    /// True for the WIA compatibility layer. False for PaperStream / fi (sheet-counted
    /// CAP_XFERCOUNT) and for the Ambir (one enable, image-counted CAP_XFERCOUNT), even when
    /// that source's name starts with "WIA-".
    /// </summary>
    internal static bool ScanOneSheetAtATime(string? manufacturer, string? productFamily, string? productName)
    {
        if (FeederXferCount.CountsSheets(manufacturer, productFamily, productName))
        {
            return false;
        }

        string id = $"{manufacturer} {productFamily} {productName}";
        if (id.Contains("Ambir", StringComparison.OrdinalIgnoreCase)
            || id.Contains("nScan", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return (productName?.StartsWith("WIA-", StringComparison.OrdinalIgnoreCase) ?? false)
            || id.Contains("TigerType", StringComparison.OrdinalIgnoreCase)
            || id.Contains("wiatwain", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What to do after one enable. <paramref name="pagesBefore"/> is the page count going in.
    /// <paramref name="sheetsAlreadyDone"/> does not include this enable. <paramref name="maxSheets"/>
    /// of 0 means no limit. <paramref name="feederStillLoaded"/> is false only when the source
    /// reports an empty feeder; "cannot say" is true, and the next enable decides.
    /// </summary>
    internal static SheetStep AfterEnable(int pagesBefore, int pagesAfter, int sheetsAlreadyDone, int maxSheets, bool feederStillLoaded)
    {
        if (pagesAfter <= pagesBefore)
        {
            return pagesAfter == 0 ? SheetStep.WaitForPaper : SheetStep.FeederEmptied;
        }

        int sheets = sheetsAlreadyDone + 1;
        if ((maxSheets > 0 && sheets >= maxSheets) || !feederStillLoaded)
        {
            return SheetStep.Finished;
        }

        return SheetStep.NextSheet;
    }
}
