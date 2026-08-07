using BlueTusk.ServiceTopology.Domain;

namespace BlueTusk.ServiceTopology.Api;

public sealed record RegisterServiceRequest(string Name);

public sealed record ConnectServicesRequest(Guid SourceId, Guid DestinationId);

public sealed record ReportHealthRequest(ServiceHealth Health, long ExpectedVersion);

public sealed record OpenIncidentRequest(string Summary);
