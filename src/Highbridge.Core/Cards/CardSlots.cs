namespace Highbridge.Cards;

/// <summary>
/// Windows shows the chip slot and the tap slot of one reader as two PC/SC names. These helpers
/// decide which names belong together and which payment directory to ask for first.
/// </summary>
internal static class CardSlots
{
    private static readonly string[] InterfaceWords = ["contactless", "picc", "nfc", "contact", "icc", "cl"];

    internal static bool IsSam(string name) => Words(name).Contains("sam");

    internal static bool IsContactlessName(string name)
    {
        var words = Words(name);
        return words.Contains("contactless") || words.Contains("picc") || words.Contains("nfc") || words.Contains("cl");
    }

    internal static bool IsContactName(string name)
    {
        if (IsContactlessName(name))
        {
            return false;
        }

        var words = Words(name);
        return words.Contains("contact") || words.Contains("icc");
    }

    /// <summary>
    /// Readers to watch. An empty <paramref name="readerId"/> watches every payment reader.
    /// A named reader also watches the other slot of the same device. A SAM is watched only when
    /// it is the reader that was asked for. Returns null when <paramref name="readerId"/> is not listed.
    /// </summary>
    internal static IReadOnlyList<string>? WatchList(IReadOnlyList<string> readers, string? readerId)
    {
        if (readers.Count == 0)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(readerId))
        {
            var payment = readers.Where(reader => !IsSam(reader)).ToList();
            return payment.Count > 0 ? payment : readers.ToList();
        }

        string? selected = readers.FirstOrDefault(reader => reader.Equals(readerId, StringComparison.Ordinal))
            ?? readers.FirstOrDefault(reader => reader.Equals(readerId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return null;
        }

        if (IsSam(selected))
        {
            return [selected];
        }

        string family = FamilyKey(selected);
        var siblings = readers.Where(reader => !IsSam(reader) && FamilyKey(reader) == family).ToList();
        return siblings.Count > 0 ? siblings : [selected];
    }

    /// <summary>
    /// Which present slot to connect to. Contact and contactless prefer that slot when both have a
    /// card. Auto uses the requested reader when that slot has a card, otherwise a tap slot.
    /// </summary>
    internal static string ChoosePresent(IReadOnlyList<string> present, CardInterface iface, string? requestedId)
    {
        if (present.Count == 0)
        {
            throw new ArgumentException("At least one reader must have a card.", nameof(present));
        }

        if (iface == CardInterface.Auto && !string.IsNullOrWhiteSpace(requestedId))
        {
            string? picked = present.FirstOrDefault(name => name.Equals(requestedId, StringComparison.OrdinalIgnoreCase));
            if (picked is not null)
            {
                return picked;
            }
        }

        IEnumerable<string> preferred = iface switch
        {
            CardInterface.Contact => present.Where(IsContactName),
            CardInterface.Contactless => present.Where(IsContactlessName),
            _ => present.Where(IsContactlessName),
        };
        return preferred.Concat(present).First();
    }

    /// <summary>
    /// PSE (contact) and PPSE (contactless), preferred directory first. The other is always second.
    /// </summary>
    internal static CardInterface[] DirectoryOrder(CardInterface requested, string reader)
    {
        bool contactlessFirst = requested switch
        {
            CardInterface.Contact => false,
            CardInterface.Contactless => true,
            _ => !IsContactName(reader),
        };
        return contactlessFirst
            ? [CardInterface.Contactless, CardInterface.Contact]
            : [CardInterface.Contact, CardInterface.Contactless];
    }

    private static string FamilyKey(string name) =>
        string.Join(' ', Words(name).Where(word => !InterfaceWords.Contains(word)));

    private static List<string> Words(string name)
    {
        var words = new List<string>();
        var current = new List<char>();
        foreach (char ch in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                current.Add(ch);
                continue;
            }

            if (current.Count > 0)
            {
                words.Add(new string(current.ToArray()));
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            words.Add(new string(current.ToArray()));
        }

        return words;
    }
}
