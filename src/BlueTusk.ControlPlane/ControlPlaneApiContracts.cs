namespace BlueTusk.ControlPlane;

public static class ControlPlaneApiContract
{
    public const int CurrentVersion = 1;

    public const int MinimumSupportedVersion = 1;

    public const string VersionedRoutePrefix = "/api/v1";

    public static ControlPlaneApiCapabilities Capabilities { get; } =
        new(
            CurrentVersion,
            MinimumSupportedVersion,
            Array.AsReadOnly([CurrentVersion]));
}

public sealed record ControlPlaneApiCapabilities(
    int CurrentVersion,
    int MinimumSupportedVersion,
    IReadOnlyList<int> SupportedVersions);

public sealed record ControlPlaneApiResponse<T>(
    int ContractVersion,
    T Data);
