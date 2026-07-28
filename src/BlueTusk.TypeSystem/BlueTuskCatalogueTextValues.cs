namespace BlueTusk.TypeSystem;

/// <summary>A PostgreSQL portal name stored as <c>refcursor</c>.</summary>
public readonly record struct BlueTuskRefCursor
{
    public BlueTuskRefCursor(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>An opaque PostgreSQL expression tree stored as <c>pg_node_tree</c>.</summary>
public readonly record struct BlueTuskNodeTree
{
    public BlueTuskNodeTree(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>A PostgreSQL SQL/JSON path expression.</summary>
public readonly record struct BlueTuskJsonPath
{
    public BlueTuskJsonPath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>The one-byte PostgreSQL internal <c>"char"</c> value.</summary>
public readonly record struct BlueTuskInternalChar(byte Value);
