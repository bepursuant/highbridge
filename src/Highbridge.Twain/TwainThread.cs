using System.Runtime.Versioning;
using System.Windows.Forms;

namespace Highbridge.Twain;

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
        var ready = new TaskCompletionSource<TwainWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        TwainWindow created = ready.Task.GetAwaiter().GetResult();
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

    private static void Run(TaskCompletionSource<TwainWindow> ready)
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

        ready.SetResult(new TwainWindow(window, handle));
        Application.Run();
        window.Dispose();
    }

    private readonly record struct TwainWindow(Form Form, IntPtr Handle);
}
