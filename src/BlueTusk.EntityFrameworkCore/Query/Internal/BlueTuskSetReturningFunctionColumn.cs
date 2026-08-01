namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed record BlueTuskSetReturningFunctionColumn(
    string Name,
    Type ClrType,
    string? StoreType,
    bool IsNullable);
