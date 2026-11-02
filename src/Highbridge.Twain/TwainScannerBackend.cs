using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NTwain;
using NTwain.Data;
using Highbridge.Scanning;

namespace Highbridge.Twain;

/// <summary>
/// TWAIN scanning through NTwain (MIT). Uses whatever TWAIN manager the scanner driver installed
/// (TWAINDSM.dll, or the old twain_32.dll that ships with Windows); highbridge does not ship one.
/// A process only sees data sources of its own bitness. All TWAIN work runs on one
/// <see cref="TwainThread"/>; see there for why.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TwainScannerBackend : IScannerBackend, IDisposable
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DiagnoseTimeout = TimeSpan.FromMinutes(3);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;
    private readonly TwainTimeouts _timeouts;
    private readonly object _threadLock = new();
    private TwainThread _thread;

    public TwainScannerBackend(ILogger<TwainScannerBackend>? logger = null, TwainTimeouts? timeouts = null)
    {
        _logger = logger ?? NullLogger<TwainScannerBackend>.Instance;
        _timeouts = timeouts ?? TwainTimeouts.Default;
        NTwainLog.Install(_logger);
        _thread = StartThread();
    }

    public string Name => "twain";

    public static string DescribeEnvironment() => TwainDiagnostics.DescribeEnvironment();

    /// <summary>
    /// Makes TWAINDSM.dll log every TWAIN call to <paramref name="path"/> (overwritten when it loads).
    /// Has no effect on the old twain_32.dll. Call before the first TWAIN use in the process.
    /// </summary>
    public static void EnableManagerLog(string path)
    {
        if (Environment.GetEnvironmentVariable(TwainDiagnostics.DsmLogVariable) is null)
        {
            Environment.SetEnvironmentVariable(TwainDiagnostics.DsmLogVariable, path);
            Environment.SetEnvironmentVariable("TWAINDSM_LOGMODE", "w");
        }
    }

    public Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync<IReadOnlyList<ScannerInfo>>("list scanners", QueryTimeout, () =>
        {
            var session = TwainSessions.Open(CurrentThread, _logger);
            try
            {
                string? defaultName = TwainSessions.SafeDefault(session, _logger)?.Name;
                var sources = session.ToList();
                _logger.LogInformation("TWAIN sources ({Count}): {Sources}", sources.Count, string.Join(" | ", sources.Select(TwainDiagnostics.DescribeSource)));
                return sources.Select(s => new ScannerInfo(s.Name, s.Name, s.Manufacturer, Name, s.Name == defaultName)).ToArray();
            }
            finally
            {
                TwainSessions.Close(session, _logger);
            }
        }, cancellationToken);

    public async Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var thread = CurrentThread;
        TwainScan scan;
        try
        {
            scan = new TwainScan(thread, options, onPage, _logger, _timeouts, cancellationToken);
            thread.Post(scan.Start);
        }
        catch
        {
            _gate.Release();
            throw;
        }

        try
        {
            return await scan.Result.ConfigureAwait(false);
        }
        finally
        {
            _ = ReleaseWhenClosedAsync(scan.Closed, thread);
        }
    }

    /// <summary>
    /// Writes a report of the TWAIN setup: managers, installed and listed sources, and every
    /// capability of each source with its values. Opens each source but does not scan.
    /// </summary>
    public Task<string> DiagnoseAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync("diagnose", DiagnoseTimeout, () =>
        {
            var report = new StringBuilder();
            report.AppendLine(CultureInfo.InvariantCulture, $"highbridge scanner diagnostics, {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            report.AppendLine(CultureInfo.InvariantCulture, $"Environment: {TwainDiagnostics.DescribeEnvironment()}");
            report.AppendLine("Installed data sources (*.ds):");
            foreach (string file in TwainDiagnostics.InstalledDataSources())
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  {file}");
            }

            TwainSession session;
            try
            {
                session = TwainSessions.Open(CurrentThread, _logger);
            }
            catch (ScannerException ex)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"Could not open the TWAIN manager: {ex.Message}");
                return report.ToString();
            }

            try
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"TWAIN manager open, state {session.State}. Loaded: {string.Join("; ", TwainDiagnostics.LoadedTwainModules())}");
                var sources = session.ToList();
                report.AppendLine(CultureInfo.InvariantCulture, $"Default source: {TwainSessions.SafeDefault(session, _logger)?.Name ?? "none"}");
                report.AppendLine(CultureInfo.InvariantCulture, $"Sources ({sources.Count}):");
                foreach (var source in sources)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"  {TwainDiagnostics.DescribeSource(source)}");
                }

                foreach (var source in sources)
                {
                    report.AppendLine();
                    report.AppendLine(CultureInfo.InvariantCulture, $"Source '{source.Name}':");
                    var rc = source.Open();
                    if (rc != ReturnCode.Success)
                    {
                        report.AppendLine(CultureInfo.InvariantCulture, $"  MSG_OPENDS failed: {rc}, {TwainDiagnostics.ManagerStatus(session)}");
                        continue;
                    }

                    report.AppendLine(CultureInfo.InvariantCulture, $"  opened on thread {Environment.CurrentManagedThreadId}, state {session.State}");
                    report.AppendLine(CultureInfo.InvariantCulture, $"  TWAIN modules loaded: {string.Join("; ", TwainDiagnostics.LoadedTwainModules())}");
                    report.AppendLine("  Capabilities:");
                    TwainDiagnostics.DumpCapabilities(source, report);
                    rc = source.Close();
                    report.AppendLine(CultureInfo.InvariantCulture, $"  MSG_CLOSEDS: {rc}");
                }
            }
            finally
            {
                TwainSessions.Close(session, _logger);
            }

            string text = report.ToString();
            _logger.LogInformation("Scanner diagnostics:{NewLine}{Report}", Environment.NewLine, text);
            return text;
        }, cancellationToken);

    public void Dispose() => CurrentThread.Dispose();

    private TwainThread CurrentThread
    {
        get
        {
            lock (_threadLock)
            {
                return _thread;
            }
        }
    }

    private TwainThread StartThread()
    {
        var thread = new TwainThread("highbridge-twain");
        _logger.LogInformation("TWAIN thread {Thread} started, parent window 0x{Window:X}. {Environment}",
            thread.ManagedThreadId, thread.WindowHandle.ToInt64(), TwainDiagnostics.DescribeEnvironment());
        return thread;
    }

    private async Task<T> RunExclusiveAsync<T>(string what, TimeSpan timeout, Func<T> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var thread = CurrentThread;
        var task = thread.InvokeAsync(work);
        try
        {
            return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogError("TWAIN did not finish '{What}' within {Timeout}", what, timeout);
            throw new ScannerException("twain_unresponsive", "The scanner driver stopped responding. Check the scanner; if this keeps happening, restart highbridge.");
        }
        finally
        {
            _ = ReleaseWhenClosedAsync(task, thread);
        }
    }

    /// <summary>
    /// Holds the gate until TWAIN is closed again, so the next job never meets a half-open source.
    /// If the TWAIN thread is stuck inside a driver call, later jobs get a new thread.
    /// </summary>
    private async Task ReleaseWhenClosedAsync(Task closed, TwainThread thread)
    {
        try
        {
            await closed.WaitAsync(CloseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogError("The TWAIN thread {Thread} is stuck inside the scanner driver; starting a new one. If scanning keeps failing, restart highbridge.", thread.ManagedThreadId);
            lock (_threadLock)
            {
                if (_thread == thread)
                {
                    _thread = StartThread();
                }
            }

            thread.Dispose();
        }
        catch (Exception)
        {
            // The work's own exception already went to its caller.
        }
        finally
        {
            _gate.Release();
        }
    }
}
