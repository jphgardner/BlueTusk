namespace BlueTusk.EntityFrameworkCore.Query;

/// <summary>A typed row returned by PostgreSQL's two-array <c>unnest</c> form.</summary>
public sealed record BlueTuskUnnestPair<TFirst, TSecond>(
    TFirst First,
    TSecond Second);

/// <summary>A typed row returned by PostgreSQL's three-array <c>unnest</c> form.</summary>
public sealed record BlueTuskUnnestTriple<TFirst, TSecond, TThird>(
    TFirst First,
    TSecond Second,
    TThird Third);

/// <summary>A typed row returned by PostgreSQL's four-array <c>unnest</c> form.</summary>
public sealed record BlueTuskUnnestQuadruple<TFirst, TSecond, TThird, TFourth>(
    TFirst First,
    TSecond Second,
    TThird Third,
    TFourth Fourth);
