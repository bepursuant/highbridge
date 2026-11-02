using System.Text.Json.Serialization;

namespace Highbridge.Provenance;

[JsonConverter(typeof(CamelCaseEnumConverter<StampPosition>))]
public enum StampPosition
{
    Bottom,
    Top,
}

/// <summary>
/// What a web page may say about the provenance stamp: only where it goes. Every value printed in
/// the band comes from highbridge, so unknown members (such as text) are rejected.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StampOptions
{
    public StampPosition Position { get; init; } = StampPosition.Bottom;
}

[JsonConverter(typeof(CamelCaseEnumConverter<StampMode>))]
public enum StampMode
{
    /// <summary>The page decides whether to stamp (default).</summary>
    Optional,

    /// <summary>Every scan is stamped, whatever the page asks.</summary>
    Required,

    /// <summary>Stamp requests are rejected with <c>stamp_disabled</c>.</summary>
    Disabled,
}

/// <summary><c>provenanceStamp</c> in policy.json.</summary>
public sealed class ProvenanceStampPolicy
{
    public StampMode Mode { get; set; } = StampMode.Optional;

    /// <summary>When set, overrides the position the page asks for.</summary>
    public StampPosition? Position { get; set; }
}

/// <summary>Who and where a scan was made. Read by highbridge from Windows, never from the page.</summary>
public sealed record ProvenanceIdentity(string Workstation, string User)
{
    public static ProvenanceIdentity FromEnvironment() =>
        new(Environment.MachineName, $"{Environment.UserDomainName}\\{Environment.UserName}");
}

/// <summary>
/// The provenance of one stamped page. The same record is printed in the band, embedded in the
/// PNG (iTXt <c>highbridge:provenance</c>) and returned to the page.
/// </summary>
public sealed record PageProvenance
{
    public const int CurrentVersion = 1;
    public const string PixelHashMethod = "highbridge-pixels-v1";

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Short id made by highbridge for the whole scan, such as <c>HB-7K3M-9QX2</c>.</summary>
    public required string ScanId { get; init; }

    /// <summary>Local time with its UTC offset, to the second.</summary>
    public required DateTimeOffset ScannedAt { get; init; }

    public required DateTimeOffset ScannedAtUtc { get; init; }

    public required string Workstation { get; init; }

    /// <summary>Windows account as DOMAIN\user.</summary>
    public required string User { get; init; }

    public required string Scanner { get; init; }

    /// <summary>The browser-verified origin of the site that requested the scan.</summary>
    public required string Origin { get; init; }

    /// <summary>1-based.</summary>
    public required int Page { get; init; }

    public required int PageCount { get; init; }

    /// <summary>
    /// SHA-256 (hex) of the page as scanned, before the band was added, in the canonical pixel form
    /// named by <see cref="HashMethod"/>. <see cref="ProvenanceVerifier"/> recomputes it from the
    /// stamped PNG.
    /// </summary>
    public required string OriginalSha256 { get; init; }

    public string HashMethod { get; init; } = PixelHashMethod;

    public required StampPosition Position { get; init; }

    /// <summary>Height in pixels of the added band (including its rule).</summary>
    public required int BandHeight { get; init; }

    public required string Software { get; init; }
}
