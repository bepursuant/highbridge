using System.Globalization;
using Microsoft.Extensions.Logging;
using NTwain;

namespace Highbridge.Twain;

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
