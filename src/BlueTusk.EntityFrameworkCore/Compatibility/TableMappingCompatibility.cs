using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Compatibility;

internal static class TableMappingCompatibility
{
    public static IReadOnlyEntityType? GetBlueTuskEntityType(this ITableMappingBase mapping) =>
#if BLUETUSK_EF8_OR_GREATER
        mapping.TypeBase as IReadOnlyEntityType;
#else
        mapping.EntityType;
#endif
}
