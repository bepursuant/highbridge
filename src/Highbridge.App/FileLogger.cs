using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using Highbridge.Support;

namespace Highbridge.App;

/// <summary>
/// Append-only support log in %LOCALAPPDATA%\Highbridge\logs. Rolls at 5 MB and keeps three
/// older files. Every line is redacted before it is written.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int KeptArchives = 3;
    private readonly object _gate = new();

    public FileLoggerProvider(string directory)
    {
        Directory.CreateDirectory(directory);
        LogDirectory = directory;
        FilePath = Path.Combine(directory, "highbridge.log");
    }

    public string LogDirectory { get; }

    public string FilePath { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    Roll();
                }

                File.AppendAllText(FilePath, line);
            }
            catch (IOException)
            {
                // Logging must never break a scan or a card read.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Roll()
    {
        for (int index = KeptArchives - 1; index >= 1; index--)
        {
            string from = FilePath + "." + index;
            string to = FilePath + "." + (index + 1);
            if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
            }
        }

        File.Move(FilePath, FilePath + ".1", overwrite: true);
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string time = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            string message = formatter(state, exception);
            string detail = exception is null ? string.Empty : Environment.NewLine + exception;
            string line = LogRedactor.Redact($"{time} [{logLevel}] {category}: {message}{detail}") + Environment.NewLine;
            provider.Write(line);
        }
    }
}
