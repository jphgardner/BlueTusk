namespace BlueTusk.EntityFrameworkCore.UserDefinedTypes;

/// <summary>Builds PostgreSQL domain metadata.</summary>
public sealed class BlueTuskDomainBuilder
{
    private readonly List<BlueTuskDomainConstraintDefinition> _constraints = [];

    internal BlueTuskDomainBuilder(string name, string? schema, string baseStoreType)
    {
        Name = name;
        Schema = schema;
        BaseStoreType = baseStoreType;
    }

    private string Name { get; }

    private string? Schema { get; }

    private string BaseStoreType { get; }

    private string? Collation { get; set; }

    private string? DefaultSql { get; set; }

    private bool IsNotNull { get; set; }

    /// <summary>Sets an optional schema-qualified collation.</summary>
    public BlueTuskDomainBuilder UseCollation(string collation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collation);
        Collation = collation;
        return this;
    }

    /// <summary>Sets a trusted SQL default expression stored in the application model.</summary>
    public BlueTuskDomainBuilder HasDefaultSql(string defaultSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultSql);
        DefaultSql = defaultSql;
        return this;
    }

    /// <summary>Marks the domain itself as not accepting null values.</summary>
    public BlueTuskDomainBuilder IsRequired(bool required = true)
    {
        IsNotNull = required;
        return this;
    }

    /// <summary>Adds a named trusted SQL check expression.</summary>
    public BlueTuskDomainBuilder HasCheckConstraint(
        string name,
        string checkSql,
        bool isValidated = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkSql);
        _constraints.Add(new BlueTuskDomainConstraintDefinition(name, checkSql, isValidated));
        return this;
    }

    internal BlueTuskDomainTypeDefinition Build() =>
        new(Name, Schema, BaseStoreType, Collation, DefaultSql, IsNotNull, _constraints.ToArray());
}

/// <summary>Builds standalone PostgreSQL composite-type metadata.</summary>
public sealed class BlueTuskCompositeTypeBuilder
{
    private readonly List<BlueTuskCompositeAttributeDefinition> _attributes = [];

    internal BlueTuskCompositeTypeBuilder(string name, string? schema)
    {
        Name = name;
        Schema = schema;
    }

    private string Name { get; }

    private string? Schema { get; }

    /// <summary>Adds an ordered composite attribute with an optional schema-qualified collation.</summary>
    public BlueTuskCompositeTypeBuilder HasAttribute(
        string name,
        string storeType,
        string? collation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);
        if (collation is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(collation);
        }

        _attributes.Add(new BlueTuskCompositeAttributeDefinition(name, storeType, collation));
        return this;
    }

    internal BlueTuskCompositeTypeDefinition Build() => new(Name, Schema, _attributes.ToArray());
}

/// <summary>Builds PostgreSQL range-type metadata.</summary>
public sealed class BlueTuskRangeTypeBuilder
{
    private BlueTuskQualifiedName _multirangeType;

    internal BlueTuskRangeTypeBuilder(
        string name,
        string? schema,
        string subtypeName,
        string? subtypeSchema)
    {
        Name = name;
        Schema = schema;
        Subtype = new BlueTuskQualifiedName(subtypeName, subtypeSchema);
        _multirangeType = new BlueTuskQualifiedName(GetDefaultMultirangeName(name), schema);
    }

    private string Name { get; }

    private string? Schema { get; }

    private BlueTuskQualifiedName Subtype { get; }

    private BlueTuskQualifiedName? SubtypeOperatorClass { get; set; }

    private BlueTuskQualifiedName? Collation { get; set; }

    private BlueTuskQualifiedName? CanonicalFunction { get; set; }

    private BlueTuskQualifiedName? SubtypeDifferenceFunction { get; set; }

    /// <summary>Uses a non-default B-tree operator class for the subtype.</summary>
    public BlueTuskRangeTypeBuilder UseSubtypeOperatorClass(string name, string? schema = null)
    {
        SubtypeOperatorClass = new BlueTuskQualifiedName(name, schema);
        return this;
    }

    /// <summary>Uses a collation for a collatable range subtype.</summary>
    public BlueTuskRangeTypeBuilder UseCollation(string name, string? schema = null)
    {
        Collation = new BlueTuskQualifiedName(name, schema);
        return this;
    }

    /// <summary>References an existing range canonicalization function.</summary>
    public BlueTuskRangeTypeBuilder HasCanonicalFunction(string name, string? schema = null)
    {
        CanonicalFunction = new BlueTuskQualifiedName(name, schema);
        return this;
    }

    /// <summary>References an existing subtype-difference function.</summary>
    public BlueTuskRangeTypeBuilder HasSubtypeDifferenceFunction(string name, string? schema = null)
    {
        SubtypeDifferenceFunction = new BlueTuskQualifiedName(name, schema);
        return this;
    }

    /// <summary>Sets the name and schema of the paired multirange type.</summary>
    public BlueTuskRangeTypeBuilder HasMultirangeType(string name, string? schema = null)
    {
        _multirangeType = new BlueTuskQualifiedName(name, schema ?? Schema);
        return this;
    }

    internal BlueTuskRangeTypeDefinition Build() =>
        new(
            Name,
            Schema,
            Subtype,
            SubtypeOperatorClass,
            Collation,
            CanonicalFunction,
            SubtypeDifferenceFunction,
            _multirangeType);

    internal static string GetDefaultMultirangeName(string rangeName)
    {
        var index = rangeName.IndexOf("range", StringComparison.Ordinal);
        return index < 0
            ? $"{rangeName}_multirange"
            : string.Concat(rangeName.AsSpan(0, index), "multirange", rangeName.AsSpan(index + "range".Length));
    }
}
