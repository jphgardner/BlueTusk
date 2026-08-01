namespace BlueTusk.EntityFrameworkCore.Routines;

/// <summary>The PostgreSQL routine kind represented by model and migration metadata.</summary>
public enum BlueTuskRoutineKind
{
    /// <summary>A PostgreSQL function, including a user-defined window function.</summary>
    Function,

    /// <summary>A PostgreSQL procedure invoked with <c>CALL</c>.</summary>
    Procedure,
}

/// <summary>The mode of a PostgreSQL routine parameter.</summary>
public enum BlueTuskRoutineParameterMode
{
    /// <summary>An input parameter.</summary>
    In,

    /// <summary>An output parameter.</summary>
    Out,

    /// <summary>A parameter that carries a value in and out.</summary>
    InOut,

    /// <summary>A variadic input parameter.</summary>
    Variadic,
}

/// <summary>The optimizer volatility promise of a PostgreSQL function.</summary>
public enum BlueTuskFunctionVolatility
{
    /// <summary>The function may change within one scan or have side effects.</summary>
    Volatile,

    /// <summary>The function is stable within one scan.</summary>
    Stable,

    /// <summary>The function depends only on its arguments.</summary>
    Immutable,
}

/// <summary>The parallel-query safety classification of a PostgreSQL function.</summary>
public enum BlueTuskFunctionParallelSafety
{
    /// <summary>The function forces serial execution.</summary>
    Unsafe,

    /// <summary>The function may run only in the parallel group leader.</summary>
    Restricted,

    /// <summary>The function may run in parallel workers.</summary>
    Safe,
}

/// <summary>A parameter in a model-authored PostgreSQL routine.</summary>
public sealed record BlueTuskRoutineParameterDefinition(
    string? Name,
    string StoreType,
    BlueTuskRoutineParameterMode Mode = BlueTuskRoutineParameterMode.In,
    string? DefaultSql = null);

/// <summary>A routine-local PostgreSQL configuration assignment.</summary>
public sealed record BlueTuskRoutineConfigurationDefinition(
    string Name,
    string ValueSql);

/// <summary>
/// A provider-owned PostgreSQL function or procedure. SQL fragments are trusted model-time input.
/// </summary>
public sealed record BlueTuskRoutineDefinition(
    BlueTuskRoutineKind Kind,
    string Name,
    string? Schema,
    string InputArgumentTypesSql,
    string IdentityArgumentsSql,
    string ArgumentsSql,
    string? ResultSql,
    string CreateOrReplaceSql,
    bool IsWindow = false,
    bool HasTrackedBodyDependencies = false);

/// <summary>Provider-owned PostgreSQL functions and procedures.</summary>
public sealed record BlueTuskRoutineDefinitionSet(IReadOnlyList<BlueTuskRoutineDefinition> Routines)
{
    /// <summary>An empty routine definition set.</summary>
    public static BlueTuskRoutineDefinitionSet Empty { get; } = new([]);
}
