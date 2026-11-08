namespace Highbridge.Cards;

/// <summary>A short label for a Windows reader name, for the tray window and the support log.</summary>
public static class CardReaderSlot
{
    public static string Describe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Other";
        }

        if (CardSlots.IsSam(name))
        {
            return "SAM";
        }

        if (CardSlots.IsContactlessName(name))
        {
            return "Contactless";
        }

        if (CardSlots.IsContactName(name))
        {
            return "Contact";
        }

        return "Other";
    }
}
