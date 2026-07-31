using Microsoft.EntityFrameworkCore.Update;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies)
    : UpdateSqlGenerator(dependencies);
