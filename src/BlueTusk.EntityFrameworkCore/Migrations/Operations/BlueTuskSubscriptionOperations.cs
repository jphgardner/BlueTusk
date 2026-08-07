using BlueTusk.EntityFrameworkCore.Subscriptions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BlueTusk.EntityFrameworkCore.Migrations.Operations;

public sealed class CreateSubscriptionOperation : MigrationOperation
{
    public required BlueTuskSubscriptionDefinition Definition { get; init; }
}

public sealed class AlterSubscriptionOperation : MigrationOperation
{
    public required BlueTuskSubscriptionDefinition OldDefinition { get; init; }
    public required BlueTuskSubscriptionDefinition Definition { get; init; }
}

public sealed class DropSubscriptionOperation : MigrationOperation
{
    public required string Name { get; init; }
    public bool HasSlot { get; init; } = true;
}

public sealed class RenameSubscriptionOperation : MigrationOperation
{
    public required string Name { get; init; }
    public required string NewName { get; init; }
}

public sealed class RefreshSubscriptionOperation : MigrationOperation
{
    public required string Name { get; init; }
    public bool CopyData { get; init; } = true;
}

public sealed class RefreshSubscriptionSequencesOperation : MigrationOperation
{
    public required string Name { get; init; }
}

public sealed class SkipSubscriptionTransactionOperation : MigrationOperation
{
    public required string Name { get; init; }
    public string? FinishLsn { get; init; }
}
