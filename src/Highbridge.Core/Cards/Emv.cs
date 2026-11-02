using System.Text;
using Great.EmvTags;

namespace Highbridge.Cards;

/// <summary>
/// EMV command building and allowlisted field mapping. TLV and PDOL structure come from
/// Great.EmvTags. Callers pass card bytes in and get allowlisted fields out. Nothing here returns
/// a full PAN, a track, a cryptogram, an ATR, or a status word.
/// </summary>
internal static class Emv
{
    internal static readonly byte[] PseName = "1PAY.SYS.DDF01"u8.ToArray();
    internal static readonly byte[] PpseName = "2PAY.SYS.DDF01"u8.ToArray();

    internal static byte[] Select(ReadOnlySpan<byte> name)
    {
        if (name.Length is < 5 or > 16)
        {
            throw new EmvFormatException();
        }

        var apdu = new byte[6 + name.Length];
        apdu[0] = 0x00;
        apdu[1] = 0xA4;
        apdu[2] = 0x04;
        apdu[3] = 0x00;
        apdu[4] = (byte)name.Length;
        name.CopyTo(apdu.AsSpan(5));
        return apdu;
    }

    internal static byte[] ReadRecord(int sfi, int record)
    {
        if (sfi is < 1 or > 30 || record is < 1 or > 255)
        {
            throw new EmvFormatException();
        }

        return [0x00, 0xB2, (byte)record, (byte)((sfi << 3) | 0x04), 0x00];
    }

    internal static byte[] GetResponse(byte le) => [0x00, 0xC0, 0x00, 0x00, le];

    /// <summary>GET PROCESSING OPTIONS. PDOL lengths come from Great.EmvTags; every value is zeros.</summary>
    internal static byte[] GetProcessingOptions(ReadOnlySpan<byte> pdol)
    {
        byte[] values = pdol.IsEmpty ? [] : ZeroFillDol(pdol);
        var data = new List<byte> { 0x83 };
        WriteLength(data, values.Length);
        data.AddRange(values);
        if (data.Count > 255)
        {
            throw new EmvFormatException();
        }

        var apdu = new byte[6 + data.Count];
        apdu[0] = 0x80;
        apdu[1] = 0xA8;
        apdu[4] = (byte)data.Count;
        data.CopyTo(apdu, 5);
        return apdu;
    }

    internal static byte[] WithLe(byte[] apdu, byte le)
    {
        if (apdu.Length < 5)
        {
            throw new EmvFormatException();
        }

        var copy = (byte[])apdu.Clone();
        copy[^1] = le;
        return copy;
    }

    internal static bool TrySplit(ReadOnlySpan<byte> response, out byte[] data, out ushort status)
    {
        data = [];
        status = 0;
        if (response.Length < 2)
        {
            return false;
        }

        status = (ushort)((response[^2] << 8) | response[^1]);
        data = response[..^2].ToArray();
        return true;
    }

    internal static List<CardApplication> DirectoryEntries(ReadOnlySpan<byte> data)
    {
        var apps = new List<CardApplication>();
        foreach (var entry in Parse(data).FindAll("61"))
        {
            var aid = entry.FindFirst("4F");
            if (aid?.Value.Bytes is not { Length: >= 5 and <= 16 } aidBytes)
            {
                continue;
            }

            apps.Add(new CardApplication(
                Convert.ToHexString(aidBytes),
                Text(entry.FindFirst("50")),
                Text(entry.FindFirst("9F12")),
                Priority(entry.FindFirst("87"))));
        }

        return apps;
    }

    internal static int? DirectorySfi(ReadOnlySpan<byte> data)
    {
        byte[]? value = Parse(data).FindFirst("88")?.Value.Bytes;
        if (value is not { Length: > 0 })
        {
            return null;
        }

        int sfi = value[0] & 0x1F;
        return sfi is >= 1 and <= 30 ? sfi : null;
    }

    internal static byte[]? Pdol(ReadOnlySpan<byte> fci)
    {
        byte[]? value = Parse(fci).FindFirst("9F38")?.Value.Bytes;
        return value is { Length: > 0 } ? value : null;
    }

    internal readonly record struct AflEntry(int Sfi, int First, int Last);

    internal static List<AflEntry> ParseAfl(ReadOnlySpan<byte> gpo)
    {
        var tags = Parse(gpo);
        byte[] afl;
        if (tags.Count == 1 && tags[0].Tag.Hex == "80")
        {
            byte[] value = tags[0].Value.Bytes;
            if (value.Length < 2 || (value.Length - 2) % 4 != 0)
            {
                throw new EmvFormatException();
            }

            afl = value[2..];
        }
        else
        {
            byte[]? value = tags.FindFirst("94")?.Value.Bytes;
            if (value is null || value.Length % 4 != 0)
            {
                throw new EmvFormatException();
            }

            afl = value;
        }

        var entries = new List<AflEntry>();
        for (int i = 0; i < afl.Length; i += 4)
        {
            int sfi = afl[i] >> 3;
            int first = afl[i + 1];
            int last = afl[i + 2];
            if (sfi is < 1 or > 30 || first == 0 || first > last)
            {
                throw new EmvFormatException();
            }

            entries.Add(new AflEntry(sfi, first, last));
        }

        return entries;
    }

