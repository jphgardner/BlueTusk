namespace BlueTusk.Data;

/// <summary>Identifies one TCP endpoint in a multi-host connection string.</summary>
public readonly record struct BlueTuskHostEndpoint(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>Controls which PostgreSQL server role is accepted during connection opening.</summary>
public enum BlueTuskTargetSessionAttributes
{
    Any,
    Primary,
    Standby,
    PreferPrimary,
    PreferStandby,
    ReadWrite,
    ReadOnly,
}

/// <summary>Controls host ordering for new physical connections.</summary>
public enum BlueTuskLoadBalanceHosts
{
    Disable,
    Random,
}
