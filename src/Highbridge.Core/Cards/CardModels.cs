using System.Text.Json.Serialization;

namespace Highbridge.Cards;

[JsonConverter(typeof(CamelCaseEnumConverter<CardInterface>))]
public enum CardInterface
{
    Auto,
    Contact,
    Contactless,
}

/// <summary>
/// What a page may ask for when reading a card. There is no APDU, class, instruction, or data
/// field: unknown members are rejected. The bridge chooses every command.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CardReadOptions
{
    public const int DefaultWaitForCardMs = 30_000;
    public const int MaxWaitForCardMs = 120_000;

    /// <summary>Reader id from <c>/v1/card-readers</c>; null means the default reader.</summary>
    public string? ReaderId { get; init; }

    public CardInterface Interface { get; init; } = CardInterface.Auto;

    /// <summary>Optional hex AID, 5–16 bytes. Otherwise the highest-priority directory entry that selects.</summary>
    public string? Aid { get; init; }

    public int WaitForCardMs { get; init; } = DefaultWaitForCardMs;

    /// <summary>Uppercase hex AID with spaces removed, or null when <see cref="Aid"/> is absent.</summary>
    public string? NormalizedAid =>
        string.IsNullOrWhiteSpace(Aid) ? null : Aid.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ReaderId is { Length: > 256 })
        {
            errors.Add("readerId is too long.");
        }

        if (!Enum.IsDefined(Interface))
        {
            errors.Add("interface must be auto, contact, or contactless.");
        }

        if (WaitForCardMs is < 0 or > MaxWaitForCardMs)
        {
            errors.Add($"waitForCardMs must be between 0 and {MaxWaitForCardMs}.");
        }

        if (Aid is not null)
        {
            string hex = Aid.Replace(" ", "", StringComparison.Ordinal);
            if (hex.Length % 2 != 0 || hex.Length is < 10 or > 32 || !hex.All(Uri.IsHexDigit))
            {
                errors.Add("aid must be 5-16 bytes of hex.");
            }
        }

        return errors;
    }
}

public sealed record CardReaderInfo(string Id, string Name, string Backend);

public sealed record CardReaderRef(string Id, string Name);

/// <summary>One payment application from the card directory. Optional fields are omitted when absent.</summary>
public sealed record CardApplication(string Aid, string? Label, string? PreferredName, int? Priority);

/// <summary>
/// The one application this read selected. <see cref="PanLast4"/> is four digits, never the full
/// number. <see cref="Expiry"/> is <c>YYYY-MM</c>.
/// </summary>
public sealed record CardSelection(
    string Aid,
    string? Label,
    string? PreferredName,
    int? Priority,
    string PanLast4,
    string Expiry,
    string? CardholderName);

public sealed record CardReadResult(
    CardReaderRef Reader,
    CardInterface Interface,
    IReadOnlyList<CardApplication> Applications,
    CardSelection Selected);

/// <summary>A reader-side failure with a message that is safe to show to the web page.</summary>
public sealed class CardReaderException : Exception
{
    public CardReaderException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }

    /// <summary>Windows PC/SC code, when this failure came from winscard. Never shown to the page.</summary>
    internal uint? PcscCode { get; set; }

    public static CardReaderException NoReaders() => new("no_readers", "No card readers were found.");

    public static CardReaderException ReaderNotFound() => new("reader_not_found", "That card reader was not found.");

    public static CardReaderException NoCard() => new("no_card", "No card is in the reader.");

    public static CardReaderException CardRemoved() => new("card_removed", "The card was removed.");

    public static CardReaderException CardUnresponsive() => new("card_unresponsive", "The card did not answer.");

    public static CardReaderException NoPaymentApps() => new("no_payment_apps", "The card has no payment applications.");

    public static CardReaderException AppNotFound() => new("app_not_found", "The requested application is not on the card.");

    public static CardReaderException ReadFailed() => new("read_failed", "The card could not be read.");

    public static CardReaderException PcscUnavailable(Exception? inner = null) =>
        new("pcsc_unavailable", "PC/SC is not available on this computer.", inner);
}

public interface ICardReaderBackend
{
    /// <summary>Short identifier such as "pcsc" or "mock".</summary>
    string Name { get; }

    Task<IReadOnlyList<CardReaderInfo>> ListReadersAsync(CancellationToken cancellationToken);

    /// <summary>Reads one card. Throws <see cref="CardReaderException"/> with a page-safe message.</summary>
    Task<CardReadResult> ReadAsync(CardReadOptions options, CancellationToken cancellationToken);
}
