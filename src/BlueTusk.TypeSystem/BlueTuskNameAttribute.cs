namespace BlueTusk.TypeSystem;

/// <summary>Overrides the PostgreSQL name associated with a CLR enum member or composite member.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BlueTuskNameAttribute : Attribute
{
    public BlueTuskNameAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
    }

    public string Name { get; }
}
