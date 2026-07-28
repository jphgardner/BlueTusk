namespace BlueTusk.Data.Notifications;

/// <summary>An asynchronous notification delivered by PostgreSQL.</summary>
public sealed record BlueTuskNotification(
    int ProcessId,
    string Channel,
    string Payload);
