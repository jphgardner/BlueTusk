using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskModificationCommandBatchFactory(
    ModificationCommandBatchFactoryDependencies dependencies,
    IDbContextOptions options) : IModificationCommandBatchFactory
{
    private readonly int _maxBatchSize = RelationalOptionsExtension.Extract(options).MaxBatchSize ?? 42;

    public ModificationCommandBatch Create()
        => new BlueTuskModificationCommandBatch(dependencies, _maxBatchSize);
}

internal sealed class BlueTuskModificationCommandBatch(
    ModificationCommandBatchFactoryDependencies dependencies, int maxBatchSize)
    : AffectedCountModificationCommandBatch(dependencies, maxBatchSize)
{
    // A single unusually wide command remains valid, as required by EF's batch
    // contract. These limits bound aggregation, not the size of one entity.
    protected override bool IsValid()
        => SqlBuilder.Length <= 64 * 1024 && ParameterValues.Count <= short.MaxValue;
}
