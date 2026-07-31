namespace BlueTusk.Extensions.Citext;

/// <summary>A PostgreSQL citext value whose comparison semantics are supplied by the server.</summary>
public sealed record BlueTuskCitext
{
    public BlueTuskCitext(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static implicit operator BlueTuskCitext(string value) => new(value);

    public static explicit operator string(BlueTuskCitext value) => value.Value;
}
