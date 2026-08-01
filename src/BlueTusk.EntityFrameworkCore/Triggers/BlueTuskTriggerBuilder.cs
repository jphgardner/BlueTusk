using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.Triggers;

public class BlueTuskTriggerBuilder
{
    private readonly IMutableEntityType _entityType;
    private readonly List<BlueTuskTriggerEventDefinition> _events = [];
    private readonly List<string> _arguments = [];

    internal BlueTuskTriggerBuilder(IMutableEntityType entityType, string name)
    {
        _entityType = entityType;
        Name = name;
    }

    private string Name { get; }

    private BlueTuskTriggerTiming Timing { get; set; } = BlueTuskTriggerTiming.Before;

    private BlueTuskTriggerOrientation Orientation { get; set; } = BlueTuskTriggerOrientation.Statement;

    private string? FunctionName { get; set; }

    private string? FunctionSchema { get; set; }

    private string? WhenSql { get; set; }

    private string? OldTransitionTable { get; set; }

    private string? NewTransitionTable { get; set; }

    private bool IsConstraintValue { get; set; }

    private string? ReferencedTable { get; set; }

    private string? ReferencedTableSchema { get; set; }

    private bool IsDeferrableValue { get; set; }

    private bool IsInitiallyDeferredValue { get; set; }

    private BlueTuskTriggerEnabledMode EnabledMode { get; set; }

    private string? ExtensionDependency { get; set; }

    public BlueTuskTriggerBuilder UseTiming(BlueTuskTriggerTiming timing)
    {
        Timing = timing;
        return this;
    }

    public BlueTuskTriggerBuilder OnInsert() => AddEvent(BlueTuskTriggerEventKind.Insert, []);

    public BlueTuskTriggerBuilder OnUpdate(params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(propertyNames);
        foreach (var propertyName in propertyNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
            _ = _entityType.FindProperty(propertyName)
                ?? throw new ArgumentException(
                    $"Property '{propertyName}' is not mapped by entity '{_entityType.DisplayName()}'.",
                    nameof(propertyNames));
        }

        return AddEvent(BlueTuskTriggerEventKind.Update, propertyNames);
    }

    public BlueTuskTriggerBuilder OnDelete() => AddEvent(BlueTuskTriggerEventKind.Delete, []);

    public BlueTuskTriggerBuilder OnTruncate() => AddEvent(BlueTuskTriggerEventKind.Truncate, []);

    public BlueTuskTriggerBuilder ForEachRow(bool row = true)
    {
        Orientation = row ? BlueTuskTriggerOrientation.Row : BlueTuskTriggerOrientation.Statement;
        return this;
    }

