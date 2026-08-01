using System.Data;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;

#pragma warning disable EF1001 // Provider SQL generation requires EF Core relational command infrastructure.

namespace BlueTusk.EntityFrameworkCore.Update.Internal;

internal enum BlueTuskMergeMatchedAction
{
    Update,
    Delete,
    DoNothing,
}

internal sealed record BlueTuskMergeCommandPlan(
    string CommandText,
    IRelationalCommand Command,
    IReadOnlyDictionary<string, object?> ParameterValues);

internal static class BlueTuskMergeCommandFactory
{
    public static BlueTuskMergeCommandPlan Create<TEntity>(
        DbContext context,
        Expression<Func<TEntity>> values,
        LambdaExpression matchProperties,
        LambdaExpression? updateProperties,
        BlueTuskMergeMatchedAction matchedAction)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(matchProperties);
        if (context.Database.ProviderName != BlueTuskEntityFrameworkCoreInfo.ProviderName)
        {
            throw new InvalidOperationException(
                "PostgreSQL MERGE can only execute with the BlueTusk Entity Framework Core provider.");
        }

        if (matchedAction == BlueTuskMergeMatchedAction.Update && updateProperties is null)
        {
            throw new ArgumentNullException(nameof(updateProperties));
        }

        if (values.Body is not MemberInitExpression { Bindings.Count: > 0 } initializer
            || initializer.NewExpression.Type != typeof(TEntity))
        {
            throw new ArgumentException(
                "PostgreSQL MERGE values must be a non-empty entity object initializer.",
                nameof(values));
        }

        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException(
                $"The entity type '{typeof(TEntity).Name}' is not part of the current EF model.");
        if (entityType.BaseType is not null || entityType.GetDerivedTypes().Any())
        {
            throw new InvalidOperationException(
                "PostgreSQL MERGE currently requires an entity mapped without inheritance.");
        }

        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException(
                $"The entity type '{entityType.DisplayName()}' is not mapped to a table.");
        var schema = entityType.GetSchema();
        var relationalTable = entityType.Model.GetRelationalModel().FindTable(tableName, schema)
            ?? throw new InvalidOperationException(
                $"The mapped table for entity type '{entityType.DisplayName()}' was not found.");

        var insertedValues = GetInsertedValues(entityType, relationalTable, initializer, nameof(values));
        var matchColumns = GetSelectedColumns(
            entityType,
            relationalTable,
            matchProperties,
            insertedValues,
            nameof(matchProperties));
        var updateColumns = matchedAction == BlueTuskMergeMatchedAction.Update
            ? GetSelectedColumns(
                entityType,
                relationalTable,
                updateProperties!,
                insertedValues,
                nameof(updateProperties))
            : [];

        var helper = context.GetService<ISqlGenerationHelper>();
        var commandBuilder = context.GetService<IRelationalCommandBuilderFactory>().Create();
        var parameterValues = new Dictionary<string, object?>(insertedValues.Length, StringComparer.Ordinal);
        var parameterPlaceholders = new string[insertedValues.Length];
        for (var index = 0; index < insertedValues.Length; index++)
        {
            var invariantName = $"__merge_{index}";
            var parameterName = helper.GenerateParameterName(invariantName);
            parameterPlaceholders[index] = helper.GenerateParameterNamePlaceholder(invariantName);
            parameterValues.Add(invariantName, Evaluate(insertedValues[index].Value));
            commandBuilder.AddParameter(new TypeMappedRelationalParameter(
                invariantName,
                parameterName,
                insertedValues[index].Column.StoreTypeMapping,
                insertedValues[index].Property.IsNullable,
                ParameterDirection.Input));
        }

        const string targetAlias = "target";
        const string sourceAlias = "source";
        commandBuilder.Append("MERGE INTO ")
            .Append(helper.DelimitIdentifier(tableName, schema))
            .Append(" AS ")
            .Append(helper.DelimitIdentifier(targetAlias))
            .AppendLine()
            .Append("USING (VALUES (");
        AppendItems(commandBuilder, parameterPlaceholders, static value => value);
        commandBuilder.Append(")) AS ")
            .Append(helper.DelimitIdentifier(sourceAlias))
            .Append(" (");
        AppendItems(
            commandBuilder,
            insertedValues,
            value => helper.DelimitIdentifier(value.Column.Name));
        commandBuilder.Append(")")
            .AppendLine()
            .Append("ON ");
        for (var index = 0; index < matchColumns.Length; index++)
        {
            if (index > 0)
            {
                commandBuilder.Append(" AND ");
            }

            AppendQualifiedColumn(commandBuilder, helper, targetAlias, matchColumns[index].Name);
            commandBuilder.Append(" = ");
            AppendQualifiedColumn(commandBuilder, helper, sourceAlias, matchColumns[index].Name);
        }

