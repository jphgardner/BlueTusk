namespace BlueTusk.Protocol;

public readonly record struct BlueTuskProtocolVersion(ushort Major, ushort Minor)
{
    public static BlueTuskProtocolVersion Version30 { get; } = new(3, 0);

    public int ToWireValue() => (Major << 16) | Minor;

    public override string ToString() => $"{Major}.{Minor}";
}

