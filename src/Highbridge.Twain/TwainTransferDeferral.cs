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
/// One image per MSG_XFERREADY, taken after that notification has returned.
/// NTwain otherwise pulls every following image inside the first notification. WIA answers an
/// early pull with Failure / TWCC_BUMMER (the pending count stays at 32767, its "not done yet")
/// and a reset at that point aborts a sheet the scanner has already started. A failed pull is
/// not retried and the feeder is not reset; the next sheet waits for the next MSG_XFERREADY.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TwainTransferDeferral : IMessageFilter, IDisposable
{
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
    private readonly MethodInfo _disableSource;
    private readonly PropertyInfo _pendingCount;
    private readonly Dictionary<XferMech, MethodInfo> _transfers;
    private bool _installed;
    private bool _posted;
    private bool _busy;
    private bool _loggedClose;
    private int _disposed;

    private TwainTransferDeferral(TwainSession session, TwainThread thread, ILogger logger, Action<Exception> onFailure, Func<bool> preparePage, object twainEvent, object pendingXfers, MethodInfo processEvent, MethodInfo handleSourceMessage, MethodInfo setState, MethodInfo endXfer, MethodInfo disableSource, PropertyInfo pendingCount, Dictionary<XferMech, MethodInfo> transfers)
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
        _disableSource = disableSource;
        _pendingCount = pendingCount;
        _transfers = transfers;
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

            if (!TryBind(session, out var bound) || bound is null)
            {
                logger.LogWarning("Could not bind NTwain's transfer entry points; pages will be pulled inside the scanner's notification");
                return null;
            }

            var deferral = new TwainTransferDeferral(session, thread, logger, onFailure, preparePage, bound.Event, bound.PendingXfers, bound.ProcessEvent, bound.HandleSourceMessage, bound.SetState, bound.EndXfer, bound.DisableSource, bound.PendingCount, bound.Transfers);
            return deferral.TakeOver() ? deferral : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not defer TWAIN transfers; pages will be pulled inside the scanner's notification");
            return null;
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
        // While a transfer is posted or running, leave messages alone. Consuming MSG_XFERREADY
        // inside the image call makes WIA fail it, and MSG_CLOSEDSREQ in that gap is what made
        // DisableSource run in a loop before the first sheet.
        if (_disposed != 0 || _posted || _busy || _session.State < 5)
        {
            return false;
        }

        if (!TryConsume(ref message, out TwainMessage twainMessage))
        {
            return false;
        }

        if (twainMessage == TwainMessage.XferReady)
        {
            _posted = true;
            _logger.LogInformation("Transfer is ready. The image will be taken after this notification returns");
            _thread.Post(TransferOne);
            return true;
        }

        if (twainMessage is TwainMessage.CloseDSReq or TwainMessage.CloseDSOK)
        {
            if (!_loggedClose)
            {
                _loggedClose = true;
                _logger.LogInformation("The source sent {Message} (TWAIN state {State}). The feeder is left alone", twainMessage, _session.State);
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
        if (_disposed != 0 || _session.State < 5)
        {
            _posted = false;
            return;
        }

        _posted = false;
        _busy = true;
        try
        {
            if (!_preparePage())
            {
                return;
            }

            if (_session.State < 6)
            {
                _setState.Invoke(_session, [6]);
            }

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            ReturnCode transfer = TransferImage();
            double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
            if (transfer is not (ReturnCode.XferDone or ReturnCode.Success))
            {
                _logger.LogInformation("Image transfer returned {ReturnCode} in {Seconds:F1} s. Waiting for the scanner to offer the sheet", transfer, seconds);
                return;
            }

            int pending = EndImage(out ReturnCode end);
            _logger.LogInformation("Image transfer returned {ReturnCode} in {Seconds:F1} s; MSG_ENDXFER {EndCode}; pending count {Count}", transfer, seconds, end, pending);
            if (pending == 0)
            {
                Disable();
            }
            else
            {
                _logger.LogInformation("Pending count is {Count}. The next sheet waits for the next transfer-ready", pending);
            }
        }
        catch (Exception ex)
        {
            _onFailure(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
        }
        finally
        {
            _busy = false;
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

        if (!_transfers.TryGetValue(mechanism, out MethodInfo? transfer))
        {
            transfer = _transfers[XferMech.Native];
        }

        return transfer.Invoke(null, [_session]) is ReturnCode code ? code : ReturnCode.Failure;
    }

    private int EndImage(out ReturnCode code)
    {
        object pending = Activator.CreateInstance(_endXfer.GetParameters()[0].ParameterType)!;
        object? result = _endXfer.Invoke(_pendingXfers, [pending]);
        code = result is ReturnCode end ? end : ReturnCode.Failure;
        return (int)_pendingCount.GetValue(pending)!;
    }

    private void Disable()
    {
        try
        {
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
            _onFailure(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
        }
    }

    private bool TakeOver()
    {
        object? hook = typeof(TwainSession).GetField("_msgLoopHook", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_session);
        IMessageFilter? replaced = hook as IMessageFilter;
        if (replaced is not null)
        {
            Application.RemoveMessageFilter(replaced);
        }

        try
        {
            Application.AddMessageFilter(this);
            _installed = true;
            _logger.LogInformation("Each transfer-ready takes one image, after the notification returns. The next sheet waits for the next transfer-ready");
            return true;
        }
        catch
        {
            if (replaced is not null)
            {
                Application.AddMessageFilter(replaced);
            }

            throw;
        }
    }

    private bool TryConsume(ref Message message, out TwainMessage twainMessage)
    {
        twainMessage = TwainMessage.Null;
        var native = new NativeMessage { Hwnd = message.HWnd, Message = (uint)message.Msg, WParam = message.WParam, LParam = message.LParam };
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMessage>());
        try
        {
            Marshal.StructureToPtr(native, pointer, false);
            var twainEvent = new TWEvent { pEvent = pointer };
            if (_processEvent.Invoke(_event, [twainEvent]) is not ReturnCode.DSEvent || twainEvent.TWMessage == TwainMessage.Null)
            {
                return false;
            }

            twainMessage = twainEvent.TWMessage;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static bool TryBind(TwainSession session, out Bound? bound)
    {
        bound = null;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        object? control = typeof(TwainSession).GetProperty("DGControl", instance)?.GetValue(session);
        object? evt = control?.GetType().GetProperty("Event", instance)?.GetValue(control);
        object? pending = control?.GetType().GetProperty("PendingXfers", instance)?.GetValue(control);
        MethodInfo? process = evt?.GetType().GetMethod("ProcessEvent", instance);
        MethodInfo? handle = typeof(TwainSession).GetMethod("HandleSourceMsg", instance);
        MethodInfo? state = typeof(TwainSession).GetProperty("State", instance)?.GetSetMethod(nonPublic: true);
        MethodInfo? endXfer = pending?.GetType().GetMethod("EndXfer", instance);
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

        PropertyInfo? count = endXfer?.GetParameters()[0].ParameterType.GetProperty("Count");
        if (evt is null || pending is null || process is null || handle is null || state is null || endXfer is null || disable is null || count is null)
        {
            return false;
        }

        bound = new Bound(evt, pending, process, handle, state, endXfer, disable, count, transfers);
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

    private sealed record Bound(object Event, object PendingXfers, MethodInfo ProcessEvent, MethodInfo HandleSourceMessage, MethodInfo SetState, MethodInfo EndXfer, MethodInfo DisableSource, PropertyInfo PendingCount, Dictionary<XferMech, MethodInfo> Transfers);
}
