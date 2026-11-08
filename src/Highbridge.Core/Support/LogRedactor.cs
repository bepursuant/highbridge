using System.Text.RegularExpressions;

namespace Highbridge.Support;

/// <summary>
/// Last line of defense before a line is written to the support log. Card numbers and long hex
/// blobs (an ATR or a raw command) are replaced. An AID is at most 32 hex characters and a Windows
/// PC/SC code is 8, so those stay.
/// </summary>
public static partial class LogRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        string withoutTrack = Track().Replace(text, "[redacted-number]");
        return LongHex().Replace(Pan().Replace(withoutTrack, "[redacted-number]"), "[redacted-hex]");
    }

    // A digit run inside an AID such as A0000000031010 is not a card number. A track uses D as the separator.
    [GeneratedRegex(@"(?<![\dA-Fa-f])(?:\d[ -]?){12,18}\d(?![\dA-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex Pan();

    [GeneratedRegex(@"(?<![\dA-Fa-f])\d{13,19}[Dd]\d{2,}(?![\dA-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex Track();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{40,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongHex();
}
