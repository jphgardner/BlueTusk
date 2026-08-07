namespace BlueTusk.OrderOperations.Api;

public sealed record CreateOrderRequest(string CustomerReference);

public sealed record TransitionOrderRequest(long ExpectedVersion, string? AllocationReference);
