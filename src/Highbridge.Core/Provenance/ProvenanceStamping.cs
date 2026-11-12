using System.Reflection;
using System.Security.Cryptography;
using Highbridge.Jobs;
using Highbridge.Scanning;

namespace Highbridge.Provenance;

/// <summary>A page as the scanner delivered it, with the time highbridge received it.</summary>
public sealed record ReceivedPage(ScannedPage Page, DateTimeOffset ReceivedAt);

/// <summary>
/// Applies the stamp policy to scan requests and stamps finished jobs. All provenance values come
/// from here (identity, clock, scanner, verified origin); the page only picks on/off and position.
/// </summary>
public sealed class ProvenanceStamping
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public ProvenanceStamping(ProvenanceStampPolicy? policy = null, ProvenanceIdentity? identity = null, string? software = null)
    {
        Policy = policy ?? new ProvenanceStampPolicy();
        Identity = identity ?? ProvenanceIdentity.FromEnvironment();
        Software = software ?? DefaultSoftware;
    }

    public ProvenanceStampPolicy Policy { get; }

    public ProvenanceIdentity Identity { get; }

    public string Software { get; }

    private static string DefaultSoftware { get; } =
        "highbridge " + (typeof(ProvenanceStamping).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");

    /// <summary>
    /// The stamp a scan gets, or null for none. Throws <see cref="JobRejectedException"/> when the
    /// policy disables stamping or the site's approval does not cover sending the user's identity.
    /// </summary>
    /// <param name="identityApproved">The site's approval (or the admin's policy) covers user and computer names.</param>
    public StampOptions? Resolve(StampOptions? requested, bool identityApproved, out bool requiredByPolicy)
    {
        requiredByPolicy = Policy.Mode == StampMode.Required;
        if (Policy.Mode == StampMode.Disabled)
        {
            return requested is null
                ? null
                : throw new JobRejectedException("stamp_disabled", "Your administrator has turned off provenance stamps.");
        }

        if (requested is null && !requiredByPolicy)
        {
            return null;
        }

        if (!requiredByPolicy && !identityApproved)
        {
            throw new JobRejectedException(
                "approval_required",
                "Stamped scans include your Windows user name and computer name. Approve this site again (POST /v1/pair) to allow that.");
        }

        var stamp = requested ?? new StampOptions();
        return Policy.Position is { } forced ? stamp with { Position = forced } : stamp;
    }

    /// <summary>40 random bits as <c>HB-XXXX-XXXX</c> (Crockford base32: no I, L, O or U).</summary>
    public static string NewScanId()
    {
        Span<char> id = stackalloc char[8];
        for (int i = 0; i < id.Length; i++)
        {
            id[i] = Crockford[RandomNumberGenerator.GetInt32(Crockford.Length)];
        }

        return $"HB-{id[..4]}-{id[4..]}";
    }

    /// <summary>Stamps every page of a finished scan ("page N of M" needs them all).</summary>
    public IReadOnlyList<ScannedPage> StampAll(
        IReadOnlyList<ReceivedPage> pages,
        StampOptions stamp,
        string origin,
        string scanId,
        string scannerFallback,
        int fallbackDpi,
        TimeZoneInfo timeZone)
    {
        var stamped = new List<ScannedPage>(pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            var (page, receivedAt) = pages[i];
            if (page.MediaType != OutputFormats.PngMediaType)
            {
                throw new ScannerException("stamp_failed", $"Cannot stamp {page.MediaType} pages.");
            }

            var utc = TruncateToSeconds(receivedAt.ToUniversalTime());
            var record = new PageProvenance
            {
                ScanId = scanId,
                ScannedAt = TimeZoneInfo.ConvertTime(utc, timeZone),
                ScannedAtUtc = utc,
                Workstation = Identity.Workstation,
                User = Identity.User,
                Scanner = string.IsNullOrWhiteSpace(page.ScannerName) ? scannerFallback : page.ScannerName,
                Origin = origin,
                Page = i + 1,
                PageCount = pages.Count,
                OriginalSha256 = string.Empty,
                Position = stamp.Position,
                BandHeight = 0,
                Software = Software,
            };

            StampedImage result;
            try
            {
                result = ProvenanceStamper.Stamp(page.Data, record, fallbackDpi);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
            {
                throw new ScannerException("stamp_failed", "highbridge could not add the provenance stamp, so no pages were returned.", ex);
            }

            stamped.Add(page.Stamped(result.Png, result.Width, result.Height, result.Provenance));
        }

        return stamped;
    }

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Offset);
}