    public BlueTuskTriggerBuilder ExecuteFunction(
        string name,
        string? schema = null,
        params string[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        FunctionName = name;
        FunctionSchema = schema;
        _arguments.Clear();
        _arguments.AddRange(arguments);
        return this;
    }

    public BlueTuskTriggerBuilder When(string conditionSql)
    {
        WhenSql = conditionSql;
        return this;
    }

    public BlueTuskTriggerBuilder Referencing(
        string? oldTable = null,
        string? newTable = null)
    {
        OldTransitionTable = oldTable;
        NewTransitionTable = newTable;
        return this;
    }

    public BlueTuskTriggerBuilder AsConstraint(
        string? referencedTable = null,
        string? referencedTableSchema = null,
        bool deferrable = false,
        bool initiallyDeferred = false)
    {
        IsConstraintValue = true;
        ReferencedTable = referencedTable;
        ReferencedTableSchema = referencedTableSchema;
        IsDeferrableValue = deferrable;
        IsInitiallyDeferredValue = initiallyDeferred;
        return this;
    }

    public BlueTuskTriggerBuilder HasEnabledMode(BlueTuskTriggerEnabledMode mode)
    {
        EnabledMode = mode;
        return this;
    }

    public BlueTuskTriggerBuilder DependsOnExtension(string extensionName)
    {
        ExtensionDependency = extensionName;
        return this;
    }

    internal BlueTuskTriggerDefinition Build() => new(
        Name,
        Timing,
        _events.ToArray(),
        Orientation,
        FunctionName,
        FunctionSchema,
        _arguments.ToArray(),
        WhenSql,
        OldTransitionTable,
        NewTransitionTable,
        IsConstraintValue,
        ReferencedTable,
        ReferencedTableSchema,
        IsDeferrableValue,
        IsInitiallyDeferredValue,
        EnabledMode,
        ExtensionDependency: ExtensionDependency);

    private BlueTuskTriggerBuilder AddEvent(
        BlueTuskTriggerEventKind kind,
        IReadOnlyList<string> updateColumns)
    {
        _events.Add(new BlueTuskTriggerEventDefinition(kind, updateColumns));
        return this;
    }
}

public sealed class BlueTuskTriggerBuilder<TEntity> : BlueTuskTriggerBuilder
    where TEntity : class
{
    internal BlueTuskTriggerBuilder(IMutableEntityType entityType, string name)
        : base(entityType, name)
    {
    }

    public BlueTuskTriggerBuilder<TEntity> OnUpdate(Expression<Func<TEntity, object?>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        base.OnUpdate(GetPropertyNames(properties));
        return this;
    }

    public new BlueTuskTriggerBuilder<TEntity> UseTiming(BlueTuskTriggerTiming timing)
    {
        base.UseTiming(timing);
        return this;
    }

    public new BlueTuskTriggerBuilder<TEntity> OnInsert()
    {
        base.OnInsert();
        return this;
    }

    public new BlueTuskTriggerBuilder<TEntity> OnDelete()
    {
        base.OnDelete();
        return this;
    }

    public new BlueTuskTriggerBuilder<TEntity> OnTruncate()
    {
        base.OnTruncate();
        return this;
    }

    public new BlueTuskTriggerBuilder<TEntity> ForEachRow(bool row = true)
    {
        base.ForEachRow(row);
        return this;
    }

    public new BlueTuskTriggerBuilder<TEntity> ExecuteFunction(
        string name,
        string? schema = null,
        params string[] arguments)
    {
        base.ExecuteFunction(name, schema, arguments);
        return this;
    }

    public new BlueTuskTriggerBuilder<TEntity> When(string conditionSql)
    {
        base.When(conditionSql);
        return this;
    }

    public new BlueTuskTriggerBuilder<TEntity> Referencing(string? oldTable = null, string? newTable = null)
    {
        base.Referencing(oldTable, newTable);
        return this;
    }

    public new BlueTuskTriggerBuilder<TEntity> AsConstraint(
        string? referencedTable = null,
        string? referencedTableSchema = null,
        bool deferrable = false,
        bool initiallyDeferred = false)
    {
        base.AsConstraint(referencedTable, referencedTableSchema, deferrable, initiallyDeferred);
        return this;
    }

    public new BlueTuskTriggerBuilder<TEntity> HasEnabledMode(BlueTuskTriggerEnabledMode mode)
    {
        base.HasEnabledMode(mode);
        return this;
    }

    public new BlueTuskTriggerBuilder<TEntity> DependsOnExtension(string extensionName)
    {
        base.DependsOnExtension(extensionName);
        return this;
    }

    private static string[] GetPropertyNames(Expression<Func<TEntity, object?>> expression)
    {
        static string ReadMember(Expression item, ParameterExpression parameter)
        {
            while (item is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            {
                item = unary.Operand;
            }

            return item is MemberExpression { Expression: var target } member && target == parameter
                ? member.Member.Name
                : throw new ArgumentException("The expression must select mapped properties directly.", nameof(expression));
        }

        return expression.Body is NewExpression creation
            ? creation.Arguments.Select(item => ReadMember(item, expression.Parameters[0])).ToArray()
            : [ReadMember(expression.Body, expression.Parameters[0])];
    }
}
