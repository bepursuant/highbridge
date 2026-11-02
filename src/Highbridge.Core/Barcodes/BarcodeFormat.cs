using System.Text.Json;
using System.Text.Json.Serialization;
using ZXingFormat = ZXing.BarcodeFormat;

namespace Highbridge.Barcodes;

/// <summary>
/// Barcode symbologies highbridge can look for. A set of them (flags) restricts detection; a single
/// value names the format of a found barcode. JSON names are camelCase: "qrCode", "code128", "upcA".
/// </summary>
[Flags]
[JsonConverter(typeof(CamelCaseEnumConverter<BarcodeFormat>))]
public enum BarcodeFormat
{
    None = 0,
    QrCode = 1 << 0,
    DataMatrix = 1 << 1,
    Pdf417 = 1 << 2,
    Aztec = 1 << 3,
    Code128 = 1 << 4,
    Code39 = 1 << 5,
    Code93 = 1 << 6,
    Codabar = 1 << 7,
    Ean13 = 1 << 8,
    Ean8 = 1 << 9,
    UpcA = 1 << 10,
    UpcE = 1 << 11,
    Itf = 1 << 12,
}

public static class BarcodeFormatSet
{
    /// <summary>Used when a scan asks for barcodes without naming formats.</summary>
    public const BarcodeFormat Default =
        BarcodeFormat.QrCode | BarcodeFormat.DataMatrix | BarcodeFormat.Pdf417 |
        BarcodeFormat.Code128 | BarcodeFormat.Code39 | BarcodeFormat.Ean13 | BarcodeFormat.Ean8 |
        BarcodeFormat.UpcA | BarcodeFormat.UpcE | BarcodeFormat.Itf;

    public const BarcodeFormat All = Default | BarcodeFormat.Aztec | BarcodeFormat.Code93 | BarcodeFormat.Codabar;

    private static readonly (BarcodeFormat Ours, ZXingFormat Theirs)[] Map =
    [
        (BarcodeFormat.QrCode, ZXingFormat.QR_CODE),
        (BarcodeFormat.DataMatrix, ZXingFormat.DATA_MATRIX),
        (BarcodeFormat.Pdf417, ZXingFormat.PDF_417),
        (BarcodeFormat.Aztec, ZXingFormat.AZTEC),
        (BarcodeFormat.Code128, ZXingFormat.CODE_128),
        (BarcodeFormat.Code39, ZXingFormat.CODE_39),
        (BarcodeFormat.Code93, ZXingFormat.CODE_93),
        (BarcodeFormat.Codabar, ZXingFormat.CODABAR),
        (BarcodeFormat.Ean13, ZXingFormat.EAN_13),
        (BarcodeFormat.Ean8, ZXingFormat.EAN_8),
        (BarcodeFormat.UpcA, ZXingFormat.UPC_A),
        (BarcodeFormat.UpcE, ZXingFormat.UPC_E),
        (BarcodeFormat.Itf, ZXingFormat.ITF),
    ];

    /// <summary>The single formats contained in <paramref name="set"/>.</summary>
    public static IEnumerable<BarcodeFormat> Split(BarcodeFormat set) =>
        Map.Select(m => m.Ours).Where(f => (set & f) != 0);

    public static string Name(BarcodeFormat format) => JsonNamingPolicy.CamelCase.ConvertName(format.ToString());

    /// <summary>Parses one camelCase name ("qrCode"); case-insensitive, rejects numbers and sets.</summary>
    public static bool TryParse(string? value, out BarcodeFormat format)
    {
        format = BarcodeFormat.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var (ours, _) in Map)
        {
            if (string.Equals(ours.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                format = ours;
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses a comma-separated list ("qrCode,code128"), as used in highbridge:// links.</summary>
    public static bool TryParseList(string value, out BarcodeFormat set)
    {
        set = BarcodeFormat.None;
        foreach (string name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParse(name, out var format))
            {
                return false;
            }

            set |= format;
        }

        return true;
    }

    public static string ToList(BarcodeFormat set) => string.Join(',', Split(set).Select(Name));

    /// <summary>Problems with the barcode options of a scan request; empty when they are usable.</summary>
    public static IEnumerable<string> Validate(bool barcodes, BarcodeFormat? formats)
    {
        if (formats is not { } set)
        {
            yield break;
        }

        if (!barcodes)
        {
            yield return "barcodeFormats needs barcodes: true.";
        }

        if (set == BarcodeFormat.None)
        {
            yield return "barcodeFormats must name at least one format.";
        }
        else if ((set & ~All) != 0)
        {
            yield return "barcodeFormats contains an unsupported format.";
        }
    }

    internal static List<ZXingFormat> ToZXing(BarcodeFormat set) =>
        Map.Where(m => (set & m.Ours) != 0).Select(m => m.Theirs).ToList();

    internal static BarcodeFormat FromZXing(ZXingFormat format) =>
        Map.FirstOrDefault(m => m.Theirs == format).Ours;
}

/// <summary>Reads and writes a format set as a JSON array of names: ["qrCode", "code128"].</summary>
public sealed class BarcodeFormatListConverter : JsonConverter<BarcodeFormat?>
{
    public override bool HandleNull => true;

    public override BarcodeFormat? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("barcodeFormats must be an array of format names.");
        }

        var set = BarcodeFormat.None;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String || !BarcodeFormatSet.TryParse(reader.GetString(), out var format))
            {
                throw new JsonException("barcodeFormats contains an unknown format.");
            }

            set |= format;
        }

        return set;
    }

    public override void Write(Utf8JsonWriter writer, BarcodeFormat? value, JsonSerializerOptions options)
    {
        if (value is not { } set)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var format in BarcodeFormatSet.Split(set))
        {
            writer.WriteStringValue(BarcodeFormatSet.Name(format));
        }

        writer.WriteEndArray();
    }
}