    /// <summary>
    /// Maps directory fields plus record bytes onto the allowlist. Tag 5A wins over the PAN inside
    /// tag 57. Tag 5F24 wins over the expiry inside tag 57. Track bytes are not copied out.
    /// </summary>
    internal static CardSelection MapSelection(CardApplication app, IEnumerable<byte[]> blobs)
    {
        string? pan5A = null;
        string? pan57 = null;
        string? expiryDate = null;
        string? expiryTrack = null;
        string? name = null;
        string? label = app.Label;
        string? preferred = app.PreferredName;

        foreach (byte[] blob in blobs)
        {
            var tags = Parse(blob);
            if (tags.FindFirst("5A") is { } panTag)
            {
                pan5A = Last4(PanNibbles(panTag.Value.Bytes)) ?? pan5A;
            }

            if (tags.FindFirst("57") is { } track)
            {
                var (trackPan, trackExpiry) = Track2(track.Value.Bytes);
                pan57 ??= trackPan;
                expiryTrack ??= trackExpiry;
            }

            if (tags.FindFirst("5F24") is { } dated)
            {
                expiryDate = ExpiryDate(dated.Value.Bytes) ?? expiryDate;
            }

            name ??= Text(tags.FindFirst("5F20"));
            label ??= Text(tags.FindFirst("50"));
            preferred ??= Text(tags.FindFirst("9F12"));
        }

        string? panLast4 = pan5A ?? pan57;
        string? expiry = expiryDate ?? expiryTrack;
        if (panLast4 is null || expiry is null)
        {
            throw new EmvFormatException();
        }

        return new CardSelection(app.Aid, label, preferred, app.Priority, panLast4, expiry, name);
    }

    private static byte[] ZeroFillDol(ReadOnlySpan<byte> dol)
    {
        EmvTlvList fields;
        try
        {
            fields = EmvTagParser.ParseDol(dol.ToArray());
        }
        catch (Exception)
        {
            throw new EmvFormatException();
        }

        var output = new List<byte>();
        foreach (var field in fields)
        {
            if (field.Length is < 0 or > 255)
            {
                throw new EmvFormatException();
            }

            for (int n = 0; n < field.Length; n++)
            {
                output.Add(0);
            }
        }

        return output.ToArray();
    }

    private static void WriteLength(List<byte> target, int length)
    {
        if (length < 0x80)
        {
            target.Add((byte)length);
            return;
        }

        if (length > 0xFF)
        {
            throw new EmvFormatException();
        }

        target.Add(0x81);
        target.Add((byte)length);
    }

    private static EmvTlvList Parse(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return new EmvTlvList();
        }

        try
        {
            return EmvTlvList.Parse(data.ToArray()) ?? new EmvTlvList();
        }
        catch (Exception)
        {
            throw new EmvFormatException();
        }
    }

    private static int? Priority(EmvTlv? node)
    {
        byte[]? value = node?.Value.Bytes;
        if (value is not { Length: > 0 })
        {
            return null;
        }

        int priority = value[0] & 0x7F;
        return priority == 0 ? null : priority;
    }

    private static string? Text(EmvTlv? node)
    {
        byte[]? value = node?.Value.Bytes;
        if (value is not { Length: > 0 })
        {
            return null;
        }

        // Great.EmvTags replaces bytes above 0x7F with '?'. EMV text is the common character set,
        // so decode the value bytes ourselves.
        string text = Encoding.Latin1.GetString(value).Trim('\0', ' ');
        return text.Length == 0 ? null : text;
    }

    private static string? ExpiryDate(ReadOnlySpan<byte> value)
    {
        if (value.Length < 2 || Bcd(value[0]) is not int year || Bcd(value[1]) is not int month || month is < 1 or > 12)
        {
            return null;
        }

        return $"{2000 + year:0000}-{month:00}";
    }

    private static int? Bcd(byte value)
    {
        int hi = value >> 4;
        int lo = value & 0x0F;
        return hi > 9 || lo > 9 ? null : (hi * 10) + lo;
    }

    private static IEnumerable<int> PanNibbles(byte[] value)
    {
        foreach (byte b in value)
        {
            foreach (int nibble in new[] { b >> 4, b & 0x0F })
            {
                if (nibble is 0xF or 0xD)
                {
                    yield break;
                }

                if (nibble > 9)
                {
                    yield break;
                }

                yield return nibble;
            }
        }
    }

    private static (string? Pan, string? Expiry) Track2(ReadOnlySpan<byte> value)
    {
        var pan = new List<int>();
        var expiry = new List<int>();
        bool inExpiry = false;
        foreach (byte b in value)
        {
            foreach (int nibble in new[] { b >> 4, b & 0x0F })
            {
                if (!inExpiry)
                {
                    if (nibble is 0xD or 0xF)
                    {
                        inExpiry = true;
                        continue;
                    }

                    if (nibble > 9)
                    {
                        return (null, null);
                    }

                    pan.Add(nibble);
                    continue;
                }

                if (nibble > 9 || expiry.Count == 4)
                {
                    return (Last4(pan), ExpiryNibbles(expiry));
                }

                expiry.Add(nibble);
            }
        }

        return (Last4(pan), ExpiryNibbles(expiry));
    }

    private static string? ExpiryNibbles(List<int> nibbles)
    {
        if (nibbles.Count < 4)
        {
            return null;
        }

        int year = (nibbles[0] * 10) + nibbles[1];
        int month = (nibbles[2] * 10) + nibbles[3];
        return month is < 1 or > 12 ? null : $"{2000 + year:0000}-{month:00}";
    }

    /// <summary>Keeps four digits. The rest of the number is never assembled into a string.</summary>
    private static string? Last4(IEnumerable<int> nibbles)
    {
        var ring = new char[4];
        int count = 0;
        foreach (int nibble in nibbles)
        {
            ring[count % 4] = (char)('0' + nibble);
            count++;
        }

        if (count < 4)
        {
            return null;
        }

        var last = new char[4];
        int start = count % 4;
        for (int i = 0; i < 4; i++)
        {
            last[i] = ring[(start + i) % 4];
        }

        return new string(last);
    }
}

internal sealed class EmvFormatException : Exception
{
    public EmvFormatException()
        : base("The card data could not be read.")
    {
    }
}
