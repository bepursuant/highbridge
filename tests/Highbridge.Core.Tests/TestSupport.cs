using Highbridge.Configuration;
using Highbridge.Consent;
using Highbridge.Scanning;

namespace Highbridge.Core.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal static class TestRuntime
{
    public const string Origin = "https://app.example.com";

    public static MockScannerBackend FastMock() =>
        new(new MockScannerOptions { PageDelay = TimeSpan.FromMilliseconds(1), FeederSheets = 2 });

    public static HighbridgeRuntime Create(
        IConsentService? consent = null,
        HighbridgePolicy? policy = null,
        TimeProvider? time = null,
        IScannerBackend? backend = null) =>
        new(
            backend ?? FastMock(),
            consent ?? new FixedConsentService(allow: true),
            new SettingsStore(path: null),
            policy ?? new HighbridgePolicy(),
            time);
}

/// <summary>Wraps a backend and counts how often the scanner was actually started.</summary>
internal sealed class CountingBackend(IScannerBackend inner) : IScannerBackend
{
    private int _scans;

    public int Scans => Volatile.Read(ref _scans);

    public string Name => inner.Name;

    public Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken) =>
        inner.ListScannersAsync(cancellationToken);

    public Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _scans);
        return inner.ScanAsync(options, onPage, cancellationToken);
    }
}
