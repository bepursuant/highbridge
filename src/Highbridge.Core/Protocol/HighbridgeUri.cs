using System.Globalization;
using System.Text;
using Highbridge.Barcodes;
using Highbridge.Cards;
using Highbridge.Provenance;
using Highbridge.Scanning;

namespace Highbridge.Protocol;

public enum ProtocolAction
{
    /// <summary>highbridge://launch - just make sure the app is running.</summary>
    Launch,

    /// <summary>highbridge://scan?... - start a scan for an already-paired origin.</summary>
    Scan,

    /// <summary>highbridge://read-card?... - start a card read for an already-paired origin.</summary>
    ReadCard,
}

public sealed record ProtocolCommand(
    ProtocolAction Action,
    string? Origin,
    string? RequestId,
    ScanOptions Options,
    CardReadOptions? CardRead = null);

/// <summary>
/// Parses and builds <c>highbridge://</c> URLs. Query parameter names match the JSON body of
/// <c>POST /v1/scans</c> or <c>POST /v1/card-reads</c>, plus <c>v</c> (protocol version),
/// <c>origin</c> and <c>request</c>.
/// </summary>
public static class HighbridgeUri
{
    public const string Scheme = "highbridge";
    public const int Version = 1;
    public const int MaxLength = 2048;

    public static bool TryParse(string? raw, out ProtocolCommand? command, out string? error)
    {
        command = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "empty URL";
            return false;
        }

        raw = raw.Trim();
        if (raw.Length > MaxLength)
        {
            error = "URL is too long";
            return false;
        }

