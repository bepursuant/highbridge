using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Highbridge.Jobs;

/// <summary>
/// Job id, retention, and claim timeout shared by every device. Scanner-only work (pages, barcodes,
/// stamps) stays on <see cref="ScanJobManager"/>.
/// </summary>
public static partial class DeviceJobRules
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(10);

    /// <summary>A protocol job that its origin does not claim within this time never starts.</summary>
    public static readonly TimeSpan DefaultClaimTimeout = TimeSpan.FromSeconds(30);

    public const int MaxJobs = 32;

    public static bool IsValidRequestId(string? id) => id is not null && RequestIdPattern().IsMatch(id);

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    private static partial Regex RequestIdPattern();
}
