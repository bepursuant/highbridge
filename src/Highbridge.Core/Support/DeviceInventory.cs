using System.Globalization;
using System.Text;
using Highbridge.Cards;
using Highbridge.Scanning;

namespace Highbridge.Support;

/// <summary>Scanners and card readers as the tray window and the support log show them.</summary>
public sealed record SupportSnapshot(
    string Version,
    int Port,
    string ScannerBackend,
    string ReaderBackend,
    bool CardReaderEnabled,
    IReadOnlyList<ScannerInfo> Scanners,
    IReadOnlyList<CardReaderInfo> Readers,
    string? ScannerError,
    string? ReaderError,
    DateTimeOffset At,
    bool ScannersListed = true);

public static class DeviceInventory
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public static async Task<SupportSnapshot> CollectAsync(
        IScannerBackend scanners,
        ICardReaderBackend readers,
        string version,
        int port,
        bool cardReaderEnabled,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        TimeProvider? time = null,
        bool listScanners = true)
    {
        var limit = timeout ?? DefaultTimeout;
        var clock = time ?? TimeProvider.System;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scannerTask = listScanners
            ? ListScanners(scanners, timeoutSource.Token, cancellationToken)
            : Task.FromResult<(IReadOnlyList<ScannerInfo> Items, string? Error)>(([], null));
        var readerTask = ListReaders(readers, timeoutSource.Token, cancellationToken);
        try
        {
            await Task.WhenAll(scannerTask, readerTask).WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await timeoutSource.CancelAsync().ConfigureAwait(false);
        }

        var (foundScanners, scannerError) = scannerTask.IsCompletedSuccessfully
            ? scannerTask.Result
            : ([], "The scanner list did not finish in time.");
        var (foundReaders, readerError) = readerTask.IsCompletedSuccessfully
            ? readerTask.Result
            : ([], "The card reader list did not finish in time.");
        return new SupportSnapshot(
            version,
            port,
            scanners.Name,
            readers.Name,
            cardReaderEnabled,
            foundScanners,
            foundReaders,
            scannerError,
            readerError,
            clock.GetLocalNow(),
            listScanners);
    }

    private static async Task<(IReadOnlyList<ScannerInfo> Items, string? Error)> ListScanners(
        IScannerBackend backend,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        try
        {
            return (await backend.ListScannersAsync(timeoutToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ([], "The scanner list did not finish in time.");
        }
        catch (Exception ex)
        {
            return ([], LogRedactor.Redact(string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message));
        }
    }

    private static async Task<(IReadOnlyList<CardReaderInfo> Items, string? Error)> ListReaders(
        ICardReaderBackend backend,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        try
        {
            return (await backend.ListReadersAsync(timeoutToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ([], "The card reader list did not finish in time.");
        }
        catch (Exception ex)
        {
            return ([], LogRedactor.Redact(string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message));
        }
    }
}

public static class SupportReport
{
    public static string Format(SupportSnapshot snapshot)
    {
        var text = new StringBuilder();
        text.Append("highbridge ").Append(snapshot.Version);
        text.Append("  port ").Append(snapshot.Port.ToString(CultureInfo.InvariantCulture));
        text.AppendLine();
        text.Append("scanners (").Append(snapshot.ScannerBackend).Append(')');
        if (!snapshot.ScannersListed)
        {
            text.Append("  not listed");
        }

        AppendError(text, snapshot.ScannerError);
        text.AppendLine();
        if (snapshot.ScannersListed)
        {
            AppendScanners(text, snapshot.Scanners);
        }
        text.Append("card readers (").Append(snapshot.ReaderBackend).Append(", ");
        text.Append(snapshot.CardReaderEnabled ? "enabled" : "disabled by policy").Append(')');
        AppendError(text, snapshot.ReaderError);
        text.AppendLine();
        AppendReaders(text, snapshot.Readers);
        text.Append("listed ").Append(snapshot.At.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        text.AppendLine();
        return LogRedactor.Redact(text.ToString());
    }

    private static void AppendError(StringBuilder text, string? error)
    {
        if (error is not null)
        {
            text.Append("  error: ").Append(error);
        }
    }

    private static void AppendScanners(StringBuilder text, IReadOnlyList<ScannerInfo> scanners)
    {
        if (scanners.Count == 0)
        {
            text.AppendLine("  (none)");
            return;
        }

        foreach (var scanner in scanners.OrderByDescending(s => s.IsDefault).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            text.Append("  ").Append(scanner.IsDefault ? "default" : "       ");
            text.Append("  ").Append(scanner.Name);
            if (!string.IsNullOrWhiteSpace(scanner.Manufacturer))
            {
                text.Append("  ").Append(scanner.Manufacturer);
            }

            text.Append("  id=").Append(scanner.Id);
            text.AppendLine();
        }
    }

    private static void AppendReaders(StringBuilder text, IReadOnlyList<CardReaderInfo> readers)
    {
        if (readers.Count == 0)
        {
            text.AppendLine("  (none)");
            return;
        }

        foreach (var reader in readers.OrderBy(r => CardReaderSlot.Describe(r.Name), StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            text.Append("  ").Append(CardReaderSlot.Describe(reader.Name).PadRight(12));
            text.Append(' ').Append(reader.Name);
            text.Append("  id=").Append(reader.Id);
            text.AppendLine();
        }
    }
}
