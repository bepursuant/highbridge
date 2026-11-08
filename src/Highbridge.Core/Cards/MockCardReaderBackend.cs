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

    private static readonly CardApplication Visa = new(VisaAid, "VISA CREDIT", "Visa", 1);
    private static readonly CardApplication Mastercard = new(MastercardAid, "MASTERCARD", null, 2);
    private static readonly CardSelection VisaSelected = new(VisaAid, "VISA CREDIT", "Visa", 1, "1234", "2028-12", "DOE/JOHN");
    private static readonly CardSelection MastercardSelected = new(MastercardAid, "MASTERCARD", null, 2, "5678", "2027-06", "DOE/JANE");

    private static readonly IReadOnlyList<CardReaderInfo> Readers =
    [
        new(SuccessId, "highbridge mock card reader", "mock"),
        new(NoCardId, "highbridge mock reader with no card", "mock"),
        new(EmptyId, "highbridge mock reader with no payment apps", "mock"),
    ];

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
        CardSelection selected = VisaSelected;
        if (options.NormalizedAid is { } aid)
        {
            if (aid.Equals(MastercardAid, StringComparison.OrdinalIgnoreCase))
            {
                selected = MastercardSelected;
            }
            else if (!aid.Equals(VisaAid, StringComparison.OrdinalIgnoreCase))
            {
                throw CardReaderException.AppNotFound();
            }
        }

        var iface = options.Interface == CardInterface.Contactless ? CardInterface.Contactless : CardInterface.Contact;
        return Task.FromResult(new CardReadResult(new CardReaderRef(reader.Id, reader.Name), iface, apps, selected));
    }
}
