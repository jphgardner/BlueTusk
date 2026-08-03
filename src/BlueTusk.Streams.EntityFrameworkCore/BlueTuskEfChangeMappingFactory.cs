using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.Streams.EntityFrameworkCore;

public sealed record EfChangeMappingDiagnostic(string Code, string Message);

public sealed class EfChangeMappingValidationException : Exception
{
    public EfChangeMappingValidationException(IReadOnlyList<EfChangeMappingDiagnostic> diagnostics)
        : base(CreateMessage(diagnostics))
    {
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public IReadOnlyList<EfChangeMappingDiagnostic> Diagnostics { get; }

    private static string CreateMessage(IReadOnlyList<EfChangeMappingDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics.Count == 0
            ? "The EF change mapping is invalid."
            : "The EF change mapping is invalid: " +
              string.Join("; ", diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
    }
}

public static class BlueTuskEfChangeMappingFactory
{
    public static ChangeEntityMapping<TEntity> Create<TEntity>(
        IModel model,
        ChangeTable relation,
        ChangeMappingPolicy? policy = null)
        where TEntity : class, new()
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(relation);
        var diagnostics = new List<EfChangeMappingDiagnostic>();
        var entityType = model.FindEntityType(typeof(TEntity));
        if (entityType is null)
        {
            throw Invalid("BTSEF001", $"CLR type {typeof(TEntity).FullName} is not present in the EF model.");
        }

        var tableName = entityType.GetTableName();
        if (tableName is null)
        {
            throw Invalid("BTSEF002", $"Entity {entityType.DisplayName()} is not mapped to a table.");
        }

        var schema = entityType.GetSchema() ?? model.GetDefaultSchema() ?? "public";
        if (!string.Equals(schema, relation.Schema, StringComparison.Ordinal) ||
            !string.Equals(tableName, relation.Name, StringComparison.Ordinal))
        {
            throw Invalid(
                "BTSEF003",
                $"Entity {entityType.DisplayName()} maps to {schema}.{tableName}, not relation {relation}.");
        }

        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey is null)
        {
            throw Invalid("BTSEF004", $"Entity {entityType.DisplayName()} has no primary key.");
        }

        var storeObject = StoreObjectIdentifier.Table(tableName, schema);
        var relationColumns = relation.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var bindings = new List<(PropertyInfo Property, string Column)>();
        var mappedColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in entityType.GetProperties())
        {
            var columnName = property.GetColumnName(storeObject);
            if (columnName is null)
            {
                continue;
            }

            if (!relationColumns.ContainsKey(columnName))
            {
                diagnostics.Add(new EfChangeMappingDiagnostic(
                    "BTSEF005",
                    $"EF property {property.Name} maps to column {columnName}, which is not published by {relation}."));
                continue;
            }

            if (property.PropertyInfo is not { SetMethod.IsPublic: true } propertyInfo)
            {
                diagnostics.Add(new EfChangeMappingDiagnostic(
                    "BTSEF006",
                    $"EF property {property.Name} must have a public CLR setter for typed CDC materialisation."));
                continue;
            }

            if (!mappedColumns.Add(columnName))
            {
                diagnostics.Add(new EfChangeMappingDiagnostic(
                    "BTSEF007",
                    $"More than one EF property maps to published column {columnName}."));
                continue;
            }

            bindings.Add((propertyInfo, columnName));
        }

        var keyColumns = new List<string>(primaryKey.Properties.Count);
        foreach (var keyProperty in primaryKey.Properties)
        {
            var columnName = keyProperty.GetColumnName(storeObject);
            if (columnName is null || !relationColumns.ContainsKey(columnName))
            {
                diagnostics.Add(new EfChangeMappingDiagnostic(
                    "BTSEF008",
                    $"Primary-key property {keyProperty.Name} is not present in the published relation."));
            }
            else
            {
                keyColumns.Add(columnName);
            }
        }

        if (diagnostics.Count > 0)
        {
            throw new EfChangeMappingValidationException(diagnostics.AsReadOnly());
        }

        var builder = new ChangeEntityMappingBuilder<TEntity>()
            .UseConventions(false)
            .ToTable(schema, tableName)
            .HasKey(keyColumns.ToArray());
        foreach (var binding in bindings)
        {
            AddProperty(builder, binding.Property, binding.Column);
        }

        return builder.Build(relation, policy);
    }

    private static void AddProperty<TEntity>(
        ChangeEntityMappingBuilder<TEntity> builder,
        PropertyInfo property,
        string columnName)
        where TEntity : class, new()
    {
        var target = Expression.Parameter(typeof(TEntity), "entity");
        var delegateType = typeof(Func<,>).MakeGenericType(typeof(TEntity), property.PropertyType);
        var lambda = Expression.Lambda(delegateType, Expression.Property(target, property), target);
        var propertyMethod = typeof(ChangeEntityMappingBuilder<TEntity>).GetMethods()
            .Single(method =>
                string.Equals(method.Name, nameof(ChangeEntityMappingBuilder<TEntity>.Property), StringComparison.Ordinal) &&
                method.IsGenericMethodDefinition &&
                method.GetParameters().Length == 4);
        _ = propertyMethod
            .MakeGenericMethod(property.PropertyType)
            .Invoke(builder, [lambda, columnName, null, null]);
    }

    private static EfChangeMappingValidationException Invalid(string code, string message) =>
        new(Array.AsReadOnly([new EfChangeMappingDiagnostic(code, message)]));
}