        string prefix = Scheme + ":";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "not a highbridge: URL";
            return false;
        }

        string rest = raw[prefix.Length..].TrimStart('/');
        int queryStart = rest.IndexOf('?', StringComparison.Ordinal);
        string actionPart = (queryStart >= 0 ? rest[..queryStart] : rest).TrimEnd('/');
        string query = queryStart >= 0 ? rest[(queryStart + 1)..] : string.Empty;
        int hash = query.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            query = query[..hash];
        }

        ProtocolAction action;
        switch (actionPart.ToLowerInvariant())
        {
            case "":
            case "launch":
                action = ProtocolAction.Launch;
                break;
            case "scan":
                action = ProtocolAction.Scan;
                break;
            case "read-card":
                action = ProtocolAction.ReadCard;
                break;
            default:
                error = $"unknown action '{actionPart}'";
                return false;
        }

        var parameters = ParseQuery(query);
        if (parameters.TryGetValue("v", out string? v) && v != Version.ToString(CultureInfo.InvariantCulture))
        {
            error = $"unsupported protocol version '{v}'";
            return false;
        }

        if (action == ProtocolAction.Launch)
        {
            command = new ProtocolCommand(action, null, null, new ScanOptions());
            return true;
        }

        if (!parameters.TryGetValue("origin", out string? origin) || string.IsNullOrEmpty(origin))
        {
            error = action == ProtocolAction.ReadCard ? "read-card URL is missing 'origin'" : "scan URL is missing 'origin'";
            return false;
        }

        if (!parameters.TryGetValue("request", out string? requestId) || string.IsNullOrEmpty(requestId))
        {
            error = action == ProtocolAction.ReadCard ? "read-card URL is missing 'request'" : "scan URL is missing 'request'";
            return false;
        }

        if (action == ProtocolAction.ReadCard)
        {
            if (!TryReadCard(parameters, out var cardRead, out error))
            {
                return false;
            }

            command = new ProtocolCommand(action, origin, requestId, new ScanOptions(), cardRead);
            return true;
        }

        if (!TryReadOptions(parameters, out var options, out error))
        {
            return false;
        }

        command = new ProtocolCommand(action, origin, requestId, options);
        return true;
    }

    public static string BuildLaunch() => $"{Scheme}://launch";

    public static string BuildScan(string origin, string requestId, ScanOptions options)
    {
        var sb = new StringBuilder($"{Scheme}://scan?v={Version}");
        Append(sb, "origin", origin);
        Append(sb, "request", requestId);
        if (options.ScannerId is not null)
        {
            Append(sb, "scannerId", options.ScannerId);
        }

        Append(sb, "dpi", options.Dpi.ToString(CultureInfo.InvariantCulture));
        Append(sb, "colorMode", ToCamel(options.ColorMode.ToString()));
        Append(sb, "source", ToCamel(options.Source.ToString()));
        Append(sb, "duplex", options.Duplex ? "true" : "false");
        if (options.OneSheet)
        {
            Append(sb, "oneSheet", "true");
        }

        if (options.ShowDriverUi)
        {
            Append(sb, "showDriverUi", "true");
        }

        if (options.MaxPages > 0)
        {
            Append(sb, "maxPages", options.MaxPages.ToString(CultureInfo.InvariantCulture));
        }

        Append(sb, "format", ToCamel(options.Format.ToString()));
        if (StampUriParameter.Format(options.Stamp) is { } stamp)
        {
            Append(sb, StampUriParameter.Name, stamp);
        }

        if (options.Barcodes)
        {
            Append(sb, "barcodes", "true");
        }

        if (options.BarcodeFormats is { } barcodeFormats)
        {
            Append(sb, "barcodeFormats", BarcodeFormatSet.ToList(barcodeFormats));
        }

        return sb.ToString();
    }

    public static string BuildReadCard(string origin, string requestId, CardReadOptions options)
    {
        var sb = new StringBuilder($"{Scheme}://read-card?v={Version}");
        Append(sb, "origin", origin);
        Append(sb, "request", requestId);
        if (!string.IsNullOrEmpty(options.ReaderId))
        {
            Append(sb, "readerId", options.ReaderId);
        }

        if (options.Interface != CardInterface.Auto)
        {
            Append(sb, "interface", ToCamel(options.Interface.ToString()));
        }

        if (options.NormalizedAid is { } aid)
        {
            Append(sb, "aid", aid);
        }

        if (options.WaitForCardMs != CardReadOptions.DefaultWaitForCardMs)
        {
            Append(sb, "waitForCardMs", options.WaitForCardMs.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static bool TryReadCard(Dictionary<string, string> p, out CardReadOptions options, out string? error)
    {
        options = new CardReadOptions();
        error = null;
        foreach (string blocked in new[] { "apdu", "cla", "ins", "data" })
        {
            if (p.ContainsKey(blocked))
            {
                error = $"unknown option '{blocked}'";
                return false;
            }
        }

        if (p.TryGetValue("readerId", out string? readerId) && readerId.Length > 0)
        {
            options = options with { ReaderId = readerId };
        }

        if (p.TryGetValue("interface", out string? iface))
        {
            CardInterface? parsed = iface.ToLowerInvariant() switch
            {
                "auto" => CardInterface.Auto,
                "contact" => CardInterface.Contact,
                "contactless" => CardInterface.Contactless,
                _ => null,
            };
            if (parsed is null)
            {
                error = $"unknown interface '{iface}'";
                return false;
            }

            options = options with { Interface = parsed.Value };
        }

        if (p.TryGetValue("aid", out string? aid) && aid.Length > 0)
        {
            options = options with { Aid = aid };
        }

        if (p.TryGetValue("waitForCardMs", out string? wait))
        {
            if (!int.TryParse(wait, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                error = "waitForCardMs must be a whole number";
                return false;
            }

            options = options with { WaitForCardMs = value };
        }

        var problems = options.Validate();
        if (problems.Count > 0)
        {
            error = string.Join(" ", problems);
            return false;
        }

        return true;
    }

    private static bool TryReadOptions(Dictionary<string, string> p, out ScanOptions options, out string? error)
    {
        options = new ScanOptions();
        error = null;

        if (p.TryGetValue("scannerId", out string? scannerId) && scannerId.Length > 0)
        {
            options = options with { ScannerId = scannerId };
        }

        if (p.TryGetValue("dpi", out string? dpi))
        {
            if (!int.TryParse(dpi, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                error = "dpi must be a whole number";
                return false;
            }

            options = options with { Dpi = value };
        }

        if (p.TryGetValue("colorMode", out string? color))
        {
            ColorMode? mode = color.ToLowerInvariant() switch
            {
                "color" => ColorMode.Color,
                "gray" or "grey" => ColorMode.Gray,
                "blackwhite" or "bw" => ColorMode.BlackWhite,
                _ => null,
            };
            if (mode is null)
            {
                error = $"unknown colorMode '{color}'";
                return false;
            }

            options = options with { ColorMode = mode.Value };
        }

        if (p.TryGetValue("source", out string? source))
        {
            if (!Enum.TryParse<PaperSource>(source, ignoreCase: true, out var value) || !Enum.IsDefined(value) || int.TryParse(source, out _))
            {
                error = $"unknown source '{source}'";
                return false;
            }

            options = options with { Source = value };
        }

        if (!TryReadBool(p, "duplex", out bool? duplex, ref error)
            || !TryReadBool(p, "showDriverUi", out bool? showUi, ref error)
            || !TryReadBool(p, "oneSheet", out bool? oneSheet, ref error))
        {
            return false;
        }

        options = options with { Duplex = duplex ?? false, ShowDriverUi = showUi ?? false, OneSheet = oneSheet ?? false };

        if (p.TryGetValue("maxPages", out string? maxPages))
        {
            if (!int.TryParse(maxPages, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                error = "maxPages must be a whole number";
                return false;
            }

            options = options with { MaxPages = value };
        }

        if (p.TryGetValue("format", out string? format))
        {
            if (!OutputFormats.TryParse(format, out var value))
            {
                error = $"unknown format '{format}'";
                return false;
            }

            options = options with { Format = value };
        }

        if (!StampUriParameter.TryRead(p, out var stampOptions, out error) || !TryReadBool(p, "barcodes", out bool? barcodes, ref error))
        {
            return false;
        }

        options = options with { Stamp = stampOptions, Barcodes = barcodes ?? false };
        if (p.TryGetValue("barcodeFormats", out string? barcodeFormats))
        {
            if (!BarcodeFormatSet.TryParseList(barcodeFormats, out var set))
            {
                error = $"unknown barcodeFormats '{barcodeFormats}'";
                return false;
            }

            options = options with { BarcodeFormats = set };
        }

        var problems = options.Validate();
        if (problems.Count > 0)
        {
            error = string.Join(" ", problems);
            return false;
        }

        return true;
    }

    private static bool TryReadBool(Dictionary<string, string> p, string key, out bool? value, ref string? error)
    {
        value = null;
        if (!p.TryGetValue(key, out string? raw))
        {
            return true;
        }

        switch (raw.ToLowerInvariant())
        {
            case "true" or "1" or "yes":
                value = true;
                return true;
            case "false" or "0" or "no":
                value = false;
                return true;
            default:
                error = $"{key} must be true or false";
                return false;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            string key = Decode(eq >= 0 ? pair[..eq] : pair);
            string value = eq >= 0 ? Decode(pair[(eq + 1)..]) : string.Empty;
            result.TryAdd(key, value);
        }

        return result;
    }

    private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    private static void Append(StringBuilder sb, string key, string value) =>
        sb.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));

    private static string ToCamel(string s) => char.ToLowerInvariant(s[0]) + s[1..];
}
