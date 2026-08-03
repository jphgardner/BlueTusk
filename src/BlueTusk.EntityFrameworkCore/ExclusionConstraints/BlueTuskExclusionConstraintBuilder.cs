using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BlueTusk.EntityFrameworkCore.ExclusionConstraints;

/// <summary>Builds a PostgreSQL exclusion constraint.</summary>
public class BlueTuskExclusionConstraintBuilder
{
    private readonly IMutableEntityType _entityType;
    private readonly List<BlueTuskExclusionElementDefinition> _elements = [];
    private readonly List<string> _includedProperties = [];
    private readonly Dictionary<string, string> _storageParameters = new(StringComparer.Ordinal);

    internal BlueTuskExclusionConstraintBuilder(IMutableEntityType entityType, string name)
    {
        _entityType = entityType;
        Name = name;
    }

    private string Name { get; }

    private string IndexMethod { get; set; } = "gist";

    private string? Tablespace { get; set; }

    private string? PredicateSql { get; set; }

    private bool IsDeferrableValue { get; set; }

    private bool IsInitiallyDeferredValue { get; set; }

    /// <summary>Sets the backing index access method. PostgreSQL defaults exclusion constraints to GiST.</summary>
    public BlueTuskExclusionConstraintBuilder UseIndexMethod(string method)
    {
        IndexMethod = method;
        return this;
    }

    /// <summary>Adds a mapped property as an exclusion element.</summary>
    public BlueTuskExclusionConstraintBuilder HasProperty(
        string propertyName,
        string @operator,
        Action<BlueTuskExclusionElementBuilder>? configure = null,
        string? operatorSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        _ = _entityType.FindProperty(propertyName)
            ?? throw new ArgumentException(
                $"Property '{propertyName}' is not mapped by entity '{_entityType.DisplayName()}'.",
                nameof(propertyName));
        return AddElement(propertyName, isColumn: true, @operator, configure, operatorSchema);
    }

    /// <summary>Adds a trusted PostgreSQL expression as an exclusion element.</summary>
    public BlueTuskExclusionConstraintBuilder HasExpression(
        string expressionSql,
        string @operator,
        Action<BlueTuskExclusionElementBuilder>? configure = null,
        string? operatorSchema = null) =>
        AddElement(expressionSql, isColumn: false, @operator, configure, operatorSchema);

    /// <summary>Adds mapped properties to the backing index's INCLUDE list.</summary>
    public BlueTuskExclusionConstraintBuilder IncludeProperties(params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(propertyNames);
        foreach (var propertyName in propertyNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
            _ = _entityType.FindProperty(propertyName)
                ?? throw new ArgumentException(
                    $"Property '{propertyName}' is not mapped by entity '{_entityType.DisplayName()}'.",
                    nameof(propertyNames));
            _includedProperties.Add(propertyName);
        }

        return this;
    }

    /// <summary>Adds or replaces a validated backing-index storage parameter.</summary>
    public BlueTuskExclusionConstraintBuilder HasStorageParameter(string name, string value)
    {
        _storageParameters[name] = value;
        return this;
    }

    /// <summary>Sets the backing index tablespace.</summary>
    public BlueTuskExclusionConstraintBuilder UseTablespace(string tablespace)
    {
        Tablespace = tablespace;
        return this;
    }

    /// <summary>Restricts the constraint to rows matching a trusted PostgreSQL predicate.</summary>
    public BlueTuskExclusionConstraintBuilder HasFilter(string predicateSql)
    {
        PredicateSql = predicateSql;
        return this;
    }

    /// <summary>Controls whether the constraint may be deferred and its initial transaction state.</summary>
    public BlueTuskExclusionConstraintBuilder IsDeferrable(
        bool deferrable = true,
        bool initiallyDeferred = false)
    {
        IsDeferrableValue = deferrable;
        IsInitiallyDeferredValue = initiallyDeferred;
        return this;
    }

    internal BlueTuskExclusionConstraintDefinition Build() =>
        new(
            Name,
            IndexMethod,
            _elements.ToArray(),
            _includedProperties.ToArray(),
            _storageParameters.Select(item => new BlueTuskExclusionParameterDefinition(item.Key, item.Value)).ToArray(),
            Tablespace,
            PredicateSql,
            IsDeferrableValue,
            IsInitiallyDeferredValue);

    private BlueTuskExclusionConstraintBuilder AddElement(
        string expression,
        bool isColumn,
        string @operator,
        Action<BlueTuskExclusionElementBuilder>? configure,
        string? operatorSchema)
    {
        var builder = new BlueTuskExclusionElementBuilder(expression, isColumn, @operator, operatorSchema);
        configure?.Invoke(builder);
        _elements.Add(builder.Build());
        return this;
    }
}

/// <summary>Builds one PostgreSQL exclusion-constraint element.</summary>
public sealed class BlueTuskExclusionElementBuilder
{
    internal BlueTuskExclusionElementBuilder(
        string expression,
        bool isColumn,
        string @operator,
        string? operatorSchema)
    {
        Expression = expression;
        IsColumn = isColumn;
        Operator = @operator;
        OperatorSchema = operatorSchema;
    }

