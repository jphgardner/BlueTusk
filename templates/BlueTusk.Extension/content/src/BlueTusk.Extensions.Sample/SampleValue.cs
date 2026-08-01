namespace BlueTusk.Extensions.Sample;

/// <summary>A lossless CLR value for PostgreSQL sample_type.</summary>
public sealed record SampleValue
{
    public SampleValue(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value;
}
