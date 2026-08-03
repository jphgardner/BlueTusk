namespace BlueTusk.TypeSystem;

/// <summary>
/// Marks a partial CLR type for source-generated PostgreSQL composite-codec registration.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class BlueTuskCompositeAttribute : Attribute
{
    public BlueTuskCompositeAttribute(string schema, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Schema = schema;
        Name = name;
    }

    public string Schema { get; }

    public string Name { get; }
}
