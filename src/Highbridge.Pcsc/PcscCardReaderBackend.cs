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

    private CardReadResult Read(CardReadOptions options, CancellationToken cancellationToken)
    {
        string logged = string.IsNullOrWhiteSpace(options.ReaderId) ? "(default)" : options.ReaderId;
        IntPtr context = IntPtr.Zero;
        IntPtr card = IntPtr.Zero;
        bool transaction = false;
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
            logged = reader;
            var (connected, protocol) = Winscard.Connect(context, reader);
            card = connected;
            transaction = Winscard.TryBegin(card);
            if (!transaction)
            {
                _logger.LogWarning("Card read on {Reader} is continuing without an exclusive transaction", reader);
            }

            var result = Exchange(card, ref protocol, reader, atr, options);
            _logger.LogInformation(
                "Card read on {Reader} via {Interface} selected {Aid}",
                result.Reader.Name,
                result.Interface,
                result.Selected.Aid);
            return result;
        }
        catch (CardReaderException ex)
        {
            _logger.LogWarning("Card read on {Reader} failed: {Code} {Pcsc}", logged, ex.Code, ex.PcscCode is uint pcsc ? $"0x{pcsc:X8}" : "");
            throw;
        }
        catch (EmvFormatException)
        {
            _logger.LogWarning("Card read on {Reader} failed: {Code}", logged, "read_failed");
            throw CardReaderException.ReadFailed();
        }
        finally
        {
            if (transaction)
            {
                Winscard.End(card);
            }

            Winscard.Disconnect(card);
            Winscard.Release(context);
        }
    }

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
        foreach (var attempt in attempts)
        {
            directory = TryDirectory(card, ref protocol, attempt.Name);
            if (directory is not null)
            {
                used = attempt.Interface;
                break;
            }
        }

        if (directory is null)
        {
            throw CardReaderException.NoPaymentApps();
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
            if (!TryResponse(card, ref protocol, Emv.Select(Convert.FromHexString(app.Aid)), out byte[] data, out ushort status))
            {
                throw CardReaderException.ReadFailed();
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
        if (!TryResponse(card, ref protocol, command, out byte[] data, out ushort status))
        {
            throw CardReaderException.ReadFailed();
        }

        // Some contactless cards reject the expected-length byte. Ask again without it.
        if (status == 0x6700 && command.Length > 5 && TryResponse(card, ref protocol, command[..^1], out byte[] retry, out ushort retryStatus))
        {
            data = retry;
            status = retryStatus;
        }

        if (status is 0x6A82 or 0x6A81 || status != 0x9000)
        {
            return status is 0x6A82 or 0x6A81 ? null : throw CardReaderException.ReadFailed();
        }

        return Emv.DirectoryEntries(data).Count > 0 || Emv.DirectorySfi(data) is not null ? data : null;
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
