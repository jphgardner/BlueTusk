namespace BlueTusk.TypeSystem;

public static class BlueTuskBuiltInTypes
{
    public static BlueTuskTypeDescriptor Int4 { get; } = new()
    {
        Id = new BlueTuskTypeId(23),
        Schema = "pg_catalog",
        Name = "int4",
        Kind = BlueTuskTypeKind.Base,
        ArrayType = new BlueTuskTypeId(1007),
    };

    public static BlueTuskTypeRegistry CreateInitialRegistry() =>
        new BlueTuskTypeRegistryBuilder()
            .Register(Int4, new BlueTuskInt32Codec())
            .Build();
}

