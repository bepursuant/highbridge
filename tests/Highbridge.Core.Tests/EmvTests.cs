using System.Text;
using System.Text.Json;
using Highbridge;
using Highbridge.Cards;

namespace Highbridge.Core.Tests;

public class EmvTests
{
    private static readonly byte[] FullPan = Convert.FromHexString("4111111111111234");

    [Fact]
    public void Select_and_empty_gpo_match_the_payment_commands()
    {
        Assert.Equal(
            Convert.FromHexString("00A404000E315041592E5359532E444446303100"),
            Emv.Select(Emv.PseName));
        Assert.Equal(
            Convert.FromHexString("00A404000E325041592E5359532E444446303100"),
            Emv.Select(Emv.PpseName));
        Assert.Equal(Convert.FromHexString("80A8000002830000"), Emv.GetProcessingOptions([]));
        Assert.Equal(Convert.FromHexString("00B2010C00"), Emv.ReadRecord(1, 1));
    }

    [Fact]
    public void Pdol_amount_stays_zero_and_tags_are_not_echoed()
    {
        byte[] pdol = Tlv("9F38", Convert.FromHexString("9F02069F1A02"));
        byte[] gpo = Emv.GetProcessingOptions(Emv.Pdol(Wrap(pdol))!);

        Assert.Equal(16, gpo.Length);
        Assert.Equal(new byte[] { 0x80, 0xA8, 0x00, 0x00, 0x0A, 0x83, 0x08 }, gpo[..7]);
        Assert.Equal(new byte[6], gpo[7..13]);
        Assert.Equal(0x00, gpo[^1]);
        Assert.DoesNotContain("9F02", Convert.ToHexString(gpo), StringComparison.Ordinal);
        Assert.DoesNotContain("9F1A", Convert.ToHexString(gpo), StringComparison.Ordinal);
    }

    [Fact]
    public void Contactless_pdol_names_terminal_capabilities_and_keeps_amount_zero()
    {
        byte[] pdol = Tlv("9F38", Convert.FromHexString("9F66049F0206"));
        byte[] gpo = Emv.GetProcessingOptions(Emv.Pdol(Wrap(pdol))!);

        Assert.Equal(Convert.FromHexString("36000000000000000000"), gpo[7..17]);
        Assert.Equal(0x00, gpo[^1]);
    }

    [Fact]
    public void Directory_entries_come_from_nested_templates()
    {
        byte[] visa = Convert.FromHexString("A0000000031010");
        byte[] entry = Tlv("61", Concat(
            Tlv("4F", visa),
            Tlv("50", "VISA CREDIT"u8.ToArray()),
            Tlv("87", [0x81]),
            Tlv("9F12", "Visa"u8.ToArray())));
        byte[] fci = Tlv("6F", Tlv("A5", Tlv("BF0C", entry)));

        var apps = Emv.DirectoryEntries(fci);

        var app = Assert.Single(apps);
        Assert.Equal("A0000000031010", app.Aid);
        Assert.Equal("VISA CREDIT", app.Label);
        Assert.Equal("Visa", app.PreferredName);
        Assert.Equal(1, app.Priority);
    }

    [Fact]
    public void Allowlist_keeps_last_four_expiry_and_name_and_drops_everything_else()
    {
        byte[] record = Tlv("70", Concat(
            Tlv("5A", FullPan),
            Tlv("5F24", Convert.FromHexString("281231")),
            Tlv("5F20", "DOE/JOHN"u8.ToArray()),
            Tlv("56", "4111111111119999"u8.ToArray()),
            Tlv("9F26", Convert.FromHexString("0011223344556677"))));
        var app = new CardApplication("A0000000031010", "VISA CREDIT", null, 1);

        var selected = Emv.MapSelection(app, [record]);

        Assert.Equal("4111111111111234", selected.Pan);
        Assert.Equal("1234", selected.PanLast4);
        Assert.Equal("2028-12", selected.Expiry);
        Assert.Equal("DOE/JOHN", selected.CardholderName);
        Assert.Null(selected.PreferredName);
        Assert.DoesNotContain("4111111111119999", selected.Pan, StringComparison.Ordinal);
        Assert.DoesNotContain("0011223344556677", selected.ToString(), StringComparison.Ordinal);
        AssertNumberIsForThePageOnly(selected, "4111111111111234");
    }

