using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NTwain;
using NTwain.Data;
using Highbridge.Imaging;
using Highbridge.Scanning;

namespace Highbridge.Twain;

/// <summary>
/// One scan job, from opening the TWAIN manager to closing it. Everything except the watchdog and
/// cancellation runs on the <see cref="TwainThread"/>; NTwain raises its events there because the
/// session is bound to that thread's message loop.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TwainScan
{
    private readonly TwainThread _thread;
    private readonly ScanOptions _options;
    private readonly Action<ScannedPage>? _onPage;
    private readonly ILogger _logger;
    private readonly TwainTimeouts _timeouts;
    private readonly TaskCompletionSource<IReadOnlyList<ScannedPage>> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ScannedPage> _pages = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Threading.Timer _watchdog;
    private readonly CancellationTokenRegistration _cancellation;
    private TwainSession? _session;
    private DataSource? _source;
    private NegotiatedScan _negotiated = new(XferMech.Native, true);
    private TWImageInfo? _pendingImage;
    private StripImageAssembler? _strips;
    private MemoryStream? _encodedStrips;
    private Exception? _transferError;
    private long _lastActivity;
    private volatile string _phase = "waiting for the TWAIN thread";
    private bool _shutDown;
    private volatile bool _driverWindow;

    public TwainScan(TwainThread thread, ScanOptions options, Action<ScannedPage>? onPage, ILogger logger, TwainTimeouts timeouts, CancellationToken cancellationToken)
    {
        _thread = thread;
        _options = options;
        _onPage = onPage;
        _logger = logger;
        _timeouts = timeouts;
        _lastActivity = _clock.ElapsedTicks;
        _watchdog = new System.Threading.Timer(_ => CheckForStall(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _cancellation = cancellationToken.Register(() => Abort(new OperationCanceledException(cancellationToken), "the scan was canceled"));
    }

    public Task<IReadOnlyList<ScannedPage>> Result => _result.Task;

    /// <summary>Completes once the source and the TWAIN manager are closed again.</summary>
    public Task Closed => _closed.Task;

    /// <summary>Runs on the TWAIN thread: opens everything, negotiates, and enables the source (TWAIN state 5).</summary>
    public void Start()
    {
        if (_result.Task.IsCompleted)
        {
            Shutdown();
            return;
        }

        try
        {
            _logger.LogInformation("Scan requested: {Options}", _options);
            Progress("opening the TWAIN manager");
            _session = TwainSessions.Open(_thread, _logger);
            Hook(_session);

            _source = TwainSessions.PickSource(_session, _options.ScannerId, _logger);
            Progress($"opening '{_source.Name}'");
            var rc = _source.Open();
            if (rc != ReturnCode.Success)
            {
                _logger.LogWarning("MSG_OPENDS for '{Source}' failed: {ReturnCode}, {Status}", _source.Name, rc, TwainDiagnostics.ManagerStatus(_session));
                throw new ScannerException("scanner_unavailable", $"Could not open '{_source.Name}'. Is it switched on and connected? ({rc})");
            }

            _logger.LogInformation("Opened '{Source}' on thread {Thread}; TWAIN state {State}. TWAIN modules loaded: {Modules}",
                _source.Name, Environment.CurrentManagedThreadId, _session.State, string.Join("; ", TwainDiagnostics.LoadedTwainModules()));
            if (TwainDiagnostics.IsWiaCompatibilityLayer(_source))
            {
                _logger.LogInformation("'{Source}' is Windows' WIA-TWAIN compatibility layer, which supports only a few settings; any it rejects keep their defaults", _source.Name);
            }

            Progress("negotiating settings");
            _negotiated = new CapabilityNegotiator(_source, _session, _logger).Apply(_options);

            var mode = _options.ShowDriverUi ? SourceEnableMode.ShowUI : SourceEnableMode.NoUI;
            _driverWindow = _options.ShowDriverUi;
            Progress("starting the scanner");
            rc = _source.Enable(mode, false, _thread.WindowHandle);
            _logger.LogInformation("MSG_ENABLEDS ({Mode}, parent window 0x{Window:X}) returned {ReturnCode}; TWAIN state {State}",
                mode, _thread.WindowHandle.ToInt64(), rc, _session.State);
            if (rc == ReturnCode.CheckStatus)
            {
                _driverWindow = true;
                _logger.LogInformation("The source cannot scan without its own window, so it will show one; the scan continues when you finish there");
            }
            else if (rc != ReturnCode.Success)
            {
                _logger.LogWarning("MSG_ENABLEDS failed: {Status}", TwainDiagnostics.SourceStatus(_source));
                throw new ScannerException("scan_not_started", $"'{_source.Name}' did not start scanning ({rc}).");
            }

            Progress("waiting for the scanner to deliver the first page");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The scan did not start ({Phase})", _phase);
            Fail(ex);
            Shutdown();
        }
    }

    /// <summary>Ends the scan from any thread: the caller gets <paramref name="reason"/> now; TWAIN is closed on its thread.</summary>
    public void Abort(Exception reason, string why)
    {
        if (!Fail(reason))
        {
            return;
        }

        _logger.LogWarning("Stopping the scan: {Why}. TWAIN state {State}, last step: {Phase}", why, _session?.State, _phase);
        try
        {
            _thread.Post(Shutdown);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            _closed.TrySetResult();
        }
    }

    private void Hook(TwainSession session)
    {
        session.StateChanged += (_, _) => _logger.LogInformation("TWAIN state is now {State} ({StateName})", session.State, session.StateEx);
        session.TransferReady += OnTransferReady;
        session.DataTransferred += OnDataTransferred;
        session.TransferError += OnTransferError;
        session.TransferCanceled += (_, _) =>
        {
            Progress("the source canceled a transfer");
            _logger.LogInformation("The source canceled the transfer (TWRC_CANCEL)");
        };
        session.DeviceEvent += (_, e) => _logger.LogInformation("Device event {Event} from {Device}", e.DeviceEvent.Event, e.DeviceEvent.DeviceName);
        session.SourceDisabled += OnSourceDisabled;
    }

    private void OnTransferReady(object? sender, TransferReadyEventArgs e)
    {
        FinishMemoryPage();
        Progress($"transferring page {_pages.Count + 1}");
        _logger.LogInformation("Transfer ready (MSG_XFERREADY): {Pending} pending, end of job {EndOfJob}, TWAIN state {State}",
            e.PendingTransferCount, e.EndOfJobFlag, _session?.State);

        if (_result.Task.IsCompleted)
        {
            _logger.LogInformation("The scan was already stopped; canceling the remaining transfers");
            e.CancelAll = true;
            return;
        }

        if (_options.EffectivePageLimit() is int limit && _pages.Count >= limit)
        {
            if (_negotiated.SheetCountedFeeder)
            {
                // MSG_RESET (CancelAll) aborts a sheet PaperStream has already picked and
                // crashes the fi-8170. CAP_XFERCOUNT was set to one sheet, so this is only a
                // stray image; it is dropped in AddPage and the feeder is left alone.
                _logger.LogInformation("Reached {Limit} page(s). Not resetting the feeder; this driver crashes if a picked sheet is aborted", limit);
            }
            else
            {
                _logger.LogInformation("Reached {Limit} page(s); canceling the remaining transfers", limit);
                e.CancelAll = true;
                return;
            }
        }

        try
        {
            _pendingImage = e.PendingImageInfo;
            if (_pendingImage is { } info)
            {
                _logger.LogInformation("Next image: {Width} x {Length} px, {Bits} bits per pixel, {PixelType}, compression {Compression}, {XRes} x {YRes} dpi, transfer {Mechanism}",
                    info.ImageWidth, info.ImageLength, info.BitsPerPixel, info.PixelType, info.Compression, info.XResolution, info.YResolution, _negotiated.Mechanism);
            }
            else
            {
                _logger.LogInformation("DAT_IMAGEINFO failed: {Status}", _source is null ? "no source" : TwainDiagnostics.SourceStatus(_source));
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not read DAT_IMAGEINFO");
        }
    }

    private void OnDataTransferred(object? sender, DataTransferredEventArgs e)
    {
        Progress($"receiving page {_pages.Count + 1}");
        try
        {
            switch (e.TransferType)
            {
                case XferMech.Memory:
                    AddStrip(e.MemoryInfo, e.MemoryData);
                    break;
                case XferMech.File:
                case XferMech.MemFile:
                    _logger.LogInformation("File transfer: {Path} ({Format})", e.FileDataPath, e.ImageFileFormat);
                    using (var file = File.OpenRead(e.FileDataPath))
                    {
                        AddPage(Encode(file));
                    }

                    TryDelete(e.FileDataPath);
                    break;
                default:
                    using (var native = e.GetNativeImageStream())
                    {
                        if (native is null)
                        {
                            throw new ScannerException("unsupported_transfer", "The scanner returned image data highbridge cannot read.");
                        }

                        AddPage(Encode(native));
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not convert the {Mechanism} transfer for page {Page}", e.TransferType, _pages.Count + 1);
            _transferError ??= ex;
        }
    }

    private void OnTransferError(object? sender, TransferErrorEventArgs e)
    {
        Progress("the source reported a transfer error");
        var condition = e.SourceStatus?.ConditionCode;
        _logger.LogWarning(e.Exception, "Transfer error: {ReturnCode}, TWCC {Condition}, TWAIN state {State}", e.ReturnCode, condition, _session?.State);
        _transferError ??= e.Exception ?? ErrorFor(e.ReturnCode, condition);
    }

    private void OnSourceDisabled(object? sender, EventArgs e)
    {
        if (_shutDown)
        {
            return;
        }

        FinishMemoryPage();
        _logger.LogInformation("Source disabled (TWAIN state {State}, reason {Reason}): {Pages} page(s) in {Seconds:F1} s",
            _session?.State, _session?.DisableReason, _pages.Count, _clock.Elapsed.TotalSeconds);
        Complete();

        // Close after NTwain has returned from this event, not inside it.
        _thread.Post(Shutdown);
    }

    private void AddStrip(TWImageMemXfer strip, byte[] data)
    {
        var info = _pendingImage ?? throw new ScannerException("unsupported_transfer", "The scanner sent image data without describing the image.");
        if (strip.Compression != CompressionType.None)
        {
            // Compressed memory transfers (JPEG, PNG...) carry an encoded file split across buffers.
            (_encodedStrips ??= new MemoryStream()).Write(data, 0, (int)strip.BytesWritten);
            return;
        }

        if (info.Planar)
        {
            throw new ScannerException("unsupported_transfer", "The scanner sent planar image data, which highbridge cannot read.");
        }

        _strips ??= new StripImageAssembler(info.ImageWidth, info.BitsPerPixel, _negotiated.ZeroIsBlack);
        _strips.AddStrip((int)strip.XOffset, (int)strip.YOffset, (int)strip.Columns, (int)strip.Rows, (int)strip.BytesPerRow, data.AsSpan(0, (int)strip.BytesWritten));
        if (info.ImageLength > 0 && _strips.Height >= info.ImageLength)
        {
            FinishMemoryPage();
        }
    }

    /// <summary>A memory transfer has no "image done" event, so the page is finished here, at the next image or at the end.</summary>
    private void FinishMemoryPage()
    {
        try
        {
            if (_strips is { IsEmpty: false } strips)
            {
                _strips = null;
                AddPage(PngPage(strips.Width, strips.Height, strips.ToPng()));
            }

            if (_encodedStrips is { Length: > 0 } encoded)
            {
                _encodedStrips = null;
                encoded.Position = 0;
                using (encoded)
                {
                    AddPage(Encode(encoded));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not assemble the memory transfer for page {Page}", _pages.Count + 1);
            _transferError ??= ex;
        }
    }

    private void AddPage(ScannedPage page)
    {
        if (_negotiated.SheetCountedFeeder && _options.EffectivePageLimit() is int limit && _pages.Count >= limit)
        {
            _logger.LogInformation("Ignoring page {Page}; the one-sheet limit is {Limit} and this feeder cannot be reset", _pages.Count + 1, limit);
            return;
        }

        _pages.Add(page);
        Progress($"received page {_pages.Count}");
        _logger.LogInformation("Page {Page}: {Width} x {Height} px, {Bytes} bytes {MediaType}", _pages.Count, page.Width, page.Height, page.Data.Length, page.MediaType);
        _onPage?.Invoke(page);
    }

    private ScannedPage Encode(Stream image)
    {
        // A PDF job is assembled once every page of that job is in. The transfer itself is the
        // page image, same as PNG, so stamps and barcodes can read it.
        var encoding = _options.Format switch
        {
            OutputFormat.Png or OutputFormat.Pdf => ImageFormat.Png,
            _ => throw new ScannerException("unsupported_format", $"highbridge cannot produce {_options.Format} pages."),
        };

        using var decoded = Image.FromStream(image);
        using var output = new MemoryStream();
        decoded.Save(output, encoding);
        return PngPage(decoded.Width, decoded.Height, output.ToArray());
    }

    private ScannedPage PngPage(int width, int height, byte[] png)
    {
        if (_options.Format is not (OutputFormat.Png or OutputFormat.Pdf))
        {
            throw new ScannerException("unsupported_format", $"highbridge cannot produce {_options.Format} pages.");
        }

        return new ScannedPage(_pages.Count, OutputFormats.PngMediaType, width, height, png) { ScannerName = _source?.Name };
    }

    private void Complete()
    {
        if (_pages.Count == 0 && _transferError is not null)
        {
            Fail(_transferError as ScannerException ?? new ScannerException("transfer_error", "The scan failed. Check the scanner and try again.", _transferError));
            return;
        }

        if (_transferError is not null)
        {
            _logger.LogWarning("Returning {Pages} page(s) despite a transfer error", _pages.Count);
        }

        _result.TrySetResult(_pages.ToArray());
    }

    private bool Fail(Exception error) => error is OperationCanceledException canceled
        ? _result.TrySetCanceled(canceled.CancellationToken)
        : _result.TrySetException(error);

    /// <summary>Runs on the TWAIN thread. Steps TWAIN back down to state 2 from wherever it is; safe to call more than once.</summary>
    private void Shutdown()
    {
        if (_shutDown)
        {
            return;
        }

        _shutDown = true;
        _watchdog.Dispose();
        _cancellation.Dispose();
        try
        {
            if (_session is { } session)
            {
                int before = session.State;
                if (before > 3)
                {
                    session.ForceStepDown(2);
                }
                else if (before == 3)
                {
                    session.Close();
                }

                _logger.LogInformation("Closed TWAIN: state {Before} -> {After}", before, session.State);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing TWAIN");
        }
        finally
        {
            if (!_result.Task.IsCompleted)
            {
                Complete();
            }

            _closed.TrySetResult();
        }
    }

    private void Progress(string phase)
    {
        _phase = phase;
        Interlocked.Exchange(ref _lastActivity, _clock.ElapsedTicks);
    }

    private void CheckForStall()
    {
        if (_result.Task.IsCompleted)
        {
            return;
        }

        int state = _session?.State ?? 0;
        // Someone working in the driver's own window is not a stall.
        var limit = state >= 6 || _driverWindow ? _timeouts.Transfer : _timeouts.Idle;
        var idle = TimeSpan.FromSeconds((_clock.ElapsedTicks - Interlocked.Read(ref _lastActivity)) / (double)Stopwatch.Frequency);
        if (idle < limit)
        {
            return;
        }

        var windows = TwainDiagnostics.VisibleProcessWindows();
        _logger.LogError("No TWAIN activity for {Seconds:F0} s in state {State} ({Phase}). Visible windows of this process: {Windows}",
            idle.TotalSeconds, state, _phase, windows.Count > 0 ? string.Join("; ", windows) : "none");
        string hint = windows.Count > 0 ? " If a scanner window is open, finish or close it." : string.Empty;
        Abort(new ScannerException("scan_timeout", $"The scanner stopped responding ({_phase}).{hint} Then try again."), $"timed out after {idle.TotalSeconds:F0} s");
    }

    private static ScannerException ErrorFor(ReturnCode rc, ConditionCode? condition) => condition switch
    {
        ConditionCode.PaperJam => new("paper_jam", "The paper jammed. Clear the jam, then try again."),
        ConditionCode.PaperDoubleFeed => new("double_feed", "Two sheets were fed at once. Reload the paper, then try again."),
        ConditionCode.NoMedia => new("no_paper", "There is no paper in the scanner."),
        ConditionCode.CheckDeviceOnline => new("scanner_offline", "The scanner is offline. Check that it is on and connected."),
        _ => new("transfer_error", $"The scanner reported an error ({rc}, TWCC {condition?.ToString() ?? "unknown"})."),
    };

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation(ex, "Could not delete the transfer file {Path}", path);
        }
    }
}

/// <summary>
/// How long a scan may go without any TWAIN activity before it fails with "scan_timeout":
/// <see cref="Idle"/> while waiting for the scanner (states 4-5), <see cref="Transfer"/> during a
/// transfer (states 6-7), where one slow page over Wi-Fi can be a single long driver call.
/// </summary>
public sealed record TwainTimeouts(TimeSpan Idle, TimeSpan Transfer)
{
    public static TwainTimeouts Default { get; } = new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
}
