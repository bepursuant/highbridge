using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Highbridge.Hosting;

/// <summary>
/// Forwards command-line arguments (usually a highbridge:// URL) from a second launch to the
/// running instance over a named pipe restricted to the current user.
/// </summary>
public sealed class SingleInstanceChannel : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _serverLoop;
    private int _disposed;

    public SingleInstanceChannel(string? pipeName = null)
    {
        _pipeName = pipeName ?? DefaultPipeName;
    }

    public static string DefaultPipeName
    {
        get
        {
            string user = new(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());
            return $"highbridge-{user}-v1";
        }
    }

    public static async Task<bool> TrySendAsync(string[] args, string? pipeName = null, TimeSpan? timeout = null)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args) + "\n");
            await client.WriteAsync(payload, cts.Token).ConfigureAwait(false);
            await client.FlushAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Start(Func<string[], Task> onMessage)
    {
        if (_serverLoop is not null)
        {
            throw new InvalidOperationException("Already started.");
        }

        _serverLoop = Task.Run(() => ServeAsync(onMessage, _stop.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_serverLoop is not null)
        {
            try
            {
                await _serverLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task ServeAsync(Func<string[], Task> onMessage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is { Length: > 0 and < 16384 } && JsonSerializer.Deserialize<string[]>(line) is { } args)
                {
                    await onMessage(args).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A misbehaving client must not take the listener down.
            }
        }
    }
}
