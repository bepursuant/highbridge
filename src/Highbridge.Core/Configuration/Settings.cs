using System.Text.Json;
using System.Text.Json.Serialization;
using Highbridge.Provenance;

namespace Highbridge.Configuration;

public sealed class HighbridgeSettings
{
    public List<PairedOrigin> PairedOrigins { get; set; } = [];
}

public sealed class PairedOrigin
{
    public required string Origin { get; set; }

    public DateTimeOffset PairedAt { get; set; }

    /// <summary>
    /// Which approval wording the user accepted. Version 2 tells the user that stamped scans send
    /// their Windows user name and computer name. Version 3 tells them a card read sends the last
    /// four digits, the expiry, and the cardholder name. Version 4 tells them the full card number
    /// is sent to the site over the local connection. Missing means an older approval.
    /// </summary>
    public int? ApprovalVersion { get; set; }
}

[JsonConverter(typeof(CamelCaseEnumConverter<CardReaderMode>))]
public enum CardReaderMode
{
    Enabled,
    Disabled,
}

/// <summary><c>cardReader</c> in policy.json.</summary>
public sealed class CardReaderPolicy
{
    public CardReaderMode Mode { get; set; } = CardReaderMode.Enabled;
}

/// <summary>
/// Machine-wide settings an administrator can deploy to %ProgramData%\Highbridge\policy.json.
/// Unset values fall back to defaults; set values win over user settings.
/// </summary>
public sealed class HighbridgePolicy
{
    /// <summary>
    /// Origins that are treated as paired without prompting. A listed origin may scan and read cards.
    /// A card read includes the full card number, delivered only over the local API.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>When false, only <see cref="AllowedOrigins"/> may use highbridge.</summary>
    public bool? AllowPairing { get; set; }

    /// <summary>Accept http://localhost and http://127.0.0.1 pages (development). Default true.</summary>
    public bool? AllowInsecureLoopbackOrigins { get; set; }

    /// <summary>Provenance stamp rules; for example <c>{"mode": "required"}</c> stamps every scan.</summary>
    public ProvenanceStampPolicy? ProvenanceStamp { get; set; }

    /// <summary>
    /// Card reader switch. <c>{"mode": "disabled"}</c> makes card routes fail with
    /// <c>card_reader_disabled</c> and leaves scanning alone. Omitted means enabled.
    /// </summary>
    public CardReaderPolicy? CardReader { get; set; }

    public bool CardReaderEnabled => CardReader is not { Mode: CardReaderMode.Disabled };

    public static HighbridgePolicy Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new HighbridgePolicy();
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<HighbridgePolicy>(stream, HighbridgeJson.Options) ?? new HighbridgePolicy();
    }
}

public static class HighbridgePaths
{
    public static string UserSettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Highbridge", "settings.json");

    public static string MachinePolicyFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Highbridge", "policy.json");
}

/// <summary>Thread-safe JSON settings file. Pass a null path for an in-memory store.</summary>
public sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private HighbridgeSettings _current;

    public SettingsStore(string? path)
    {
        _path = path;
        _current = Load(path);
    }

    public HighbridgeSettings Snapshot()
    {
        lock (_gate)
        {
            return Clone(_current);
        }
    }

    public void Update(Action<HighbridgeSettings> mutate)
    {
        lock (_gate)
        {
            var next = Clone(_current);
            mutate(next);
            Save(next);
            _current = next;
        }
    }

    private static HighbridgeSettings Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new HighbridgeSettings();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<HighbridgeSettings>(stream, HighbridgeJson.Options) ?? new HighbridgeSettings();
        }
        catch (JsonException)
        {
            // A corrupt file must not brick the app; it is rewritten on the next change.
            return new HighbridgeSettings();
        }
    }

    private void Save(HighbridgeSettings settings)
    {
        if (_path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, HighbridgeJson.Indented));
        File.Move(temp, _path, overwrite: true);
    }

    private static HighbridgeSettings Clone(HighbridgeSettings s) =>
        JsonSerializer.Deserialize<HighbridgeSettings>(JsonSerializer.Serialize(s, HighbridgeJson.Options), HighbridgeJson.Options)!;
}
