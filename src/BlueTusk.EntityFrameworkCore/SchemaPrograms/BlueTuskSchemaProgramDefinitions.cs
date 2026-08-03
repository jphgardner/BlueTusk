namespace BlueTusk.EntityFrameworkCore.SchemaPrograms;

/// <summary>A schema-qualified PostgreSQL schema-object name.</summary>
public sealed record BlueTuskSchemaProgramName(string Name, string? Schema = null);

/// <summary>A schema-qualified PostgreSQL operator symbol.</summary>
public sealed record BlueTuskOperatorName(string Name, string? Schema = null);

/// <summary>A PostgreSQL operator.</summary>
public sealed record BlueTuskOperatorDefinition(
    string Name,
    string? Schema,
    string? LeftType,
    string RightType,
    BlueTuskSchemaProgramName Function,
    BlueTuskOperatorName? Commutator,
    BlueTuskOperatorName? Negator,
    BlueTuskSchemaProgramName? RestrictionFunction,
    BlueTuskSchemaProgramName? JoinFunction,
    bool SupportsHashJoins,
    bool SupportsMergeJoins);

/// <summary>The purpose of an operator-family strategy member.</summary>
public enum BlueTuskOperatorPurpose
{
    /// <summary>The operator is used to search an index.</summary>
    Search,

    /// <summary>The operator defines ordering described by another B-tree family.</summary>
    OrderBy,
}

/// <summary>An operator member of an operator family or class.</summary>
public sealed record BlueTuskOperatorMemberDefinition(
    int StrategyNumber,
    BlueTuskOperatorName Operator,
    string LeftType,
    string RightType,
    BlueTuskOperatorPurpose Purpose = BlueTuskOperatorPurpose.Search,
    BlueTuskSchemaProgramName? SortFamily = null);

/// <summary>A support-function member of an operator family or class.</summary>
public sealed record BlueTuskOperatorFunctionDefinition(
    int SupportNumber,
    string LeftType,
    string RightType,
    BlueTuskSchemaProgramName Function,
    IReadOnlyList<string> ArgumentTypes);

/// <summary>A PostgreSQL operator family and its loose cross-type members.</summary>
public sealed record BlueTuskOperatorFamilyDefinition(
    string Name,
    string? Schema,
    string IndexMethod,
    IReadOnlyList<BlueTuskOperatorMemberDefinition> Operators,
    IReadOnlyList<BlueTuskOperatorFunctionDefinition> Functions);

/// <summary>A PostgreSQL index operator class.</summary>
public sealed record BlueTuskOperatorClassDefinition(
    string Name,
    string? Schema,
    string IndexMethod,
    string DataType,
    bool IsDefault,
    BlueTuskSchemaProgramName? Family,
    IReadOnlyList<BlueTuskOperatorMemberDefinition> Operators,
    IReadOnlyList<BlueTuskOperatorFunctionDefinition> Functions,
    string? StorageType);

/// <summary>The implementation mechanism of a PostgreSQL cast.</summary>
public enum BlueTuskCastMethod
{
    /// <summary>The cast calls a PostgreSQL function.</summary>
    Function,

    /// <summary>The types are binary-coercible and no function is called.</summary>
    Binary,

    /// <summary>The source output and target input functions perform the conversion.</summary>
    InOut,
}

/// <summary>The contexts in which PostgreSQL may apply a cast.</summary>
public enum BlueTuskCastContext
{
    /// <summary>The cast must be requested explicitly.</summary>
    Explicit,

    /// <summary>The cast may be used for assignment coercion.</summary>
    Assignment,

    /// <summary>The cast may be selected implicitly in expressions.</summary>
    Implicit,
}

/// <summary>A PostgreSQL cast implementation function and its overload identity.</summary>
public sealed record BlueTuskCastFunctionDefinition(
    BlueTuskSchemaProgramName Function,
    IReadOnlyList<string> ArgumentTypes);

/// <summary>A PostgreSQL cast between two store types.</summary>
public sealed record BlueTuskCastDefinition(
    string SourceType,
    string TargetType,
    BlueTuskCastMethod Method,
    BlueTuskCastFunctionDefinition? Function,
    BlueTuskCastContext Context);

/// <summary>The PostgreSQL aggregate kind.</summary>
public enum BlueTuskAggregateKind
{
    /// <summary>An ordinary aggregate.</summary>
    Ordinary,

    /// <summary>An ordered-set aggregate.</summary>
    OrderedSet,

    /// <summary>A hypothetical-set aggregate.</summary>
    HypotheticalSet,
}

/// <summary>Whether an aggregate final function can modify transition state.</summary>
public enum BlueTuskAggregateFinalFunctionModify
{
    /// <summary>The final function does not modify transition state.</summary>
    ReadOnly,

    /// <summary>The state can be shared between compatible final functions.</summary>
    Shareable,

    /// <summary>The final function may modify transition state.</summary>
    ReadWrite,
}

/// <summary>The parallel-query safety classification of an aggregate.</summary>
public enum BlueTuskAggregateParallelSafety
{
    /// <summary>The aggregate forces serial execution.</summary>
    Unsafe,

    /// <summary>The aggregate may run only in the parallel group leader.</summary>
    Restricted,

    /// <summary>The aggregate may run in parallel workers.</summary>
    Safe,
}

/// <summary>A PostgreSQL aggregate, including ordered and moving-state forms.</summary>
public sealed record BlueTuskAggregateDefinition(
    string Name,
    string? Schema,
    string IdentityArgumentsSql,
    BlueTuskAggregateKind Kind,
    BlueTuskSchemaProgramName TransitionFunction,
    string StateType,
    int? StateSpace,
    BlueTuskSchemaProgramName? FinalFunction,
    bool FinalFunctionExtra,
    BlueTuskAggregateFinalFunctionModify FinalFunctionModify,
    BlueTuskSchemaProgramName? CombineFunction,
    BlueTuskSchemaProgramName? SerialFunction,
    BlueTuskSchemaProgramName? DeserialFunction,
    string? InitialCondition,
    BlueTuskSchemaProgramName? MovingTransitionFunction,
    BlueTuskSchemaProgramName? MovingInverseFunction,
    string? MovingStateType,
    int? MovingStateSpace,
    BlueTuskSchemaProgramName? MovingFinalFunction,
    bool MovingFinalFunctionExtra,
    BlueTuskAggregateFinalFunctionModify MovingFinalFunctionModify,
    string? MovingInitialCondition,
    BlueTuskOperatorName? SortOperator,
    BlueTuskAggregateParallelSafety ParallelSafety);

/// <summary>Provider-owned PostgreSQL operators, index semantics, casts, and aggregates.</summary>
public sealed record BlueTuskSchemaProgramDefinitionSet(
    IReadOnlyList<BlueTuskOperatorDefinition> Operators,
    IReadOnlyList<BlueTuskOperatorFamilyDefinition> OperatorFamilies,
    IReadOnlyList<BlueTuskOperatorClassDefinition> OperatorClasses,
    IReadOnlyList<BlueTuskCastDefinition> Casts,
    IReadOnlyList<BlueTuskAggregateDefinition> Aggregates)
{
    /// <summary>An empty definition set.</summary>
    public static BlueTuskSchemaProgramDefinitionSet Empty { get; } = new([], [], [], [], []);
}
