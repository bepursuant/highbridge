namespace Highbridge.Consent;

public sealed record PairingRequest(string Origin);

/// <summary>
/// Asks the person at the keyboard to approve a website. This is the only prompt highbridge
/// shows: scans from an approved site run without asking. Implementations must time out rather
/// than hang forever.
/// </summary>
public interface IConsentService
{
    Task<bool> RequestPairingAsync(PairingRequest request, CancellationToken cancellationToken);
}

/// <summary>Non-interactive consent for the mock dev host and tests. Never use with a real scanner.</summary>
public sealed class FixedConsentService : IConsentService
{
    private readonly bool _allow;
    private int _pairingRequests;

    public FixedConsentService(bool allow)
    {
        _allow = allow;
    }

    public int PairingRequests => Volatile.Read(ref _pairingRequests);

    public Task<bool> RequestPairingAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _pairingRequests);
        return Task.FromResult(_allow);
    }
}
