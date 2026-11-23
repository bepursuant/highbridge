namespace Highbridge.Provenance;

/// <summary>Who and where a scan was made. Read by highbridge from Windows, never from the page.</summary>
public sealed record ProvenanceIdentity(string Workstation, string User)
{
    public static ProvenanceIdentity FromEnvironment() =>
        new(Environment.MachineName, $"{Environment.UserDomainName}\\{Environment.UserName}");
}

/// <summary>
/// The provenance of one document. Embedded in the file (for a PNG, iTXt
/// <c>highbridge:provenance</c>) and returned to the page. The image itself is not changed.
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
    /// SHA-256 (hex) of the document pixels, in the canonical form named by <see cref="HashMethod"/>.
    /// <see cref="ProvenanceVerifier"/> recomputes it from the stamped PNG.
    /// </summary>
    public required string OriginalSha256 { get; init; }

    public string HashMethod { get; init; } = PixelHashMethod;

    public required string Software { get; init; }
}
