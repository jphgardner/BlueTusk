using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskMemberTranslatorProvider : RelationalMemberTranslatorProvider
{
    public BlueTuskMemberTranslatorProvider(
        RelationalMemberTranslatorProviderDependencies dependencies,
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper,
        IDbContextOptions contextOptions)
        : base(dependencies)
    {
        var dataSource = contextOptions.FindExtension<BlueTuskOptionsExtension>()?.DataSource;
        AddTranslators(
        [
            new BlueTuskCompositeMemberTranslator(
                new BlueTuskCompositeFieldMappingResolver(
                    typeMappingSource,
                    sqlGenerationHelper,
                    dataSource)),
            new BlueTuskStringMemberTranslator(dependencies.SqlExpressionFactory),
        ]);
    }
}
