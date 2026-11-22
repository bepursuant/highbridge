namespace Highbridge.Server;

/// <summary>Hub responses. A device module owns the responses for its own routes.</summary>
public sealed record DeviceStatus(string Kind, string Backend);

public sealed record StatusResponse(
    string App,
    string Version,
    int ApiVersion,
    string Backend,
    IReadOnlyList<DeviceStatus> Devices,
    bool Paired,
    bool PairingAllowed);

public sealed record PairResponse(bool Paired);

public sealed record ErrorResponse(string Error, string Message);
