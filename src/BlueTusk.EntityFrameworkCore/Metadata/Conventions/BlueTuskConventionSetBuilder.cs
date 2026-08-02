using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.Infrastructure.Internal;

internal sealed class BlueTuskConventionSetBuilder(
    ProviderConventionSetBuilderDependencies dependencies,
    RelationalConventionSetBuilderDependencies relationalDependencies)
    : RelationalConventionSetBuilder(dependencies, relationalDependencies)
{
    public override ConventionSet CreateConventionSet()
    {
        var conventionSet = base.CreateConventionSet();
        conventionSet.ModelInitializedConventions.Add(
            new RelationalMaxIdentifierLengthConvention(63, Dependencies, RelationalDependencies));
        return conventionSet;
    }
}
