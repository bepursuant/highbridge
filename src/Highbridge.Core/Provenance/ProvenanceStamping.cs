using System.Reflection;
using System.Security.Cryptography;
using Highbridge.Jobs;
using Highbridge.Scanning;

namespace Highbridge.Provenance;

/// <summary>A page as the scanner delivered it, with the time highbridge received it.</summary>
public sealed record ReceivedPage(ScannedPage Page, DateTimeOffset ReceivedAt);

/// <summary>
/// Writes provenance onto every finished document. All values come from here (identity, clock,
/// scanner, verified origin). The page cannot turn it off or supply its own.
/// </summary>
public sealed class ProvenanceStamping
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public ProvenanceStamping(ProvenanceIdentity? identity = null, string? software = null)
    {
        Identity = identity ?? ProvenanceIdentity.FromEnvironment();
        Software = software ?? DefaultSoftware;
    }

    public ProvenanceIdentity Identity { get; }

    public string Software { get; }

    private static string DefaultSoftware { get; } =
        "highbridge " + (typeof(ProvenanceStamping).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");

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

    /// <summary>Stamps each finished document once. Page numbers are the document's place in the scan.</summary>
    public IReadOnlyList<ScannedPage> StampAll(
        IReadOnlyList<ReceivedPage> pages,
        string origin,
        string scanId,
        string scannerFallback,
        TimeZoneInfo timeZone)
    {
        var stamped = new List<ScannedPage>(pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            var (page, receivedAt) = pages[i];
            if (page.MediaType != "image/png")
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
                Software = Software,
            };

            StampedImage result;
            try
            {
                result = ProvenanceStamper.Stamp(page.Data, record);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
            {
                throw new ScannerException("stamp_failed", "highbridge could not add the provenance record, so no pages were returned.", ex);
            }

            stamped.Add(page.Stamped(result.Png, result.Width, result.Height, result.Provenance));
        }

        return stamped;
    }

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Offset);
}
