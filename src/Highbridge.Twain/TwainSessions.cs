using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NTwain;
using NTwain.Data;
using Highbridge.Scanning;

namespace Highbridge.Twain;

[SupportedOSPlatform("windows")]
internal static class TwainSessions
{
    /// <summary>Opens the TWAIN manager (state 3) bound to <paramref name="thread"/>'s message loop. Call on that thread.</summary>
    public static TwainSession Open(TwainThread thread, ILogger logger)
    {
        if (!thread.IsCurrentThread)
        {
            throw new InvalidOperationException("TWAIN sessions must be opened on the TWAIN thread.");
        }

        if (!PlatformInfo.Current.IsSupported)
        {
            logger.LogWarning("No TWAIN manager: {Environment}", TwainDiagnostics.DescribeEnvironment());
            throw new ScannerException("twain_unavailable", "No TWAIN driver manager is installed. Install your scanner's TWAIN driver.");
        }

        // A fresh identity each time: the manager writes its id into it on MSG_OPENDSM.
        var version = typeof(TwainSessions).Assembly.GetName().Version ?? new Version(0, 1);
        var appId = TWIdentity.Create(DataGroups.Image, version, "Pursuant Solutions", "highbridge", "highbridge", "highbridge browser scanning bridge");
        // Leave StopOnTransferError off: NTwain 3.7 counts a page's TWRC_XFERDONE as an error there
        // and resets the remaining transfers after the first page.
        var session = new TwainSession(appId);
        var rc = session.Open(new WindowsFormsMessageLoopHook(thread.WindowHandle));
        if (rc != ReturnCode.Success)
        {
            logger.LogWarning("MSG_OPENDSM failed: {ReturnCode}. {Environment}", rc, TwainDiagnostics.DescribeEnvironment());
            throw new ScannerException("twain_unavailable", $"Could not open the TWAIN driver manager ({rc}).");
        }

        logger.LogInformation("Opened the TWAIN manager {Dsm} on thread {Thread}; TWAIN state {State}; manager functionalities {Functionalities}",
            PlatformInfo.Current.ExpectedDsmPath, Environment.CurrentManagedThreadId, session.State, appId.DataFunctionalities);
        return session;
    }

    public static void Close(TwainSession session, ILogger logger)
    {
        try
        {
            if (session.State > 3)
            {
                session.ForceStepDown(2);
            }
            else if (session.State == 3)
            {
                session.Close();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error while closing the TWAIN manager");
        }
    }

    public static DataSource PickSource(TwainSession session, string? scannerId, ILogger logger)
    {
        var sources = session.ToList();
        var defaultSource = SafeDefault(session, logger);
        logger.LogInformation("TWAIN sources ({Count}): {Sources}; default: {Default}",
            sources.Count, string.Join(" | ", sources.Select(TwainDiagnostics.DescribeSource)), defaultSource?.Name ?? "none");
        if (sources.Count == 0)
        {
            logger.LogWarning("Installed data sources: {Files}", string.Join("; ", TwainDiagnostics.InstalledDataSources()));
            throw new ScannerException("no_scanners", $"No TWAIN scanners were found for this {(Environment.Is64BitProcess ? "64" : "32")}-bit app.");
        }

        DataSource picked;
        if (scannerId is not null)
        {
            picked = sources.FirstOrDefault(s => s.Name == scannerId)
                ?? throw new ScannerException("scanner_not_found", $"Scanner '{scannerId}' was not found.");
        }
        else
        {
            picked = defaultSource ?? sources[0];
        }

        if (TwainDiagnostics.IsWiaCompatibilityLayer(picked))
        {
            var vendor = sources.Where(s => !TwainDiagnostics.IsWiaCompatibilityLayer(s)).Select(s => s.Name).ToList();
            if (vendor.Count > 0)
            {
                logger.LogInformation("Using the WIA-TWAIN source '{Source}'; non-WIA TWAIN sources also installed: {Vendor}", picked.Name, string.Join(", ", vendor));
            }
        }

        return picked;
    }

    public static DataSource? SafeDefault(TwainSession session, ILogger logger)
    {
        try
        {
            return session.DefaultSource;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Could not read the default TWAIN source");
            return null;
        }
    }
}
