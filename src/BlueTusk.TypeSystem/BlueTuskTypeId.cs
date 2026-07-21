namespace BlueTusk.TypeSystem;

/// <summary>A PostgreSQL object identifier (OID) identifying a type in one server catalogue.</summary>
public readonly record struct BlueTuskTypeId(uint Oid)
{
    public override string ToString() => Oid.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

