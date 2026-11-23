using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using NTwain;
using TwainMessage = NTwain.Data.Message;

namespace Highbridge.Twain;

/// <summary>
/// NTwain transfers every page inside the window message that announced the first one. A WIA
/// driver sends that message and does not feed the next sheet until the send returns, so the
/// scan deadlocks after page one. This filter accepts the notification, lets it return, and only
/// then runs the transfer.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TwainTransferDeferral : IMessageFilter, IDisposable
{
    private const uint PmQsSendMessage = 0x00400000;

    private readonly TwainSession _session;
    private readonly TwainThread _thread;
    private readonly ILogger _logger;
    private readonly Action<Exception> _onFailure;
    private readonly object _event;
    private readonly MethodInfo _processEvent;
    private readonly MethodInfo _handleSourceMessage;
    private readonly MethodInfo _setState;
    private readonly MethodInfo _transfer;
    private IMessageFilter? _replaced;
    private bool _installed;
    private bool _queued;
    private bool _inTransfer;
    private int _disposed;

    private TwainTransferDeferral(TwainSession session, TwainThread thread, ILogger logger, Action<Exception> onFailure, object twainEvent, MethodInfo processEvent, MethodInfo handleSourceMessage, MethodInfo setState, MethodInfo transfer)
    {
        _session = session;
        _thread = thread;
        _logger = logger;
        _onFailure = onFailure;
        _event = twainEvent;
        _processEvent = processEvent;
        _handleSourceMessage = handleSourceMessage;
        _setState = setState;
        _transfer = transfer;
    }

    public static TwainTransferDeferral? TryInstall(TwainSession session, TwainThread thread, ILogger logger, Action<Exception> onFailure)
    {
        try
        {
            if (typeof(TwainSession).GetField("_callbackObj", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session) is not null)
            {
                logger.LogInformation("The source registered a TWAIN callback, which already returns before the transfer");
                return null;
            }

            if (!TryBind(session, out var twainEvent, out var processEvent, out var handleSourceMessage, out var setState, out var transfer))
            {
                logger.LogWarning("Could not bind NTwain's transfer entry points; pages will be pulled inside the scanner's notification");
                return null;
            }

            var deferral = new TwainTransferDeferral(session, thread, logger, onFailure, twainEvent!, processEvent!, handleSourceMessage!, setState!, transfer!);
            return deferral.TakeOver() ? deferral : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not defer TWAIN transfers; pages will be pulled inside the scanner's notification");
            return null;
        }
    }

    /// <summary>
    /// Delivers window messages another thread has sent to this one, without dispatching posted
    /// messages. A driver stuck in SendMessage is waiting for exactly that.
    /// </summary>
    public static void FlushSentMessages() => PeekMessage(out _, IntPtr.Zero, 0, 0, PmQsSendMessage);

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
        // A posted transfer, or the transfer itself, must reach the window. Consuming it here
        // would swallow the callback that runs the scan.
        if (_disposed != 0 || _queued || _inTransfer || _session.State < 5)
        {
            return false;
        }

        if (!TryConsume(ref message, out TwainMessage twainMessage))
        {
            return false;
        }

        if (twainMessage == TwainMessage.XferReady)
        {
            _queued = true;
            _logger.LogInformation("Transfer is ready. The scanner's notification will return before the page is taken");
            _thread.Post(RunTransfer);
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

    private void RunTransfer()
    {
        if (_disposed != 0 || _session.State < 5)
        {
            _queued = false;
            return;
        }

        _inTransfer = true;
        try
        {
            FlushSentMessages();
            if (_session.State < 6)
            {
                _setState.Invoke(_session, [6]);
            }

            _transfer.Invoke(null, [_session]);
        }
        catch (Exception ex)
        {
            Exception error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            _onFailure(error);
        }
        finally
        {
            _inTransfer = false;
            _queued = false;
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
            _logger.LogInformation("Page transfers will start after the scanner's notification returns");
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
            var twainEvent = new NTwain.Data.TWEvent { pEvent = pointer };
            object? code = _processEvent.Invoke(_event, [twainEvent]);
            if (code is not NTwain.Data.ReturnCode.DSEvent)
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

    private static bool TryBind(TwainSession session, out object? twainEvent, out MethodInfo? processEvent, out MethodInfo? handleSourceMessage, out MethodInfo? setState, out MethodInfo? transfer)
    {
        twainEvent = null;
        processEvent = null;
        handleSourceMessage = null;
        setState = null;
        transfer = null;

        object? control = typeof(TwainSession).GetProperty("DGControl", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session);
        object? evt = control?.GetType().GetProperty("Event", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(control);
        MethodInfo? process = evt?.GetType().GetMethod("ProcessEvent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? handle = typeof(TwainSession).GetMethod("HandleSourceMsg", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo? state = typeof(TwainSession).GetProperty("State", BindingFlags.Instance | BindingFlags.Public)?.GetSetMethod(nonPublic: true);
        MethodInfo? routine = typeof(TwainSession).Assembly.GetType("NTwain.Internals.TransferLogic")?.GetMethod("DoTransferRoutine", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (evt is null || process is null || handle is null || state is null || routine is null)
        {
            return false;
        }

        twainEvent = evt;
        processEvent = process;
        handleSourceMessage = handle;
        setState = state;
        transfer = routine;
        return true;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

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
}
