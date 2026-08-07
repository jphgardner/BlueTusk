using BlueTusk.EntityFrameworkCore.Views;
using BlueTusk.EntityFrameworkCore.Views.Internal;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskViewAlterationPlanner
{
    public static void ValidateReplacement(
        BlueTuskViewDefinition oldDefinition,
        BlueTuskViewDefinition definition)
    {
        BlueTuskViewMetadata.Validate(oldDefinition);
        BlueTuskViewMetadata.Validate(definition);
        if (!SameName(oldDefinition.Name, oldDefinition.Schema, definition.Name, definition.Schema))
        {
            throw new InvalidOperationException(
                "CREATE OR REPLACE VIEW cannot change a view's name or schema. Use RenameView first.");
        }

        if (oldDefinition.Columns.Count > 0 && definition.Columns.Count > 0 &&
            (definition.Columns.Count < oldDefinition.Columns.Count ||
             !oldDefinition.Columns.SequenceEqual(
                 definition.Columns.Take(oldDefinition.Columns.Count),
                 StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                $"PostgreSQL view '{definition.Schema}.{definition.Name}' cannot rename, remove, or reorder existing " +
                "output columns with CREATE OR REPLACE VIEW. Keep the existing prefix and only append new columns, " +
                "or use an explicit dependency-aware replacement migration.");
        }
    }

    public static void ValidateMaterializedAlteration(
        BlueTuskMaterializedViewDefinition oldDefinition,
        BlueTuskMaterializedViewDefinition definition)
    {
        BlueTuskViewMetadata.Validate(oldDefinition);
        BlueTuskViewMetadata.Validate(definition);
        if (!SameName(oldDefinition.Name, oldDefinition.Schema, definition.Name, definition.Schema))
        {
            throw new InvalidOperationException(
                "ALTER MATERIALIZED VIEW cannot use this operation to change a view's name or schema. " +
                "Use RenameView first.");
        }

        if (!string.Equals(oldDefinition.QuerySql, definition.QuerySql, StringComparison.Ordinal) ||
            !oldDefinition.Columns.SequenceEqual(definition.Columns, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"PostgreSQL materialized view '{definition.Schema}.{definition.Name}' cannot change its defining " +
                "query or output columns in place. Use an explicit dependency-aware drop/recreate migration.");
        }
    }

    private static bool SameName(
        string oldName,
        string? oldSchema,
        string name,
        string? schema) =>
        string.Equals(oldName, name, StringComparison.Ordinal) &&
        string.Equals(oldSchema, schema, StringComparison.Ordinal);
}
