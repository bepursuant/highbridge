using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using NTwain;
using ReturnCode = NTwain.Data.ReturnCode;
using TWEvent = NTwain.Data.TWEvent;
using TwainMessage = NTwain.Data.Message;
using XferMech = NTwain.Data.XferMech;

namespace Highbridge.Twain;

/// <summary>
/// NTwain pulls every page inside the first MSG_XFERREADY, and it treats a pending count of -1
/// (the source has not said how many images remain) as "ask for the next image right now". WIA
/// answers that early request with Failure / TWCC_BUMMER and does not return for about two minutes.
/// This filter lets the notification return and takes one image per turn of the message loop.
/// The next image is requested only after that turn returns. A refused request is retried once,
/// after the message loop has run, and then the scan closes.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TwainTransferDeferral : IMessageFilter, IDisposable
{
    private const uint PmRemove = 0x0001;
    private const int PumpLimit = 32;

    private readonly TwainSession _session;
    private readonly TwainThread _thread;
    private readonly ILogger _logger;
    private readonly Action<Exception> _onFailure;
    private readonly Func<bool> _preparePage;
    private readonly object _event;
    private readonly object _pendingXfers;
    private readonly MethodInfo _processEvent;
    private readonly MethodInfo _handleSourceMessage;
    private readonly MethodInfo _setState;
    private readonly MethodInfo _endXfer;
    private readonly MethodInfo _getPending;
    private readonly MethodInfo _resetPending;
    private readonly MethodInfo _disableSource;
    private readonly Type _pendingType;
    private readonly PropertyInfo _pendingCount;
    private readonly Dictionary<XferMech, MethodInfo> _imageTransfers;
    private readonly uint _stepMessage;
    private IMessageFilter? _replaced;
    private bool _installed;
    private bool _stepQueued;
    private bool _busy;
    private bool _inTriplet;
    private bool _finishing;
    private bool _stopAfterPage;
    private bool _gotImage;
    private bool _loggedEarlyClose;
    private int _refused;
    private int _pumping;
    private int _disposed;

    private TwainTransferDeferral(
        TwainSession session,
        TwainThread thread,
        ILogger logger,
        Action<Exception> onFailure,
        Func<bool> preparePage,
        object twainEvent,
        object pendingXfers,
        MethodInfo processEvent,
        MethodInfo handleSourceMessage,
        MethodInfo setState,
        MethodInfo endXfer,
        MethodInfo getPending,
        MethodInfo resetPending,
        MethodInfo disableSource,
        Dictionary<XferMech, MethodInfo> imageTransfers,
        uint stepMessage)
    {
        _session = session;
        _thread = thread;
        _logger = logger;
        _onFailure = onFailure;
        _preparePage = preparePage;
        _event = twainEvent;
        _pendingXfers = pendingXfers;
        _processEvent = processEvent;
        _handleSourceMessage = handleSourceMessage;
        _setState = setState;
        _endXfer = endXfer;
        _getPending = getPending;
        _resetPending = resetPending;
        _disableSource = disableSource;
        _pendingType = endXfer.GetParameters()[0].ParameterType;
        _pendingCount = _pendingType.GetProperty("Count") ?? throw new MissingMemberException(_pendingType.FullName, "Count");
        _imageTransfers = imageTransfers;
        _stepMessage = stepMessage;
    }

    public static TwainTransferDeferral? TryInstall(TwainSession session, TwainThread thread, ILogger logger, Action<Exception> onFailure, Func<bool> preparePage)
    {
        try
        {
            if (typeof(TwainSession).GetField("_callbackObj", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session) is not null)
            {
                logger.LogInformation("The source registered a TWAIN callback, which already returns before the transfer");
                return null;
            }

            if (!TryBind(session, out var bound))
            {
                logger.LogWarning("Could not bind NTwain's transfer entry points; pages will be pulled inside the scanner's notification");
                return null;
            }

            uint stepMessage = RegisterWindowMessage("Highbridge.Twain.TransferStep");
            if (stepMessage == 0)
            {
                logger.LogWarning("Could not register the transfer message; pages will be pulled inside the scanner's notification");
                return null;
            }

            var deferral = new TwainTransferDeferral(
                session, thread, logger, onFailure, preparePage,
                bound.TwainEvent, bound.PendingXfers, bound.ProcessEvent, bound.HandleSourceMessage, bound.SetState,
                bound.EndXfer, bound.GetPending, bound.ResetPending, bound.DisableSource, bound.ImageTransfers, stepMessage);
            return deferral.TakeOver() ? deferral : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not defer TWAIN transfers; pages will be pulled inside the scanner's notification");
            return null;
        }
    }

    /// <summary>
    /// Dispatches messages already queued for this thread. Nothing is offered to the source while
    /// an image call is on the stack: that is the call that returns TWCC_BUMMER.
    /// </summary>
    public void PumpPostedMessages()
    {
        if (_disposed != 0 || _inTriplet || Interlocked.Exchange(ref _pumping, 1) != 0)
        {
            return;
        }

        try
        {
            for (int i = 0; i < PumpLimit; i++)
            {
                if (!PeekMessage(out NativeMessage message, IntPtr.Zero, 0, 0, PmRemove))
                {
                    return;
                }

                if (message.Message == _stepMessage)
                {
                    // Put our own step back and let the loop deliver it. Pulling it out here would
                    // start a second transfer inside this one.
                    PostMessage(_thread.WindowHandle, _stepMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            _pumping = 0;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_installed)
        {
            Application.RemoveMessageFilter(this);
            _installed = false;
        }
    }

    bool IMessageFilter.PreFilterMessage(ref Message message)
    {
        if (message.Msg == _stepMessage)
        {
            TransferOne();
            return true;
        }

        // An image triplet is in progress. Consuming MSG_XFERREADY here makes the source fail that
        // triplet with TWCC_BUMMER and sit until its own timeout, which is the multi-minute pause.
        // The same is true of the gap after MSG_XFERREADY, before the posted transfer runs: WIA
        // answers those messages with MSG_CLOSEDSREQ, and disabling there never feeds the first sheet.
        if (_disposed != 0 || _inTriplet || _stepQueued || _session.State < 5)
        {
            return false;
        }

        if (!TryConsume(ref message, out TwainMessage twainMessage))
        {
            return false;
        }

        if (twainMessage == TwainMessage.XferReady)
        {
            if (_busy || _finishing)
            {
                return true;
            }

            if (_session.State < 6)
            {
                _setState.Invoke(_session, [6]);
            }

            _logger.LogInformation("Transfer is ready. The scanner's notification will return before the page is taken");
            ScheduleStep();
            return true;
        }

        if (twainMessage is TwainMessage.CloseDSReq or TwainMessage.CloseDSOK)
        {
            // NTwain disables on CloseDSReq in state 5. Doing that from every message, before any
            // page exists, is the tight DisableSource loop. A close after a real page ends the scan once.
            if (!_gotImage)
            {
                if (!_loggedEarlyClose)
                {
                    _loggedEarlyClose = true;
                    _logger.LogInformation("The source sent {Message} before the first page (TWAIN state {State}). The scan stays open", twainMessage, _session.State);
                }

                return true;
            }

            _stopAfterPage = true;
            if (!_busy && !_finishing)
            {
                _logger.LogInformation("The source asked to close (TWAIN state {State}). Stopping the scan", _session.State);
                EndSession(reset: _session.State > 5);
            }

            return true;
        }

        try
        {
            _handleSourceMessage.Invoke(_session, [twainMessage]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not handle TWAIN message {Message}", twainMessage);
        }

        return true;
    }

    private void TransferOne()
    {
        if (_disposed != 0 || _finishing || _session.State < 5 || _busy || _inTriplet)
        {
            _stepQueued = false;
            return;
        }

        _stepQueued = false;
        _busy = true;
        try
        {
            if (_stopAfterPage && _gotImage)
            {
                EndSession(reset: _session.State > 5);
                return;
            }

            bool takePage;
            try
            {
                takePage = _preparePage();
            }
            catch (Exception ex)
            {
                _onFailure(ex);
                return;
            }

            if (!takePage)
            {
                EndSession(reset: true);
                return;
            }

            PumpPostedMessages();
            if (_session.State < 6)
            {
                _setState.Invoke(_session, [6]);
            }

            long started = Stopwatch.GetTimestamp();
            ReturnCode transfer = TransferImage();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            bool accepted = transfer is ReturnCode.XferDone or ReturnCode.Success;
            int pendingCount = -1;
            if (accepted)
            {
                pendingCount = EndImage(out ReturnCode endCode);
                _logger.LogInformation("Image transfer returned {ReturnCode} in {Seconds:F1} s; MSG_ENDXFER {EndCode}; pending count {Count}",
                    transfer, elapsed.TotalSeconds, endCode, pendingCount);
                _gotImage = true;
                _refused = 0;
                // A failed MSG_ENDXFER is the end of the batch. NTwain stops there too; another
                // image call from a half-closed source is the call that comes back as TWCC_BUMMER.
                if (endCode != ReturnCode.Success || _stopAfterPage || pendingCount == 0)
                {
                    // Count 0 means MSG_ENDXFER already left the source in state 5. A close request
                    // or a failed end, while more images are pending, still has to reset.
                    EndSession(reset: pendingCount != 0 && _session.State > 5);
                }
                else
                {
                    _logger.LogInformation("Pending count is {Count}. The next image waits until this call has returned to the message loop", pendingCount);
                    ScheduleStep();
                }

                return;
            }

            // The image was not accepted, so the source is still in state 6. MSG_ENDXFER there
            // cancels the next sheet. Read the count instead, and do not ask again immediately:
            // that immediate call is the one that blocks for the source's timeout.
            pendingCount = ReadPendingCount();
            _refused++;
            _logger.LogInformation("Image transfer returned {ReturnCode} in {Seconds:F1} s with no image; pending count {Count}",
                transfer, elapsed.TotalSeconds, pendingCount);
            if (pendingCount == 0 || transfer == ReturnCode.Cancel || _refused >= 2)
            {
                _logger.LogInformation("Closing the scan after {ReturnCode}; another immediate request is what blocks for the source's timeout", transfer);
                EndSession(reset: _session.State > 5);
                return;
            }

            _logger.LogInformation("The refused image will be requested once more, after this call returns to the message loop");
            ScheduleStep();
        }
        catch (Exception ex)
        {
            _onFailure(Unwrap(ex));
        }
        finally
        {
            _busy = false;
            _inTriplet = false;
        }
    }

    private ReturnCode TransferImage()
    {
        XferMech mechanism = XferMech.Native;
        try
        {
            if (_session.CurrentSource?.Capabilities.ICapXferMech.GetCurrent() is { } current)
            {
                mechanism = current;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the transfer mechanism; using native");
        }

        if (!_imageTransfers.TryGetValue(mechanism, out MethodInfo? transfer))
        {
            transfer = _imageTransfers[XferMech.Native];
        }

        _inTriplet = true;
        try
        {
            object? result = transfer.Invoke(null, [_session]);
            return result is ReturnCode code ? code : ReturnCode.Failure;
        }
        finally
        {
            _inTriplet = false;
        }
    }

    private int EndImage(out ReturnCode code)
    {
        object pending = NewPending();
        object? result = _endXfer.Invoke(_pendingXfers, [pending]);
        code = result is ReturnCode end ? end : ReturnCode.Failure;
        return CountOf(pending);
    }

    private int ReadPendingCount()
    {
        try
        {
            object pending = NewPending();
            _getPending.Invoke(_pendingXfers, [pending]);
            return CountOf(pending);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the pending transfer count");
            return -1;
        }
    }

    private object NewPending() => Activator.CreateInstance(_pendingType)!;

    private int CountOf(object pending) => (int)_pendingCount.GetValue(pending)!;

    private void ScheduleStep()
    {
        if (_disposed != 0 || _finishing || _stepQueued)
        {
            return;
        }

        _stepQueued = true;
        if (!PostMessage(_thread.WindowHandle, _stepMessage, IntPtr.Zero, IntPtr.Zero))
        {
            _stepQueued = false;
            _logger.LogWarning("Could not post the next page transfer");
        }
    }

    private void EndSession(bool reset)
    {
        if (_finishing || _disposed != 0)
        {
            return;
        }

        _finishing = true;
        _stepQueued = false;
        _stopAfterPage = false;
        try
        {
            if (reset && _session.State > 5)
            {
                object pending = NewPending();
                object? result = _resetPending.Invoke(_pendingXfers, [pending]);
                _logger.LogInformation("Pending transfers reset: {ReturnCode}, count {Count}", result, CountOf(pending));
            }

            if (_session.State > 5)
            {
                _setState.Invoke(_session, [5]);
            }

            if (_session.State >= 5)
            {
                _disableSource.Invoke(_session, null);
            }
        }
        catch (Exception ex)
        {
            _onFailure(Unwrap(ex));
        }
    }

    private bool TakeOver()
    {
        object? hook = typeof(TwainSession).GetField("_msgLoopHook", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_session);
        if (hook is IMessageFilter current)
        {
            Application.RemoveMessageFilter(current);
            _replaced = current;
        }

        try
        {
            Application.AddMessageFilter(this);
            _installed = true;
            _logger.LogInformation("Each page is taken after the scanner's notification returns, and the next page is not requested until that call returns to the message loop");
            return true;
        }
        catch
        {
            if (_replaced is not null)
            {
                Application.AddMessageFilter(_replaced);
                _replaced = null;
            }

            throw;
        }
    }

    private bool TryConsume(ref Message message, out TwainMessage twainMessage)
    {
        twainMessage = TwainMessage.Null;
        var native = new NativeMessage
        {
            Hwnd = message.HWnd,
            Message = (uint)message.Msg,
            WParam = message.WParam,
            LParam = message.LParam,
        };
        int size = Marshal.SizeOf<NativeMessage>();
        IntPtr pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(native, pointer, false);
            var twainEvent = new TWEvent { pEvent = pointer };
            object? code = _processEvent.Invoke(_event, [twainEvent]);
            if (code is not ReturnCode.DSEvent)
            {
                return false;
            }

            twainMessage = twainEvent.TWMessage;
            return twainMessage != TwainMessage.Null;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static bool TryBind(TwainSession session, out Bound bound)
    {
        bound = default;
        object? control = typeof(TwainSession).GetProperty("DGControl", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session);
        object? evt = control?.GetType().GetProperty("Event", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(control);
        object? pending = control?.GetType().GetProperty("PendingXfers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(control);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo? process = evt?.GetType().GetMethod("ProcessEvent", instance);
        MethodInfo? handle = typeof(TwainSession).GetMethod("HandleSourceMsg", instance);
        MethodInfo? state = typeof(TwainSession).GetProperty("State", instance)?.GetSetMethod(nonPublic: true);
        MethodInfo? endXfer = pending?.GetType().GetMethod("EndXfer", instance);
        MethodInfo? getPending = pending?.GetType().GetMethod("Get", instance);
        MethodInfo? resetPending = pending?.GetType().GetMethod("Reset", instance);
        MethodInfo? disable = DisableSource(session);
        Type? logic = typeof(TwainSession).Assembly.GetType("NTwain.Internals.TransferLogic");
        var transfers = new Dictionary<XferMech, MethodInfo>();
        foreach ((XferMech mechanism, string name) in new[]
        {
            (XferMech.Native, "DoImageNativeXfer"),
            (XferMech.Memory, "DoImageMemoryXfer"),
            (XferMech.File, "DoImageFileXfer"),
            (XferMech.MemFile, "DoImageMemoryFileXfer"),
        })
        {
            MethodInfo? method = logic?.GetMethod(name, stat);
            if (method is null)
            {
                return false;
            }

            transfers.Add(mechanism, method);
        }

        if (evt is null || pending is null || process is null || handle is null || state is null || endXfer is null || getPending is null || resetPending is null || disable is null)
        {
            return false;
        }

        bound = new Bound(evt, pending, process, handle, state, endXfer, getPending, resetPending, disable, transfers);
        return true;
    }

    private static MethodInfo? DisableSource(TwainSession session)
    {
        Type? iface = session.GetType().Assembly.GetType("NTwain.Internals.ITwainSessionInternal");
        if (iface is null)
        {
            return null;
        }

        InterfaceMapping map = session.GetType().GetInterfaceMap(iface);
        for (int i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i].Name == "DisableSource")
            {
                return map.TargetMethods[i];
            }
        }

        return null;
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    private readonly struct Bound
    {
        public Bound(object twainEvent, object pendingXfers, MethodInfo processEvent, MethodInfo handleSourceMessage, MethodInfo setState, MethodInfo endXfer, MethodInfo getPending, MethodInfo resetPending, MethodInfo disableSource, Dictionary<XferMech, MethodInfo> imageTransfers)
        {
            TwainEvent = twainEvent;
            PendingXfers = pendingXfers;
            ProcessEvent = processEvent;
            HandleSourceMessage = handleSourceMessage;
            SetState = setState;
            EndXfer = endXfer;
            GetPending = getPending;
            ResetPending = resetPending;
            DisableSource = disableSource;
            ImageTransfers = imageTransfers;
        }

        public object TwainEvent { get; }
        public object PendingXfers { get; }
        public MethodInfo ProcessEvent { get; }
        public MethodInfo HandleSourceMessage { get; }
        public MethodInfo SetState { get; }
        public MethodInfo EndXfer { get; }
        public MethodInfo GetPending { get; }
        public MethodInfo ResetPending { get; }
        public MethodInfo DisableSource { get; }
        public Dictionary<XferMech, MethodInfo> ImageTransfers { get; }
    }
}
