using System.Linq.Expressions;
using System.Text.RegularExpressions;
using BlueTusk.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Specifies PostgreSQL-specific null ordering for an index key.</summary>
public enum BlueTuskIndexNullSortOrder
{
    /// <summary>Uses the PostgreSQL default for the key's sort direction.</summary>
    Default,

    /// <summary>Places null values before non-null values.</summary>
    NullsFirst,

    /// <summary>Places null values after non-null values.</summary>
    NullsLast,
}

/// <summary>PostgreSQL-specific index configuration extensions.</summary>
public static partial class BlueTuskIndexBuilderExtensions
{
    private static readonly Regex StorageParameterNamePattern = StorageParameterNameRegex();
    private static readonly Regex StorageParameterValuePattern = StorageParameterValueRegex();

    /// <summary>Configures the PostgreSQL index access method, including extension-provided methods.</summary>
    public static IndexBuilder UseBlueTuskIndexMethod(this IndexBuilder indexBuilder, string method)
    {
        ArgumentNullException.ThrowIfNull(indexBuilder);
        ValidateQualifiedIdentifier(method, nameof(method), allowQualified: false);
        indexBuilder.Metadata.SetAnnotation(BlueTuskIndexAnnotations.Method, method);
        return indexBuilder;
    }

    /// <inheritdoc cref="UseBlueTuskIndexMethod(IndexBuilder,string)" />
    public static IndexBuilder<TEntity> UseBlueTuskIndexMethod<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        string method)
        where TEntity : class
    {
        UseBlueTuskIndexMethod((IndexBuilder)indexBuilder, method);
        return indexBuilder;
    }

    /// <summary>Configures one operator class per leading index key. Use <see langword="null" /> for the default.</summary>
    public static IndexBuilder UseBlueTuskOperatorClass(
        this IndexBuilder indexBuilder,
        params string?[] operatorClasses)
    {
        ArgumentNullException.ThrowIfNull(indexBuilder);
        ValidatePerKeyValues(indexBuilder, operatorClasses, nameof(operatorClasses), ValidateOptionalQualifiedIdentifier);
        indexBuilder.Metadata.SetAnnotation(
            BlueTuskIndexAnnotations.OperatorClasses,
            PadValues(indexBuilder, operatorClasses));
        return indexBuilder;
    }

    /// <inheritdoc cref="UseBlueTuskOperatorClass(IndexBuilder,string?[])" />
    public static IndexBuilder<TEntity> UseBlueTuskOperatorClass<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        params string?[] operatorClasses)
        where TEntity : class
    {
        UseBlueTuskOperatorClass((IndexBuilder)indexBuilder, operatorClasses);
        return indexBuilder;
    }

    /// <summary>Configures one collation per leading index key. Use <see langword="null" /> for the default.</summary>
    public static IndexBuilder UseBlueTuskCollation(
        this IndexBuilder indexBuilder,
        params string?[] collations)
    {
        ArgumentNullException.ThrowIfNull(indexBuilder);
        ValidatePerKeyValues(indexBuilder, collations, nameof(collations), ValidateOptionalQualifiedIdentifier);
        indexBuilder.Metadata.SetAnnotation(
            BlueTuskIndexAnnotations.Collations,
            PadValues(indexBuilder, collations));
        return indexBuilder;
    }

    /// <inheritdoc cref="UseBlueTuskCollation(IndexBuilder,string?[])" />
    public static IndexBuilder<TEntity> UseBlueTuskCollation<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        params string?[] collations)
        where TEntity : class
    {
        UseBlueTuskCollation((IndexBuilder)indexBuilder, collations);
        return indexBuilder;
    }

    /// <summary>Configures PostgreSQL null ordering for each leading index key.</summary>
    public static IndexBuilder HasBlueTuskNullSortOrder(
        this IndexBuilder indexBuilder,
        params BlueTuskIndexNullSortOrder[] sortOrders)
    {
        ArgumentNullException.ThrowIfNull(indexBuilder);
        ArgumentNullException.ThrowIfNull(sortOrders);
        if (sortOrders.Length > indexBuilder.Metadata.Properties.Count)
        {
            throw new ArgumentException("Null sort-order count cannot exceed the index key count.", nameof(sortOrders));
        }

        var values = new int[indexBuilder.Metadata.Properties.Count];
        for (var index = 0; index < sortOrders.Length; index++)
        {
            if (!Enum.IsDefined(sortOrders[index]))
            {
                throw new ArgumentOutOfRangeException(nameof(sortOrders), sortOrders[index], "Unknown null sort order.");
            }

            values[index] = (int)sortOrders[index];
        }

        indexBuilder.Metadata.SetAnnotation(BlueTuskIndexAnnotations.NullSortOrders, values);
        return indexBuilder;
    }

    /// <inheritdoc cref="HasBlueTuskNullSortOrder(IndexBuilder,BlueTuskIndexNullSortOrder[])" />
    public static IndexBuilder<TEntity> HasBlueTuskNullSortOrder<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        params BlueTuskIndexNullSortOrder[] sortOrders)
        where TEntity : class
    {
        HasBlueTuskNullSortOrder((IndexBuilder)indexBuilder, sortOrders);
        return indexBuilder;
    }

    /// <summary>Adds non-key properties to the PostgreSQL index's <c>INCLUDE</c> list.</summary>
    public static IndexBuilder IncludeProperties(this IndexBuilder indexBuilder, params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(indexBuilder);
        ArgumentNullException.ThrowIfNull(propertyNames);
        var validated = propertyNames.Select(
            name => indexBuilder.Metadata.DeclaringEntityType.FindProperty(name)
                ?? throw new ArgumentException(
                    $"Property '{name}' is not mapped by entity '{indexBuilder.Metadata.DeclaringEntityType.DisplayName()}'.",
                    nameof(propertyNames)))
            .Select(property => property.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (validated.Length != propertyNames.Length)
        {
            throw new ArgumentException("Included properties must be unique.", nameof(propertyNames));
        }

        if (validated.Any(name => indexBuilder.Metadata.Properties.Any(property => property.Name == name)))
        {
            throw new ArgumentException("An included property cannot also be an index key.", nameof(propertyNames));
        }

        indexBuilder.Metadata.SetAnnotation(BlueTuskIndexAnnotations.IncludeProperties, validated);
        return indexBuilder;
    }

    /// <summary>Adds non-key properties to the PostgreSQL index's <c>INCLUDE</c> list.</summary>
    public static IndexBuilder<TEntity> IncludeProperties<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        Expression<Func<TEntity, object?>> properties)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(indexBuilder);
        ArgumentNullException.ThrowIfNull(properties);
        IncludeProperties((IndexBuilder)indexBuilder, GetPropertyNames(properties));
        return indexBuilder;
    }

    /// <summary>Adds or replaces one PostgreSQL index storage parameter.</summary>
    public static IndexBuilder HasBlueTuskStorageParameter(
        this IndexBuilder indexBuilder,
        string name,
        string value)
    {
        ArgumentNullException.ThrowIfNull(indexBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!StorageParameterNamePattern.IsMatch(name))
        {
            throw new ArgumentException("Storage parameter names must be unquoted PostgreSQL identifiers.", nameof(name));
        }

        if (!StorageParameterValuePattern.IsMatch(value))
        {
            throw new ArgumentException("Storage parameter values may contain only letters, digits, '.', '_', '+', or '-'.", nameof(value));
        }

        var parameters = new Dictionary<string, string>(
            BlueTuskIndexAnnotations.DeserializeStorageParameters(
                indexBuilder.Metadata.FindAnnotation(BlueTuskIndexAnnotations.StorageParameters)?.Value as string),
            StringComparer.Ordinal)
        {
            [name] = value,
        };
        indexBuilder.Metadata.SetAnnotation(
            BlueTuskIndexAnnotations.StorageParameters,
            BlueTuskIndexAnnotations.SerializeStorageParameters(parameters));
        return indexBuilder;
    }

    /// <inheritdoc cref="HasBlueTuskStorageParameter(IndexBuilder,string,string)" />
    public static IndexBuilder<TEntity> HasBlueTuskStorageParameter<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        string name,
        string value)
        where TEntity : class
    {
        HasBlueTuskStorageParameter((IndexBuilder)indexBuilder, name, value);
        return indexBuilder;
    }

    /// <summary>Configures the B-tree, GiST, SP-GiST, or BRIN index fill factor.</summary>
    public static IndexBuilder HasBlueTuskFillFactor(this IndexBuilder indexBuilder, int fillFactor)
    {
        ArgumentNullException.ThrowIfNull(indexBuilder);
        if (fillFactor is < 10 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(fillFactor), fillFactor, "Fill factor must be between 10 and 100.");
        }

        return HasBlueTuskStorageParameter(indexBuilder, "fillfactor", fillFactor.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <inheritdoc cref="HasBlueTuskFillFactor(IndexBuilder,int)" />
    public static IndexBuilder<TEntity> HasBlueTuskFillFactor<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        int fillFactor)
        where TEntity : class
    {
        HasBlueTuskFillFactor((IndexBuilder)indexBuilder, fillFactor);
        return indexBuilder;
    }

    /// <summary>Creates and drops the index with PostgreSQL's <c>CONCURRENTLY</c> option.</summary>
    public static IndexBuilder IsBlueTuskConcurrent(this IndexBuilder indexBuilder, bool concurrent = true)
    {
        ArgumentNullException.ThrowIfNull(indexBuilder);
        indexBuilder.Metadata.SetAnnotation(BlueTuskIndexAnnotations.IsConcurrent, concurrent);
        return indexBuilder;
    }

    /// <inheritdoc cref="IsBlueTuskConcurrent(IndexBuilder,bool)" />
    public static IndexBuilder<TEntity> IsBlueTuskConcurrent<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        bool concurrent = true)
        where TEntity : class
    {
        IsBlueTuskConcurrent((IndexBuilder)indexBuilder, concurrent);
        return indexBuilder;
    }

    /// <summary>Controls whether a unique index treats null values as distinct. PostgreSQL 15 or newer is required.</summary>
    public static IndexBuilder HasBlueTuskNullsDistinct(this IndexBuilder indexBuilder, bool distinct = true)
    {
        ArgumentNullException.ThrowIfNull(indexBuilder);
        indexBuilder.Metadata.SetAnnotation(BlueTuskIndexAnnotations.NullsDistinct, distinct);
        return indexBuilder;
    }

    /// <inheritdoc cref="HasBlueTuskNullsDistinct(IndexBuilder,bool)" />
    public static IndexBuilder<TEntity> HasBlueTuskNullsDistinct<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        bool distinct = true)
        where TEntity : class
    {
        HasBlueTuskNullsDistinct((IndexBuilder)indexBuilder, distinct);
        return indexBuilder;
    }

    /// <summary>
    /// Replaces selected index keys with trusted PostgreSQL expressions. Empty entries retain the mapped column.
    /// Expressions are migration metadata and must never be formed from user input.
    /// </summary>
    public static IndexBuilder HasBlueTuskIndexExpressions(
        this IndexBuilder indexBuilder,
        params string?[] expressions)
    {
        ArgumentNullException.ThrowIfNull(indexBuilder);
        ArgumentNullException.ThrowIfNull(expressions);
        if (expressions.Length > indexBuilder.Metadata.Properties.Count)
        {
            throw new ArgumentException("Expression count cannot exceed the index key count.", nameof(expressions));
        }

        var values = new string[indexBuilder.Metadata.Properties.Count];
        for (var index = 0; index < expressions.Length; index++)
        {
            if (expressions[index] is { } expression && string.IsNullOrWhiteSpace(expression))
            {
                throw new ArgumentException("Index expressions cannot be empty or whitespace.", nameof(expressions));
            }

            values[index] = expressions[index] ?? string.Empty;
        }

        indexBuilder.Metadata.SetAnnotation(BlueTuskIndexAnnotations.Expressions, values);
        return indexBuilder;
    }

    /// <inheritdoc cref="HasBlueTuskIndexExpressions(IndexBuilder,string?[])" />
    public static IndexBuilder<TEntity> HasBlueTuskIndexExpressions<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        params string?[] expressions)
        where TEntity : class
    {
        HasBlueTuskIndexExpressions((IndexBuilder)indexBuilder, expressions);
        return indexBuilder;
    }

    private static string[] GetPropertyNames<TEntity>(Expression<Func<TEntity, object?>> expression)
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

    private static string?[] PadValues(IndexBuilder indexBuilder, string?[] values)
    {
        var padded = new string?[indexBuilder.Metadata.Properties.Count];
        Array.Copy(values, padded, values.Length);
        return padded;
    }

    private static void ValidatePerKeyValues(
        IndexBuilder indexBuilder,
        string?[] values,
        string parameterName,
        Action<string?, string> validate)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length > indexBuilder.Metadata.Properties.Count)
        {
            throw new ArgumentException("Configured value count cannot exceed the index key count.", parameterName);
        }

        foreach (var value in values)
        {
            validate(value, parameterName);
        }
    }

    private static void ValidateOptionalQualifiedIdentifier(string? value, string parameterName)
    {
        if (value is not null)
        {
            ValidateQualifiedIdentifier(value, parameterName, allowQualified: true);
        }
    }

    private static void ValidateQualifiedIdentifier(string value, string parameterName, bool allowQualified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parts = value.Split('.');
        if (parts.Any(string.IsNullOrWhiteSpace) || parts.Length > (allowQualified ? 2 : 1))
        {
            throw new ArgumentException("The value must be a valid PostgreSQL identifier or schema-qualified identifier.", parameterName);
        }
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageParameterNameRegex();

    [GeneratedRegex("^[A-Za-z0-9_.+\\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageParameterValueRegex();
}
