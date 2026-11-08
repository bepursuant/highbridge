using System.Collections.Concurrent;
using Highbridge.Configuration;

namespace Highbridge.Security;

/// <summary>Decides which web origins may use highbridge, combining machine policy and user pairings.</summary>
public sealed class OriginRegistry
{
    public static readonly TimeSpan DenialCooldown = TimeSpan.FromMinutes(10);

    /// <summary>An unanswered pairing prompt counts as "no" after this long.</summary>
    public static readonly TimeSpan PairingPromptTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The approval wording shown now. Version 2 added the stamp identity sentence. Version 3 added
    /// card reads of the last four digits, the expiry, and the cardholder name. Version 4 tells the
    /// user the full card number is sent to the site over the local connection.
    /// </summary>
    public const int CurrentApprovalVersion = 4;

    private readonly SettingsStore _settings;
    private readonly HighbridgePolicy _policy;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _policyOrigins;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _deniedUntil = new(StringComparer.Ordinal);

    public OriginRegistry(SettingsStore settings, HighbridgePolicy policy, TimeProvider? time = null)
    {
        _settings = settings;
        _policy = policy;
        _time = time ?? TimeProvider.System;
        _policyOrigins = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in policy.AllowedOrigins)
        {
            if (OriginRules.TryNormalize(raw, AllowInsecureLoopbackOrigins, out string origin))
            {
                _policyOrigins.Add(origin);
            }
        }
    }

    public bool AllowPairing => _policy.AllowPairing ?? true;

    public bool AllowInsecureLoopbackOrigins => _policy.AllowInsecureLoopbackOrigins ?? true;

    public bool TryNormalize(string? raw, out string origin) =>
        OriginRules.TryNormalize(raw, AllowInsecureLoopbackOrigins, out origin);

    public bool IsPaired(string origin) =>
        _policyOrigins.Contains(origin) || _settings.Snapshot().PairedOrigins.Any(p => p.Origin == origin);

    public bool IsPolicyOrigin(string origin) => _policyOrigins.Contains(origin);

    /// <summary>
    /// True when the site may receive the user's identity in provenance stamps: the admin listed it
    /// in policy, or the user approved it with the current wording.
    /// </summary>
    public bool ApprovalCoversIdentity(string origin) =>
        _policyOrigins.Contains(origin) ||
        _settings.Snapshot().PairedOrigins.Any(p => p.Origin == origin && p.ApprovalVersion >= CurrentApprovalVersion);

    public IReadOnlyList<PairedOrigin> ListUserPairings() => _settings.Snapshot().PairedOrigins;

    public IReadOnlyCollection<string> PolicyOrigins => _policyOrigins;

    public void Pair(string origin)
    {
        _deniedUntil.TryRemove(origin, out _);
        _settings.Update(s =>
        {
            if (s.PairedOrigins.FirstOrDefault(p => p.Origin == origin) is { } existing)
            {
                existing.ApprovalVersion = CurrentApprovalVersion;
            }
            else
            {
                s.PairedOrigins.Add(new PairedOrigin { Origin = origin, PairedAt = _time.GetUtcNow(), ApprovalVersion = CurrentApprovalVersion });
            }
        });
    }

    public void Unpair(string origin) =>
        _settings.Update(s => s.PairedOrigins.RemoveAll(p => p.Origin == origin));

    public void RecordDenial(string origin) => _deniedUntil[origin] = _time.GetUtcNow() + DenialCooldown;

    public bool IsInDenialCooldown(string origin) =>
        _deniedUntil.TryGetValue(origin, out var until) && until > _time.GetUtcNow();
}
