using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Compatibility;

internal static class EntityFrameworkMetadataCompatibility
{
    public static string? GetBlueTuskTableAlias(this ColumnExpression column) =>
#if BLUETUSK_EF6_OR_GREATER
        column.TableAlias;
#else
        column.Table.Alias;
#endif

    public static RelationalTypeMapping GetBlueTuskStoreTypeMapping(this IColumn column) =>
#if BLUETUSK_EF6_OR_GREATER
        column.StoreTypeMapping;
#else
        column.PropertyMappings.First().TypeMapping;
#endif
}
