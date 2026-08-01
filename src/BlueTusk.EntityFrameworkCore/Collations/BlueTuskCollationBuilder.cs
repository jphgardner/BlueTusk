namespace BlueTusk.EntityFrameworkCore.Collations;

/// <summary>Builds a provider-owned PostgreSQL collation.</summary>
public sealed class BlueTuskCollationBuilder
{
    internal BlueTuskCollationBuilder(string name, string? schema)
    {
        Name = name;
        Schema = schema;
    }

    private string Name { get; }

    private string? Schema { get; }

    private BlueTuskCollationProvider? Provider { get; set; }

    private string? Locale { get; set; }

    private string? LcCollate { get; set; }

    private string? LcCtype { get; set; }

    private bool? IsDeterministicValue { get; set; }

    private string? Rules { get; set; }

    private string? Version { get; set; }

    /// <summary>Selects the locale provider.</summary>
    public BlueTuskCollationBuilder UseProvider(BlueTuskCollationProvider provider)
    {
        Provider = provider;
        return this;
    }

    /// <summary>Uses one locale for the provider's collation behavior.</summary>
    public BlueTuskCollationBuilder UseLocale(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        Locale = locale;
        LcCollate = null;
        LcCtype = null;
        return this;
    }

    /// <summary>Uses separate libc <c>LC_COLLATE</c> and <c>LC_CTYPE</c> locales.</summary>
    public BlueTuskCollationBuilder UseLibcLocales(string lcCollate, string lcCtype)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lcCollate);
        ArgumentException.ThrowIfNullOrWhiteSpace(lcCtype);
        Locale = null;
        LcCollate = lcCollate;
        LcCtype = lcCtype;
        return this;
    }

    /// <summary>Controls whether logically equal but byte-distinct strings compare as distinct.</summary>
    public BlueTuskCollationBuilder IsDeterministic(bool deterministic = true)
    {
        IsDeterministicValue = deterministic;
        return this;
    }

    /// <summary>Sets trusted ICU collation rules, supported by PostgreSQL 16 and later.</summary>
    public BlueTuskCollationBuilder HasRules(string rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rules);
        Rules = rules;
        return this;
    }

    /// <summary>Sets the provider version recorded when the collation is created.</summary>
    public BlueTuskCollationBuilder HasVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Version = version;
        return this;
    }

    internal BlueTuskCollationDefinition Build() => new(
        Name,
        Schema,
        Provider,
        Locale,
        LcCollate,
        LcCtype,
        IsDeterministicValue,
        Rules,
        Version);
}
