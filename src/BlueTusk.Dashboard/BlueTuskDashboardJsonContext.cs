using System.Text.Json.Serialization;
using BlueTusk.ControlPlane;

namespace BlueTusk.Dashboard;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ControlPlaneOverview))]
[JsonSerializable(typeof(ControlPlaneSyncOverview))]
[JsonSerializable(typeof(ControlPlaneLiveOverview))]
[JsonSerializable(typeof(ControlPlaneContinuousGraphOverview))]
[JsonSerializable(typeof(ControlPlaneFleetOverview))]
[JsonSerializable(typeof(ControlPlaneApiCapabilities))]
[JsonSerializable(typeof(ControlPlaneOperationRequest))]
[JsonSerializable(typeof(ControlPlaneContinuousGraphRunRequest))]
[JsonSerializable(typeof(ControlPlaneContinuousGraphRunResult))]
[JsonSerializable(typeof(ControlPlaneApiResponse<ControlPlaneOverview>))]
[JsonSerializable(typeof(ControlPlaneApiResponse<ControlPlaneSyncOverview>))]
[JsonSerializable(typeof(ControlPlaneApiResponse<ControlPlaneLiveOverview>))]
[JsonSerializable(typeof(ControlPlaneApiResponse<ControlPlaneContinuousGraphOverview>))]
[JsonSerializable(typeof(ControlPlaneApiResponse<ControlPlaneFleetOverview>))]
[JsonSerializable(typeof(ControlPlaneApiResponse<ControlPlaneContinuousGraphRunResult>))]
[JsonSerializable(typeof(BlueTuskDashboardEndpointRouteBuilderExtensions.OperationSucceededResponse))]
[JsonSerializable(typeof(ControlPlaneApiResponse<BlueTuskDashboardEndpointRouteBuilderExtensions.OperationSucceededResponse>))]
internal sealed partial class BlueTuskDashboardJsonContext : JsonSerializerContext
{
}
