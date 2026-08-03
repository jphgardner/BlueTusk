using Microsoft.EntityFrameworkCore.Update;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskModificationCommandBatchFactory(
    ModificationCommandBatchFactoryDependencies dependencies) : IModificationCommandBatchFactory
{
    public ModificationCommandBatch Create()
        => new SingularModificationCommandBatch(dependencies);
}