    private string Expression { get; }

    private bool IsColumn { get; }

    private string Operator { get; }

    private string? OperatorSchema { get; }

    private string? Collation { get; set; }

    private string? CollationSchema { get; set; }

    private string? OperatorClass { get; set; }

    private string? OperatorClassSchema { get; set; }

    private Dictionary<string, string> OperatorClassParameters { get; } = new(StringComparer.Ordinal);

    private bool Descending { get; set; }

    private BlueTuskExclusionNullSortOrder NullSortOrder { get; set; }

    /// <summary>Sets a collation for this element.</summary>
    public BlueTuskExclusionElementBuilder UseCollation(string name, string? schema = null)
    {
        Collation = name;
        CollationSchema = schema;
        return this;
    }

    /// <summary>Sets an operator class for this element.</summary>
    public BlueTuskExclusionElementBuilder UseOperatorClass(string name, string? schema = null)
    {
        OperatorClass = name;
        OperatorClassSchema = schema;
        return this;
    }

    /// <summary>Adds or replaces a validated operator-class parameter.</summary>
    public BlueTuskExclusionElementBuilder HasOperatorClassParameter(string name, string value)
    {
        OperatorClassParameters[name] = value;
        return this;
    }

    /// <summary>Uses descending rather than ascending index order.</summary>
    public BlueTuskExclusionElementBuilder IsDescending(bool descending = true)
    {
        Descending = descending;
        return this;
    }

    /// <summary>Sets explicit null ordering for this element.</summary>
    public BlueTuskExclusionElementBuilder HasNullSortOrder(BlueTuskExclusionNullSortOrder order)
    {
        NullSortOrder = order;
        return this;
    }

    internal BlueTuskExclusionElementDefinition Build() =>
        new(
            Expression,
            IsColumn,
            IsPreformatted: false,
            Operator,
            OperatorSchema,
            Collation,
            CollationSchema,
            OperatorClass,
            OperatorClassSchema,
            OperatorClassParameters.Select(item => new BlueTuskExclusionParameterDefinition(item.Key, item.Value)).ToArray(),
            Descending,
            NullSortOrder);
}

/// <summary>Builds a PostgreSQL exclusion constraint for a typed EF entity.</summary>
public sealed class BlueTuskExclusionConstraintBuilder<TEntity> : BlueTuskExclusionConstraintBuilder
    where TEntity : class
{
    internal BlueTuskExclusionConstraintBuilder(IMutableEntityType entityType, string name)
        : base(entityType, name)
    {
    }

    /// <inheritdoc />
    public new BlueTuskExclusionConstraintBuilder<TEntity> UseIndexMethod(string method)
    {
        base.UseIndexMethod(method);
        return this;
    }

    /// <summary>Adds a directly selected mapped property as an exclusion element.</summary>
    public BlueTuskExclusionConstraintBuilder<TEntity> HasProperty<TProperty>(
        Expression<Func<TEntity, TProperty>> property,
        string @operator,
        Action<BlueTuskExclusionElementBuilder>? configure = null,
        string? operatorSchema = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        Expression body = property.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        var name = body is MemberExpression { Expression: var target } member && target == property.Parameters[0]
            ? member.Member.Name
            : throw new ArgumentException("The expression must select one mapped property directly.", nameof(property));
        base.HasProperty(name, @operator, configure, operatorSchema);
        return this;
    }

    /// <inheritdoc />
    public new BlueTuskExclusionConstraintBuilder<TEntity> HasExpression(
        string expressionSql,
        string @operator,
        Action<BlueTuskExclusionElementBuilder>? configure = null,
        string? operatorSchema = null)
    {
        base.HasExpression(expressionSql, @operator, configure, operatorSchema);
        return this;
    }

    /// <inheritdoc />
    public new BlueTuskExclusionConstraintBuilder<TEntity> IncludeProperties(params string[] propertyNames)
    {
        base.IncludeProperties(propertyNames);
        return this;
    }

    /// <inheritdoc />
    public new BlueTuskExclusionConstraintBuilder<TEntity> HasStorageParameter(string name, string value)
    {
        base.HasStorageParameter(name, value);
        return this;
    }

    /// <inheritdoc />
    public new BlueTuskExclusionConstraintBuilder<TEntity> UseTablespace(string tablespace)
    {
        base.UseTablespace(tablespace);
        return this;
    }

    /// <inheritdoc />
    public new BlueTuskExclusionConstraintBuilder<TEntity> HasFilter(string predicateSql)
    {
        base.HasFilter(predicateSql);
        return this;
    }

    /// <inheritdoc />
    public new BlueTuskExclusionConstraintBuilder<TEntity> IsDeferrable(
        bool deferrable = true,
        bool initiallyDeferred = false)
    {
        base.IsDeferrable(deferrable, initiallyDeferred);
        return this;
    }
}
