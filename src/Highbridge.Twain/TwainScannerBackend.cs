using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Forms;
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
    private readonly TwainThread _thread;

    public TwainScannerBackend(ILogger<TwainScannerBackend>? logger = null, TwainTimeouts? timeouts = null)
    {
        _logger = logger ?? NullLogger<TwainScannerBackend>.Instance;
        _timeouts = timeouts ?? TwainTimeouts.Default;
        NTwainLog.Install(_logger);
        _thread = new TwainThread("highbridge-twain");
        _logger.LogInformation("TWAIN thread {Thread} started, parent window 0x{Window:X}. {Environment}",
            _thread.ManagedThreadId, _thread.WindowHandle.ToInt64(), TwainDiagnostics.DescribeEnvironment());
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
            var session = OpenSession(_thread, _logger);
            try
            {
                string? defaultName = DefaultSource(session, _logger)?.Name;
                var sources = session.ToList();
                _logger.LogInformation("TWAIN sources ({Count}): {Sources}", sources.Count, string.Join(" | ", sources.Select(TwainDiagnostics.DescribeSource)));
                return sources.Select(s => new ScannerInfo(s.Name, s.Name, s.Manufacturer, Name, s.Name == defaultName)).ToArray();
            }
            finally
            {
                CloseSession(session, _logger);
            }
        }, cancellationToken);

    public async Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TwainScan scan;
        try
        {
            scan = new TwainScan(_thread, options, onPage, _logger, _timeouts, cancellationToken);
            _thread.Post(scan.Start);
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
            _ = ReleaseWhenClosedAsync(scan.Closed);
        }
    }

    public Task<bool> WaitForFeederAsync(ScanOptions options, CancellationToken cancellationToken)
    {
        if (options.Source == PaperSource.Flatbed)
        {
            return Task.FromResult(false);
        }

        return WaitForFeederCoreAsync(options, cancellationToken);
    }

    /// <summary>
    /// Opens the source, and if it is feeding, polls CAP_FEEDERLOADED until paper is there.
    /// The TWAIN thread is not blocked between checks, and this wait is not the scan watchdog.
    /// </summary>
    private async Task<bool> WaitForFeederCoreAsync(ScanOptions options, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TwainSession? session = null;
        DataSource? source = null;
        try
        {
            bool? loaded = await _thread.InvokeAsync(() =>
            {
                session = OpenSession(_thread, _logger);
                source = PickSource(session, options.ScannerId, _logger);
                ReturnCode rc = source.Open();
                if (rc != ReturnCode.Success)
                {
                    throw new ScannerException("scanner_unavailable", $"Could not open '{source.Name}'. Is it switched on and connected? ({rc})");
                }

                return new CapabilityNegotiator(source, session, _logger).PrepareFeeder(options);
            }).ConfigureAwait(false);

            if (loaded is null)
            {
                return false;
            }

            bool announced = false;
            while (loaded == false)
            {
                if (!announced)
                {
                    _logger.LogInformation("CAP_FEEDERLOADED says the feeder is empty; waiting for paper");
                    announced = true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
                var polling = source;
                var pollingSession = session;
                loaded = await _thread.InvokeAsync(() => new CapabilityNegotiator(polling!, pollingSession!, _logger).FeederIsLoaded()).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            Task close;
            try
            {
                var open = session;
                close = open is null
                    ? Task.CompletedTask
                    : _thread.InvokeAsync(() =>
                    {
                        CloseSession(open, _logger);
                        return true;
                    });
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                close = Task.CompletedTask;
            }

            await ReleaseWhenClosedAsync(close).ConfigureAwait(false);
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
                session = OpenSession(_thread, _logger);
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
                report.AppendLine(CultureInfo.InvariantCulture, $"Default source: {DefaultSource(session, _logger)?.Name ?? "none"}");
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
                CloseSession(session, _logger);
            }

            string text = report.ToString();
            _logger.LogInformation("Scanner diagnostics:{NewLine}{Report}", Environment.NewLine, text);
            return text;
        }, cancellationToken);

    public void Dispose() => _thread.Dispose();

    private async Task<T> RunExclusiveAsync<T>(string what, TimeSpan timeout, Func<T> work, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var task = _thread.InvokeAsync(work);
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
            _ = ReleaseWhenClosedAsync(task);
        }
    }

    /// <summary>Holds the gate until TWAIN is closed again, so the next job never meets a half-open source.</summary>
    private async Task ReleaseWhenClosedAsync(Task closed)
    {
        try
        {
            await closed.WaitAsync(CloseTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // One STA thread for the process. A call stuck inside the driver blocks that thread;
            // a later scan waits on it. Restart highbridge to clear it.
            _logger.LogError("The TWAIN thread {Thread} is stuck inside the scanner driver. Restart highbridge if scanning keeps failing.", _thread.ManagedThreadId);
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

    /// <summary>Opens the TWAIN manager (state 3) bound to <paramref name="thread"/>'s message loop. Call on that thread.</summary>
    internal static TwainSession OpenSession(TwainThread thread, ILogger logger)
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
        var version = typeof(TwainScannerBackend).Assembly.GetName().Version ?? new Version(0, 1);
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

    internal static void CloseSession(TwainSession session, ILogger logger)
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

    internal static DataSource PickSource(TwainSession session, string? scannerId, ILogger logger)
    {
        var sources = session.ToList();
        var defaultSource = DefaultSource(session, logger);
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

    internal static DataSource? DefaultSource(TwainSession session, ILogger logger)
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

/// <summary>
/// The one thread that talks to TWAIN: an STA thread running a Windows Forms message loop around a
/// hidden window. NTwain is bound to this loop (<see cref="NTwain.WindowsFormsMessageLoopHook"/>),
/// so every TWAIN call, including capability negotiation, happens on the thread that opened the
/// source; COM-based drivers such as Windows' WIA-TWAIN layer fail calls from other threads.
/// The hidden window is passed to the source as its parent: TWAIN 1.x sources and the old
/// twain_32.dll manager report "transfer ready" only as window messages, which this loop then
/// hands to TWAIN.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TwainThread : IDisposable
{
    private readonly Thread _thread;
    private readonly Form _window;
    private bool _disposed;

    public TwainThread(string name)
    {
        var ready = new TaskCompletionSource<(Form Form, IntPtr Handle)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        (Form Form, IntPtr Handle) created = ready.Task.GetAwaiter().GetResult();
        _window = created.Form;
        WindowHandle = created.Handle;
    }

    /// <summary>The hidden window used as the TWAIN parent window (TW_USERINTERFACE.hParent).</summary>
    public IntPtr WindowHandle { get; }

    public int ManagedThreadId => _thread.ManagedThreadId;

    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>Runs <paramref name="action"/> on the TWAIN thread, inside its message loop.</summary>
    public void Post(Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.BeginInvoke(action);
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _window.BeginInvoke(Application.ExitThread);
        }
        catch (InvalidOperationException)
        {
            // The loop already ended.
        }
    }

    private static void Run(TaskCompletionSource<(Form Form, IntPtr Handle)> ready)
    {
        Form window;
        IntPtr handle;
        try
        {
            // Never shown; creating it also installs the thread's WindowsFormsSynchronizationContext.
            window = new Form { ShowInTaskbar = false, Text = "highbridge TWAIN" };
            handle = window.Handle;
        }
        catch (Exception ex)
        {
            ready.SetException(ex);
            return;
        }

        ready.SetResult((window, handle));
        Application.Run();
        window.Dispose();
    }
}

/// <summary>Sends NTwain's own trace (thread ids, every TWAIN message it handles) to the highbridge log.</summary>
internal sealed class NTwainLog(ILogger logger) : ILog
{
    public bool IsInfoEnabled { get; set; } = true;

    public bool IsDebugEnabled { get; set; } = true;

    public bool IsErrorEnabled { get; set; } = true;

    public static void Install(ILogger logger) => PlatformInfo.Current.Log = new NTwainLog(logger);

    public void Info(string message) => Write(LogLevel.Information, message, null);

    public void Info(string messageFormat, params object[] args) => Write(LogLevel.Information, Format(messageFormat, args), null);

    // NTwain's debug lines are the TWAIN event trace we want in the log, so they are logged as Information.
    public void Debug(string message) => Write(LogLevel.Information, message, null);

    public void Debug(string messageFormat, params object[] args) => Write(LogLevel.Information, Format(messageFormat, args), null);

    public void Error(string message) => Write(LogLevel.Error, message, null);

    public void Error(string message, Exception exception) => Write(LogLevel.Error, message, exception);

    public void Error(string messageFormat, Exception exception, params object[] args) => Write(LogLevel.Error, Format(messageFormat, args), exception);

    private static string Format(string format, object[] args)
    {
        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    private void Write(LogLevel level, string message, Exception? exception) =>
        logger.Log(level, exception, "NTwain [thread {ThreadId}]: {Message}", Environment.CurrentManagedThreadId, message);
}
