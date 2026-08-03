using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace BlueTusk.TypeSystem;

/// <summary>
/// Preserves either the numeric or symbolic representation of a PostgreSQL object identifier alias.
/// </summary>
public readonly record struct BlueTuskObjectIdentifier
{
    private readonly uint _oid;

    public BlueTuskObjectIdentifier(uint oid)
    {
        _oid = oid;
        Name = null;
    }

    public BlueTuskObjectIdentifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _oid = 0;
        Name = name;
    }

    public bool IsNumeric => Name is null;

    public uint? Oid => IsNumeric ? _oid : null;

    public string? Name { get; }

    public override string ToString() =>
        Name ?? _oid.ToString(CultureInfo.InvariantCulture);
}

public interface IBlueTuskObjectIdentifierValue<TSelf>
    where TSelf : struct, IBlueTuskObjectIdentifierValue<TSelf>
{
    BlueTuskObjectIdentifier Identifier { get; }

    static abstract TSelf FromIdentifier(BlueTuskObjectIdentifier identifier);
}

/// <summary>A PostgreSQL function identifier (<c>regproc</c>).</summary>
public readonly record struct BlueTuskRegProc(BlueTuskObjectIdentifier Identifier) :
    IBlueTuskObjectIdentifierValue<BlueTuskRegProc>
{
    public BlueTuskRegProc(uint oid)
        : this(new BlueTuskObjectIdentifier(oid))
    {
    }

    public BlueTuskRegProc(string name)
        : this(new BlueTuskObjectIdentifier(name))
    {
    }

    public static BlueTuskRegProc FromIdentifier(BlueTuskObjectIdentifier identifier) =>
        new(identifier);

    public override string ToString() => Identifier.ToString();
}

/// <summary>A PostgreSQL function-signature identifier (<c>regprocedure</c>).</summary>
public readonly record struct BlueTuskRegProcedure(BlueTuskObjectIdentifier Identifier) :
    IBlueTuskObjectIdentifierValue<BlueTuskRegProcedure>
{
    public BlueTuskRegProcedure(uint oid)
        : this(new BlueTuskObjectIdentifier(oid))
    {
    }

    public BlueTuskRegProcedure(string name)
        : this(new BlueTuskObjectIdentifier(name))
    {
    }

    public static BlueTuskRegProcedure FromIdentifier(BlueTuskObjectIdentifier identifier) =>
        new(identifier);

    public override string ToString() => Identifier.ToString();
}

/// <summary>A PostgreSQL operator identifier without a signature (<c>regoper</c>).</summary>
public readonly record struct BlueTuskRegOper(BlueTuskObjectIdentifier Identifier) :
    IBlueTuskObjectIdentifierValue<BlueTuskRegOper>
{
    public BlueTuskRegOper(uint oid)
        : this(new BlueTuskObjectIdentifier(oid))
    {
    }

    public BlueTuskRegOper(string name)
        : this(new BlueTuskObjectIdentifier(name))
    {
    }

    public static BlueTuskRegOper FromIdentifier(BlueTuskObjectIdentifier identifier) =>
        new(identifier);

    public override string ToString() => Identifier.ToString();
}

/// <summary>A PostgreSQL operator-signature identifier (<c>regoperator</c>).</summary>
public readonly record struct BlueTuskRegOperator(BlueTuskObjectIdentifier Identifier) :
    IBlueTuskObjectIdentifierValue<BlueTuskRegOperator>
{
    public BlueTuskRegOperator(uint oid)
        : this(new BlueTuskObjectIdentifier(oid))
    {
    }

    public BlueTuskRegOperator(string name)
        : this(new BlueTuskObjectIdentifier(name))
    {
    }

    public static BlueTuskRegOperator FromIdentifier(BlueTuskObjectIdentifier identifier) =>
        new(identifier);

    public override string ToString() => Identifier.ToString();
}

/// <summary>A PostgreSQL relation identifier (<c>regclass</c>).</summary>
public readonly record struct BlueTuskRegClass(BlueTuskObjectIdentifier Identifier) :
    IBlueTuskObjectIdentifierValue<BlueTuskRegClass>
{
    public BlueTuskRegClass(uint oid)
        : this(new BlueTuskObjectIdentifier(oid))
    {
    }

    public BlueTuskRegClass(string name)
        : this(new BlueTuskObjectIdentifier(name))
    {
    }

    public static BlueTuskRegClass FromIdentifier(BlueTuskObjectIdentifier identifier) =>
        new(identifier);

    public override string ToString() => Identifier.ToString();
}

