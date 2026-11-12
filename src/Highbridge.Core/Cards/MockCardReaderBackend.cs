namespace Highbridge.Cards;

/// <summary>
/// Fake reader for development, CI, and demos. One reader returns a fixed two-application card.
/// Two others fail with <c>no_card</c> and <c>no_payment_apps</c>.
/// </summary>
public sealed class MockCardReaderBackend : ICardReaderBackend
{
    public const string SuccessId = "mock-reader";
    public const string NoCardId = "mock-no-card";
    public const string EmptyId = "mock-empty";

    public const string VisaAid = "A0000000031010";
    public const string MastercardAid = "A0000000041010";

    /// <summary>Test numbers only. A new selection is built per read so wiping one job cannot clear the next.</summary>
    public const string VisaPan = "4111111111111234";

    public const string MastercardPan = "5555555555555678";

    private static readonly CardApplication Visa = new(VisaAid, "VISA CREDIT", "Visa", 1);
    private static readonly CardApplication Mastercard = new(MastercardAid, "MASTERCARD", null, 2);

    private static readonly IReadOnlyList<CardReaderInfo> Readers =
    [
        new(SuccessId, "highbridge mock card reader", "mock"),
        new(NoCardId, "highbridge mock reader with no card", "mock"),
        new(EmptyId, "highbridge mock reader with no payment apps", "mock"),
    ];

    /// <summary>
    /// Whether a card is sitting in the mock slot. A read with <see cref="CardReadOptions.WaitForRemoval"/>
    /// waits until this is false. One-shot reads do not look at it.
    /// </summary>
    public bool CardPresent
    {
        get => Volatile.Read(ref _present) != 0;
        set => Volatile.Write(ref _present, value ? 1 : 0);
    }

    private int _present = 1;

    public string Name => "mock";

    public Task<IReadOnlyList<CardReaderInfo>> ListReadersAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Readers);

    public Task<CardReadResult> ReadAsync(CardReadOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string id = string.IsNullOrWhiteSpace(options.ReaderId) ? SuccessId : options.ReaderId;
        var reader = Readers.FirstOrDefault(r => r.Id == id) ?? throw CardReaderException.ReaderNotFound();
        if (reader.Id == NoCardId)
        {
            throw CardReaderException.NoCard();
        }

        if (reader.Id == EmptyId)
        {
            throw CardReaderException.NoPaymentApps();
        }

        CardApplication[] apps = [Visa, Mastercard];
        CardSelection selected = new(VisaAid, "VISA CREDIT", "Visa", 1, VisaPan, "2028-12", "DOE/JOHN");
        if (options.NormalizedAid is { } aid)
        {
            if (aid.Equals(MastercardAid, StringComparison.OrdinalIgnoreCase))
            {
                selected = new(MastercardAid, "MASTERCARD", null, 2, MastercardPan, "2027-06", "DOE/JANE");
            }
            else if (!aid.Equals(VisaAid, StringComparison.OrdinalIgnoreCase))
            {
                throw CardReaderException.AppNotFound();
            }
        }

        var iface = options.Interface == CardInterface.Contactless ? CardInterface.Contactless : CardInterface.Contact;
        return Task.FromResult(new CardReadResult(new CardReaderRef(reader.Id, reader.Name), iface, apps, selected));
    }

    public async Task WaitForCardRemovalAsync(CardReadOptions options, CancellationToken cancellationToken)
    {
        if (!options.WaitForRemoval)
        {
            return;
        }

        string id = string.IsNullOrWhiteSpace(options.ReaderId) ? SuccessId : options.ReaderId;
        if (Readers.All(reader => reader.Id != id))
        {
            throw CardReaderException.ReaderNotFound();
        }

        if (id == NoCardId)
        {
            return;
        }

        while (CardPresent)
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }
    }
}
