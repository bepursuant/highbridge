using System.Text.Json;
using System.Text.Json.Nodes;
using Highbridge.Cards;
using Highbridge.Configuration;
using Highbridge.Scanning;

namespace Highbridge.Devices;

/// <summary>
/// Fills a module request from the defaults saved for one physical device.
/// A property present in the request replaces the saved value. A property the request omits stays.
/// </summary>
public static class DeviceOptions
{
    public const string Scanner = "scanner";
    public const string Card = "pcsc";

    public static async Task<ScanOptions> ResolveScanAsync(
        SettingsStore settings,
        IScannerBackend backend,
        JsonElement request,
        CancellationToken cancellationToken)
    {
        string? requested = String(request, "scannerId");
        string? deviceId = requested;
        if (deviceId is null)
        {
            try
            {
                var scanners = await backend.ListScannersAsync(cancellationToken).ConfigureAwait(false);
                deviceId = scanners.FirstOrDefault(s => s.IsDefault)?.Id ?? scanners.FirstOrDefault()?.Id;
            }
            catch (ScannerException)
            {
                deviceId = null;
            }
        }

        bool saved = TryLoad(settings, Scanner, deviceId, out ScanOptions defaults);
        if (requested is null && saved && string.IsNullOrEmpty(defaults.ScannerId) && deviceId is not null)
        {
            defaults.ScannerId = deviceId;
        }

        return Overlay(defaults, request);
    }

    public static async Task<CardReadOptions> ResolveCardAsync(
        SettingsStore settings,
        ICardReaderBackend backend,
        JsonElement request,
        CancellationToken cancellationToken)
    {
        string? requested = String(request, "readerId");
        string? deviceId = requested;
        if (deviceId is null)
        {
            try
            {
                var readers = await backend.ListReadersAsync(cancellationToken).ConfigureAwait(false);
                deviceId = readers.FirstOrDefault()?.Id;
            }
            catch (CardReaderException)
            {
                deviceId = null;
            }
        }

        bool saved = TryLoad(settings, Card, deviceId, out CardReadOptions defaults);
        if (requested is null && saved && string.IsNullOrEmpty(defaults.ReaderId) && deviceId is not null)
        {
            defaults.ReaderId = deviceId;
        }

        return Overlay(defaults, request);
    }

    public static T Overlay<T>(T defaults, JsonElement request)
    {
        var node = JsonSerializer.SerializeToNode(defaults, HighbridgeJson.Options)!.AsObject();
        if (request.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in request.EnumerateObject())
            {
                node[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }

        return node.Deserialize<T>(HighbridgeJson.Options)!;
    }

    private static bool TryLoad<T>(SettingsStore settings, string module, string? deviceId, out T options)
        where T : new()
    {
        var stored = settings.DeviceOptions(module, deviceId);
        if (stored is null)
        {
            options = new T();
            return false;
        }

        options = stored.Deserialize<T>(HighbridgeJson.Options) ?? new T();
        return true;
    }

    private static string? String(JsonElement request, string name)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
