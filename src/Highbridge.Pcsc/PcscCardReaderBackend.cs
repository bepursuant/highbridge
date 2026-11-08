using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Highbridge.Cards;

namespace Highbridge.Pcsc;

/// <summary>
/// Reads a payment card through PC/SC. The page names a reader, an interface, an optional AID, and
/// how long to wait. Every command below is chosen here. GET PROCESSING OPTIONS runs once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PcscCardReaderBackend : ICardReaderBackend
{
    private readonly ILogger _logger;

    public PcscCardReaderBackend(ILogger<PcscCardReaderBackend>? logger = null)
    {
        _logger = logger ?? NullLogger<PcscCardReaderBackend>.Instance;
    }

    public string Name => "pcsc";

    public Task<IReadOnlyList<CardReaderInfo>> ListReadersAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<CardReaderInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IntPtr context = Winscard.Establish();
            try
            {
                return Winscard.ListReaders(context).Select(name => new CardReaderInfo(name, name, Name)).ToArray();
            }
            finally
            {
                Winscard.Release(context);
            }
        }, cancellationToken);

    public Task<CardReadResult> ReadAsync(CardReadOptions options, CancellationToken cancellationToken) =>
        Task.Run(() => Read(options, cancellationToken), cancellationToken);

    public Task WaitForCardRemovalAsync(CardReadOptions options, CancellationToken cancellationToken)
    {
        if (!options.WaitForRemoval)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() => WaitForRemoval(options, cancellationToken), cancellationToken);
    }

    private void WaitForRemoval(CardReadOptions options, CancellationToken cancellationToken)
    {
        IntPtr context = Winscard.Establish();
        try
        {
            IReadOnlyList<string> readers = Winscard.ListReaders(context);
            IReadOnlyList<string>? watched = CardSlots.WatchList(readers, options.ReaderId);
            if (watched is null)
            {
                throw CardReaderException.ReaderNotFound();
            }

            if (watched.Count == 0)
            {
                throw CardReaderException.NoReaders();
            }

            // The reader name is a device name. The card number is not written here.
            _logger.LogInformation("Waiting for the card to leave {Reader}", string.IsNullOrWhiteSpace(options.ReaderId) ? "(any)" : options.ReaderId);
            Winscard.WaitUntilGone(context, watched, cancellationToken);
        }
        finally
        {
            Winscard.Release(context);
        }
    }

    private CardReadResult Read(CardReadOptions options, CancellationToken cancellationToken)
    {
        string logged = string.IsNullOrWhiteSpace(options.ReaderId) ? "(default)" : options.ReaderId;
        IntPtr context = IntPtr.Zero;
        bool reported = false;
        try
        {
            context = Winscard.Establish();
            IReadOnlyList<string> readers = Winscard.ListReaders(context);
            IReadOnlyList<string>? watched = CardSlots.WatchList(readers, options.ReaderId);
            if (watched is null)
            {
                throw CardReaderException.ReaderNotFound();
            }

            if (watched.Count == 0)
            {
                throw CardReaderException.NoReaders();
            }

            var (reader, atr) = Winscard.WaitForCard(context, watched, options.Interface, options.ReaderId, options.WaitForCardMs, cancellationToken);
            var order = new List<string> { reader };
            foreach (string other in watched)
            {
                if (!order.Contains(other, StringComparer.OrdinalIgnoreCase))
                {
                    order.Add(other);
                }
            }

            CardReaderException? failure = null;
            for (int i = 0; i < order.Count; i++)
            {
                string candidate = order[i];
                logged = candidate;
                IntPtr card = IntPtr.Zero;
                bool transaction = false;
                try
                {
                    var (connected, protocol) = Winscard.Connect(context, candidate);
                    card = connected;
                    transaction = Winscard.TryBegin(card);
                    if (!transaction)
                    {
                        _logger.LogWarning("Card read on {Reader} is continuing without an exclusive transaction", candidate);
                    }

                    var result = Exchange(card, ref protocol, candidate, candidate == reader ? atr : [], options);
                    _logger.LogInformation(
                        "Card read on {Reader} via {Interface} selected {Aid}",
                        result.Reader.Name,
                        result.Interface,
                        result.Selected.Aid);
                    return result;
                }
                catch (Exception ex) when (ex is CardReaderException or EmvFormatException)
                {
                    var mapped = ex as CardReaderException ?? CardReaderException.ReadFailed();
                    failure ??= mapped;
                    reported = true;
                    _logger.LogWarning("Card read on {Reader} failed: {Code} {Pcsc}", candidate, mapped.Code, mapped.PcscCode is uint pcsc ? $"0x{pcsc:X8}" : "");
                    if (i == order.Count - 1 || !AnotherSlot(mapped))
                    {
                        // An empty second slot should not hide the failure from the slot that had a card.
                        throw mapped.Code == "no_card" && failure.Code != "no_card" ? failure : mapped;
                    }
                }
                finally
                {
                    if (transaction)
                    {
                        Winscard.End(card);
                    }

                    Winscard.Disconnect(card);
                }
            }

            throw failure ?? CardReaderException.ReadFailed();
        }
        catch (CardReaderException ex)
        {
            if (!reported)
            {
                _logger.LogWarning("Card read on {Reader} failed: {Code} {Pcsc}", logged, ex.Code, ex.PcscCode is uint pcsc ? $"0x{pcsc:X8}" : "");
            }

            throw;
        }
        catch (EmvFormatException)
        {
            _logger.LogWarning("Card read on {Reader} failed: {Code}", logged, "read_failed");
            throw CardReaderException.ReadFailed();
        }
        finally
        {
            Winscard.Release(context);
        }
    }

    /// <summary>
    /// The other slot is worth a connect when this one had no directory or no card.
    /// A Windows failure still stops here: retrying it on the other slot can send a second
    /// processing-options command to the same card.
    /// </summary>
    private static bool AnotherSlot(CardReaderException ex) =>
        ex.Code is "no_payment_apps" or "no_card" or "card_unresponsive" or "card_removed";

    private static CardReadResult Exchange(IntPtr card, ref uint protocol, string reader, byte[] atr, CardReadOptions options)
    {
        CardInterface mode = options.Interface;
        if (mode == CardInterface.Auto
            && !CardSlots.IsContactName(reader)
            && !CardSlots.IsContactlessName(reader)
            && !AtrSays(atr, "contactless")
            && AtrSays(atr, "contact"))
        {
            mode = CardInterface.Contact;
        }

        var attempts = Directories(mode, reader);
        byte[]? directory = null;
        CardInterface used = attempts[0].Interface;
        CardReaderException? transport = null;
        foreach (var attempt in attempts)
        {
            try
            {
                directory = TryDirectory(card, ref protocol, attempt.Name);
            }
            catch (CardReaderException ex) when (ex.Code == "read_failed")
            {
                // This directory name was rejected by the driver. The other name is a different command.
                transport = ex;
                directory = null;
            }

            if (directory is not null)
            {
                used = attempt.Interface;
                break;
            }
        }

        if (directory is null)
        {
            throw transport ?? CardReaderException.NoPaymentApps();
        }

        var apps = Emv.DirectoryEntries(directory);
        if (Emv.DirectorySfi(directory) is int sfi)
        {
            for (int record = 1; record <= 16; record++)
            {
                if (!TryResponse(card, ref protocol, Emv.ReadRecord(sfi, record), out var data, out ushort status))
                {
                    throw CardReaderException.ReadFailed();
                }

                if (status == 0x6A83)
                {
                    break;
                }

                if (status != 0x9000)
                {
                    throw CardReaderException.ReadFailed();
                }

                foreach (var extra in Emv.DirectoryEntries(data))
                {
                    if (apps.All(app => !app.Aid.Equals(extra.Aid, StringComparison.OrdinalIgnoreCase)))
                    {
                        apps.Add(extra);
                    }
                }
            }
        }

        if (apps.Count == 0)
        {
            throw CardReaderException.NoPaymentApps();
        }

        var chosen = Choose(card, ref protocol, apps, options.NormalizedAid);
        byte[] gpo = Emv.GetProcessingOptions(Emv.Pdol(chosen.Fci) ?? []);
        if (!TryResponse(card, ref protocol, gpo, out byte[] gpoData, out ushort gpoStatus) || gpoStatus != 0x9000)
        {
            throw CardReaderException.ReadFailed();
        }

        var blobs = new List<byte[]> { gpoData };
        foreach (var entry in Emv.ParseAfl(gpoData))
        {
            if (entry.Last - entry.First > 31)
            {
                throw CardReaderException.ReadFailed();
            }

            for (int record = entry.First; record <= entry.Last; record++)
            {
                if (!TryResponse(card, ref protocol, Emv.ReadRecord(entry.Sfi, record), out byte[] data, out ushort status) || status != 0x9000)
                {
                    throw CardReaderException.ReadFailed();
                }

                blobs.Add(data);
            }
        }

        var selected = Emv.MapSelection(chosen.App, blobs);
        return new CardReadResult(new CardReaderRef(reader, reader), used, apps, selected);
    }

    private static (CardApplication App, byte[] Fci) Choose(IntPtr card, ref uint protocol, List<CardApplication> apps, string? aid)
    {
        IEnumerable<CardApplication> order = apps
            .Select((app, index) => (app, index))
            .OrderBy(item => item.app.Priority ?? int.MaxValue)
            .ThenBy(item => item.index)
            .Select(item => item.app);
        if (aid is not null)
        {
            var match = order.FirstOrDefault(app => app.Aid.Equals(aid, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw CardReaderException.AppNotFound();
            }

            order = [match];
        }

        foreach (var app in order)
        {
            byte[] select = Emv.Select(Convert.FromHexString(app.Aid));
            if (!TryResponse(card, ref protocol, select, out byte[] data, out ushort status))
            {
                throw CardReaderException.ReadFailed();
            }

            if (status == 0x6700 && select.Length > 5 && TryResponse(card, ref protocol, select[..^1], out byte[] retry, out ushort retryStatus))
            {
                data = retry;
                status = retryStatus;
            }

            if (status is 0x6A82 or 0x6A81)
            {
                continue;
            }

            if (status != 0x9000)
            {
                throw CardReaderException.ReadFailed();
            }

            return (app, data);
        }

        throw CardReaderException.AppNotFound();
    }

    private static byte[]? TryDirectory(IntPtr card, ref uint protocol, byte[] name)
    {
        byte[] command = Emv.Select(name);
        if (!TryDirectoryResponse(card, ref protocol, command, out byte[] data, out ushort status, out CardReaderException? failure)
            && (command.Length <= 5 || !TryDirectoryResponse(card, ref protocol, command[..^1], out data, out status, out failure)))
        {
            throw failure ?? CardReaderException.ReadFailed();
        }

        // Some contactless cards reject the expected-length byte. Ask again without it.
        if (status == 0x6700 && command.Length > 5
            && TryDirectoryResponse(card, ref protocol, command[..^1], out byte[] retry, out ushort retryStatus, out _))
        {
            data = retry;
            status = retryStatus;
        }

        // Any other answer means this directory is not on the card. The caller tries the other one.
        // A Windows failure was already thrown above, so a tap that the driver rejects is not hidden.
        if (status != 0x9000)
        {
            return null;
        }

        try
        {
            return Emv.DirectoryEntries(data).Count > 0 || Emv.DirectorySfi(data) is not null ? data : null;
        }
        catch (EmvFormatException)
        {
            return null;
        }
    }

    private static bool TryDirectoryResponse(
        IntPtr card,
        ref uint protocol,
        byte[] command,
        out byte[] data,
        out ushort status,
        out CardReaderException? failure)
    {
        try
        {
            failure = null;
            if (TryResponse(card, ref protocol, command, out data, out status))
            {
                return true;
            }

            data = [];
            status = 0;
            failure = CardReaderException.ReadFailed();
            return false;
        }
        catch (CardReaderException ex) when (ex.Code == "read_failed")
        {
            data = [];
            status = 0;
            failure = ex;
            return false;
        }
    }

    private static bool TryResponse(IntPtr card, ref uint protocol, byte[] command, out byte[] data, out ushort status)
    {
        byte[] response = Winscard.Transmit(card, ref protocol, command);
        return Emv.TrySplit(response, out data, out status);
    }

    private static List<(CardInterface Interface, byte[] Name)> Directories(CardInterface requested, string reader)
    {
        (CardInterface Interface, byte[] Name) contact = (CardInterface.Contact, Emv.PseName);
        (CardInterface Interface, byte[] Name) contactless = (CardInterface.Contactless, Emv.PpseName);
        return CardSlots.DirectoryOrder(requested, reader)[0] == CardInterface.Contactless
            ? [contactless, contact]
            : [contact, contactless];
    }

    private static bool AtrSays(byte[] atr, string word) =>
        atr.Length > 0 && Encoding.ASCII.GetString(atr).Contains(word, StringComparison.OrdinalIgnoreCase);
}