        commandBuilder.AppendLine().Append("WHEN MATCHED THEN ");
        switch (matchedAction)
        {
            case BlueTuskMergeMatchedAction.Update:
                commandBuilder.Append("UPDATE SET ");
                for (var index = 0; index < updateColumns.Length; index++)
                {
                    if (index > 0)
                    {
                        commandBuilder.Append(", ");
                    }

                    commandBuilder.Append(helper.DelimitIdentifier(updateColumns[index].Name))
                        .Append(" = ");
                    AppendQualifiedColumn(commandBuilder, helper, sourceAlias, updateColumns[index].Name);
                }

                break;
            case BlueTuskMergeMatchedAction.Delete:
                commandBuilder.Append("DELETE");
                break;
            case BlueTuskMergeMatchedAction.DoNothing:
                commandBuilder.Append("DO NOTHING");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(matchedAction), matchedAction, null);
        }

        commandBuilder.AppendLine()
            .Append("WHEN NOT MATCHED THEN INSERT (");
        AppendItems(
            commandBuilder,
            insertedValues,
            value => helper.DelimitIdentifier(value.Column.Name));
        commandBuilder.Append(") VALUES (");
        AppendItems(
            commandBuilder,
            insertedValues,
            value => $"{helper.DelimitIdentifier(sourceAlias)}.{helper.DelimitIdentifier(value.Column.Name)}");
        commandBuilder.Append(")");

        var command = commandBuilder.Build();
        return new BlueTuskMergeCommandPlan(commandBuilder.ToString()!, command, parameterValues);
    }

    private static BlueTuskMergeValue[] GetInsertedValues(
        IEntityType entityType,
        ITable relationalTable,
        MemberInitExpression initializer,
        string parameterName)
    {
        var values = new BlueTuskMergeValue[initializer.Bindings.Count];
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        var columnNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < initializer.Bindings.Count; index++)
        {
            if (initializer.Bindings[index] is not MemberAssignment assignment
                || !propertyNames.Add(assignment.Member.Name)
                || entityType.FindProperty(assignment.Member.Name) is not { } property
                || relationalTable.FindColumn(property) is not { } column
                || !columnNames.Add(column.Name))
            {
                throw new ArgumentException(
                    "PostgreSQL MERGE values must assign distinct scalar properties mapped to the target table.",
                    parameterName);
            }

            values[index] = new BlueTuskMergeValue(property, column, assignment.Expression);
        }

        return values;
    }

    private static IColumn[] GetSelectedColumns(
        IEntityType entityType,
        ITable relationalTable,
        LambdaExpression selector,
        IReadOnlyList<BlueTuskMergeValue> insertedValues,
        string parameterName)
    {
        if (selector.Parameters.Count != 1 || selector.Parameters[0].Type != entityType.ClrType)
        {
            throw new ArgumentException(
                "PostgreSQL MERGE selectors must select properties from the target entity.",
                parameterName);
        }

        var selectedExpressions = selector.Body switch
        {
            NewExpression tuple => tuple.Arguments,
            MethodCallExpression
            {
                Method.DeclaringType: not null,
                Method.Name: nameof(ValueTuple.Create),
            } tuple when tuple.Method.DeclaringType == typeof(ValueTuple) => tuple.Arguments,
            _ => [selector.Body],
        };
        if (selectedExpressions.Count == 0)
        {
            throw new ArgumentException(
                "PostgreSQL MERGE selectors must name at least one mapped property.",
                parameterName);
        }

        var insertedPropertyNames = insertedValues
            .Select(value => value.Property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var selectedPropertyNames = new HashSet<string>(StringComparer.Ordinal);
        var columns = new IColumn[selectedExpressions.Count];
        for (var index = 0; index < selectedExpressions.Count; index++)
        {
            var expression = selectedExpressions[index];
            while (expression is UnaryExpression
                {
                    NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
                } conversion)
            {
                expression = conversion.Operand;
            }

            if (expression is not MemberExpression member
                || member.Expression != selector.Parameters[0]
                || !selectedPropertyNames.Add(member.Member.Name)
                || !insertedPropertyNames.Contains(member.Member.Name)
                || entityType.FindProperty(member.Member.Name) is not { } property
                || relationalTable.FindColumn(property) is not { } column)
            {
                throw new ArgumentException(
                    "PostgreSQL MERGE selectors must name distinct, initialized scalar properties mapped to the target table.",
                    parameterName);
            }

            columns[index] = column;
        }

        return columns;
    }

    private static object? Evaluate(Expression expression)
        => Expression.Lambda<Func<object?>>(
                Expression.Convert(expression, typeof(object)))
            .Compile()
            .Invoke();

    private static void AppendItems<T>(
        IRelationalCommandBuilder builder,
        IReadOnlyList<T> items,
        Func<T, string> render)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(render(items[index]));
        }
    }

    private static void AppendQualifiedColumn(
        IRelationalCommandBuilder builder,
        ISqlGenerationHelper helper,
        string alias,
        string column)
        => builder.Append(helper.DelimitIdentifier(alias))
            .Append(".")
            .Append(helper.DelimitIdentifier(column));

    private sealed record BlueTuskMergeValue(
        IProperty Property,
        IColumn Column,
        Expression Value);
}

#pragma warning restore EF1001
