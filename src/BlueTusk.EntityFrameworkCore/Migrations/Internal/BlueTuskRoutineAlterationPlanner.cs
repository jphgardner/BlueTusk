using BlueTusk.EntityFrameworkCore.Routines;
using BlueTusk.EntityFrameworkCore.Routines.Internal;

namespace BlueTusk.EntityFrameworkCore.Migrations.Internal;

internal static class BlueTuskRoutineAlterationPlanner
{
    public static void ValidateReplacement(
        BlueTuskRoutineDefinition oldDefinition,
        BlueTuskRoutineDefinition definition)
    {
        BlueTuskRoutineMetadata.Validate(oldDefinition);
        BlueTuskRoutineMetadata.Validate(definition);
        oldDefinition = BlueTuskRoutineMetadata.Normalize(oldDefinition);
        definition = BlueTuskRoutineMetadata.Normalize(definition);
        if (BlueTuskRoutineMetadata.RoutineKey.Create(oldDefinition) !=
            BlueTuskRoutineMetadata.RoutineKey.Create(definition))
        {
            throw new InvalidOperationException(
                $"CREATE OR REPLACE cannot change the kind, name, schema, or input argument types of routine " +
                $"'{oldDefinition.Schema}.{oldDefinition.Name}({oldDefinition.InputArgumentTypesSql})'. " +
                "Create a new overload or use an explicit signature-qualified replacement migration.");
        }

        if (!string.Equals(
                oldDefinition.IdentityArgumentsSql,
                definition.IdentityArgumentsSql,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"PostgreSQL cannot replace routine '{definition.Schema}.{definition.Name}" +
                $"({definition.InputArgumentTypesSql})' while changing parameter names, modes, or output arguments. " +
                "Use an explicit drop/recreate migration after handling dependencies.");
        }

        if (!string.Equals(oldDefinition.ResultSql, definition.ResultSql, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"PostgreSQL cannot replace function '{definition.Schema}.{definition.Name}" +
                $"({definition.InputArgumentTypesSql})' with a different return type. " +
                "Use an explicit drop/recreate migration after handling dependencies.");
        }

        if (oldDefinition.IsWindow != definition.IsWindow)
        {
            throw new InvalidOperationException(
                $"PostgreSQL cannot change the WINDOW attribute of function '{definition.Schema}.{definition.Name}" +
                $"({definition.InputArgumentTypesSql})' in place. Use an explicit drop/recreate migration.");
        }
    }
}
