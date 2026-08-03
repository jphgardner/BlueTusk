namespace BlueTusk.Extensions.LTree;

/// <summary>A PostgreSQL ltree hierarchical label path.</summary>
public sealed record BlueTuskLTree
{
    public BlueTuskLTree(string value)
    {
        Value = Validate(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static implicit operator BlueTuskLTree(string value) => new(value);

    public static explicit operator string(BlueTuskLTree value) => value.Value;

    private static string Validate(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("PostgreSQL ltree values cannot contain a null character.", parameterName);
        }

        return value;
    }
}

/// <summary>A PostgreSQL lquery hierarchical path pattern.</summary>
public sealed record BlueTuskLQuery
{
    public BlueTuskLQuery(string value)
    {
        Value = Validate(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static implicit operator BlueTuskLQuery(string value) => new(value);

    public static explicit operator string(BlueTuskLQuery value) => value.Value;

    private static string Validate(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("PostgreSQL lquery values cannot contain a null character.", parameterName);
        }

        return value;
    }
}

/// <summary>A PostgreSQL ltxtquery position-independent label expression.</summary>
public sealed record BlueTuskLTxtQuery
{
    public BlueTuskLTxtQuery(string value)
    {
        Value = Validate(value, nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static implicit operator BlueTuskLTxtQuery(string value) => new(value);

    public static explicit operator string(BlueTuskLTxtQuery value) => value.Value;

    private static string Validate(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("PostgreSQL ltxtquery values cannot contain a null character.", parameterName);
        }

        return value;
    }
}