/// <summary>A PostgreSQL data-type identifier (<c>regtype</c>).</summary>
public readonly record struct BlueTuskRegType(BlueTuskObjectIdentifier Identifier) :
    IBlueTuskObjectIdentifierValue<BlueTuskRegType>
{
    public BlueTuskRegType(uint oid)
        : this(new BlueTuskObjectIdentifier(oid))
    {
    }

    public BlueTuskRegType(string name)
        : this(new BlueTuskObjectIdentifier(name))
    {
    }

    public static BlueTuskRegType FromIdentifier(BlueTuskObjectIdentifier identifier) =>
        new(identifier);

    public override string ToString() => Identifier.ToString();
}

/// <summary>A PostgreSQL text-search configuration identifier (<c>regconfig</c>).</summary>
public readonly record struct BlueTuskRegConfig(BlueTuskObjectIdentifier Identifier) :
    IBlueTuskObjectIdentifierValue<BlueTuskRegConfig>
{
    public BlueTuskRegConfig(uint oid)
        : this(new BlueTuskObjectIdentifier(oid))
    {
    }

    public BlueTuskRegConfig(string name)
        : this(new BlueTuskObjectIdentifier(name))
    {
    }

    public static BlueTuskRegConfig FromIdentifier(BlueTuskObjectIdentifier identifier) =>
        new(identifier);

    public override string ToString() => Identifier.ToString();
}

/// <summary>A PostgreSQL text-search dictionary identifier (<c>regdictionary</c>).</summary>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The type name intentionally follows PostgreSQL's canonical regdictionary alias.")]
public readonly record struct BlueTuskRegDictionary(BlueTuskObjectIdentifier Identifier) :
    IBlueTuskObjectIdentifierValue<BlueTuskRegDictionary>
{
    public BlueTuskRegDictionary(uint oid)
        : this(new BlueTuskObjectIdentifier(oid))
    {
    }

    public BlueTuskRegDictionary(string name)
        : this(new BlueTuskObjectIdentifier(name))
    {
    }

    public static BlueTuskRegDictionary FromIdentifier(BlueTuskObjectIdentifier identifier) =>
        new(identifier);

    public override string ToString() => Identifier.ToString();
}

/// <summary>A PostgreSQL namespace identifier (<c>regnamespace</c>).</summary>
public readonly record struct BlueTuskRegNamespace(BlueTuskObjectIdentifier Identifier) :
    IBlueTuskObjectIdentifierValue<BlueTuskRegNamespace>
{
    public BlueTuskRegNamespace(uint oid)
        : this(new BlueTuskObjectIdentifier(oid))
    {
    }

    public BlueTuskRegNamespace(string name)
        : this(new BlueTuskObjectIdentifier(name))
    {
    }

    public static BlueTuskRegNamespace FromIdentifier(BlueTuskObjectIdentifier identifier) =>
        new(identifier);

    public override string ToString() => Identifier.ToString();
}

/// <summary>A PostgreSQL role identifier (<c>regrole</c>).</summary>
public readonly record struct BlueTuskRegRole(BlueTuskObjectIdentifier Identifier) :
    IBlueTuskObjectIdentifierValue<BlueTuskRegRole>
{
    public BlueTuskRegRole(uint oid)
        : this(new BlueTuskObjectIdentifier(oid))
    {
    }

    public BlueTuskRegRole(string name)
        : this(new BlueTuskObjectIdentifier(name))
    {
    }

    public static BlueTuskRegRole FromIdentifier(BlueTuskObjectIdentifier identifier) =>
        new(identifier);

    public override string ToString() => Identifier.ToString();
}

/// <summary>A PostgreSQL collation identifier (<c>regcollation</c>).</summary>
public readonly record struct BlueTuskRegCollation(BlueTuskObjectIdentifier Identifier) :
    IBlueTuskObjectIdentifierValue<BlueTuskRegCollation>
{
    public BlueTuskRegCollation(uint oid)
        : this(new BlueTuskObjectIdentifier(oid))
    {
    }

    public BlueTuskRegCollation(string name)
        : this(new BlueTuskObjectIdentifier(name))
    {
    }

    public static BlueTuskRegCollation FromIdentifier(BlueTuskObjectIdentifier identifier) =>
        new(identifier);

    public override string ToString() => Identifier.ToString();
}

/// <summary>A PostgreSQL database identifier (<c>regdatabase</c>).</summary>
public readonly record struct BlueTuskRegDatabase(BlueTuskObjectIdentifier Identifier) :
    IBlueTuskObjectIdentifierValue<BlueTuskRegDatabase>
{
    public BlueTuskRegDatabase(uint oid)
        : this(new BlueTuskObjectIdentifier(oid))
    {
    }

    public BlueTuskRegDatabase(string name)
        : this(new BlueTuskObjectIdentifier(name))
    {
    }

    public static BlueTuskRegDatabase FromIdentifier(BlueTuskObjectIdentifier identifier) =>
        new(identifier);

    public override string ToString() => Identifier.ToString();
}
