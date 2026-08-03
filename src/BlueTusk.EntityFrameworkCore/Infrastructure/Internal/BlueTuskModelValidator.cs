using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.Infrastructure.Internal;

internal sealed class BlueTuskModelValidator(
    ModelValidatorDependencies dependencies,
    RelationalModelValidatorDependencies relationalDependencies)
    : RelationalModelValidator(dependencies, relationalDependencies);
