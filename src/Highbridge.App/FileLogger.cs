using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Highbridge.App;

/// <summary>Append-only log in %LOCALAPPDATA%\Highbridge\logs. Rolls over at 5 MB.</summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 5 * 1024 * 1024;
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
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                }

                File.AppendAllText(FilePath, line);
            }
            catch (IOException)
            {
                // Logging must never break scanning.
            }
        }
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
            string line = $"{time} [{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? string.Empty : Environment.NewLine + exception)}{Environment.NewLine}";
            provider.Write(line);
        }
    }
}
