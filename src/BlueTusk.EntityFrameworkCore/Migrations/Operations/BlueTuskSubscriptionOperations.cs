using BlueTusk.EntityFrameworkCore.Subscriptions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateBlueTuskSubscriptionOperation : MigrationOperation
{
    public required BlueTuskSubscriptionDefinition Definition { get; init; }
}

public sealed class AlterBlueTuskSubscriptionOperation : MigrationOperation
{
    public required BlueTuskSubscriptionDefinition OldDefinition { get; init; }
    public required BlueTuskSubscriptionDefinition Definition { get; init; }
}

public sealed class DropBlueTuskSubscriptionOperation : MigrationOperation
{
    public required string Name { get; init; }
    public bool HasSlot { get; init; } = true;
}

public sealed class RenameBlueTuskSubscriptionOperation : MigrationOperation
{
    public required string Name { get; init; }
    public required string NewName { get; init; }
}

public sealed class RefreshBlueTuskSubscriptionOperation : MigrationOperation
{
    public required string Name { get; init; }
    public bool CopyData { get; init; } = true;
}

public sealed class RefreshBlueTuskSubscriptionSequencesOperation : MigrationOperation
{
    public required string Name { get; init; }
}

public sealed class SkipBlueTuskSubscriptionTransactionOperation : MigrationOperation
{
    public required string Name { get; init; }
    public string? FinishLsn { get; init; }
}
