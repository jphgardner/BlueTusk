using BlueTusk.EntityFrameworkCore.Infrastructure.Internal;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskMemberTranslatorProvider : RelationalMemberTranslatorProvider
{
    public BlueTuskMemberTranslatorProvider(
        RelationalMemberTranslatorProviderDependencies dependencies,
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
#if BLUETUSK_EF6_OR_GREATER
        ,
        IDbContextOptions contextOptions)
        : this(dependencies, typeMappingSource, sqlGenerationHelper,
            contextOptions.FindExtension<BlueTuskOptionsExtension>()?.DataSource?.TypeRegistry)
#else
        ) : this(dependencies, typeMappingSource, sqlGenerationHelper, typeRegistry: null)
#endif
    {
    }

    internal BlueTuskMemberTranslatorProvider(
        RelationalMemberTranslatorProviderDependencies dependencies,
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper,
        BlueTuskTypeRegistry? typeRegistry) : base(dependencies)
    {
        AddTranslators(
        [
            new BlueTuskCompositeMemberTranslator(
                new BlueTuskCompositeFieldMappingResolver(
                    typeMappingSource,
                    sqlGenerationHelper,
                    typeRegistry)),
            new BlueTuskStringMemberTranslator(dependencies.SqlExpressionFactory),
        ]);
    }
}