    [Fact]
    public void Track2_supplies_last_four_and_expiry_when_the_dedicated_tags_are_absent()
    {
        byte[] track = Convert.FromHexString("4111111111115678D2712F");
        byte[] record = Tlv("70", Tlv("57", track));
        var app = new CardApplication("A0000000041010", "MASTERCARD", null, null);

        var selected = Emv.MapSelection(app, [record]);

        Assert.Equal("4111111111115678", selected.Pan);
        Assert.Equal("5678", selected.PanLast4);
        Assert.Equal("2027-12", selected.Expiry);
        Assert.Null(selected.CardholderName);
        Assert.DoesNotContain("2712", selected.Pan, StringComparison.Ordinal);
        AssertNumberIsForThePageOnly(selected, "4111111111115678");
    }

    [Fact]
    public void Dedicated_pan_and_expiry_win_over_track2()
    {
        byte[] record = Tlv("70", Concat(
            Tlv("57", Convert.FromHexString("4111111111115678D2712F")),
            Tlv("5A", FullPan),
            Tlv("5F24", Convert.FromHexString("281231"))));

        var selected = Emv.MapSelection(new CardApplication("A0000000031010", null, null, null), [record]);

        Assert.Equal("4111111111111234", selected.Pan);
        Assert.Equal("1234", selected.PanLast4);
        Assert.Equal("2028-12", selected.Expiry);
        Assert.DoesNotContain("5678", selected.Pan, StringComparison.Ordinal);
    }

    [Fact]
    public void Afl_is_read_from_format_80_and_format_77()
    {
        var primitive = Assert.Single(Emv.ParseAfl(Tlv("80", Convert.FromHexString("000008010100"))));
        Assert.Equal(new Emv.AflEntry(1, 1, 1), primitive);

        byte[] constructed = Tlv("77", Concat(Tlv("82", [0x00, 0x00]), Tlv("94", Convert.FromHexString("10010200"))));
        var entry = Assert.Single(Emv.ParseAfl(constructed));
        Assert.Equal(new Emv.AflEntry(2, 1, 2), entry);
    }

    [Fact]
    public void Broken_tlv_is_rejected_without_echoing_the_bytes()
    {
        var ex = Assert.Throws<EmvFormatException>(() => Emv.DirectoryEntries([0x6F]));
        Assert.DoesNotContain("6F", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Card_options_reject_unknown_members_and_a_bad_aid()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CardReadOptions>("""{"apdu":"00A40400"}""", HighbridgeJson.Options));
        Assert.Contains("aid", new CardReadOptions { Aid = "A000" }.Validate()[0]);
        Assert.Empty(new CardReadOptions { Aid = "A0 00 00 00 03 10 10", WaitForCardMs = 0 }.Validate());
        Assert.Equal("A0000000031010", new CardReadOptions { Aid = "a0000000031010" }.NormalizedAid);
    }

    private static void AssertNumberIsForThePageOnly(CardSelection selected, string pan)
    {
        Assert.Equal(pan, selected.Pan);
        Assert.DoesNotContain(pan, selected.ToString(), StringComparison.Ordinal);
        string json = JsonSerializer.Serialize(selected, HighbridgeJson.Options);
        Assert.Contains($"\"pan\":\"{pan}\"", json, StringComparison.Ordinal);
        selected.ClearCardNumber();
        Assert.Equal("", selected.Pan);
        Assert.DoesNotContain(pan, JsonSerializer.Serialize(selected, HighbridgeJson.Options), StringComparison.Ordinal);
        Assert.Equal(pan[^4..], selected.PanLast4);
    }

    private static byte[] Wrap(byte[] inner) => Tlv("6F", inner);

    private static byte[] Tlv(string tagHex, byte[] value)
    {
        byte[] tag = Convert.FromHexString(tagHex);
        Assert.True(value.Length < 128);
        var result = new byte[tag.Length + 1 + value.Length];
        tag.CopyTo(result, 0);
        result[tag.Length] = (byte)value.Length;
        value.CopyTo(result, tag.Length + 1);
        return result;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
