namespace BlueTusk.EntityFrameworkCore.SchemaPrograms;

public sealed class BlueTuskOperatorBuilder
{
    internal BlueTuskOperatorBuilder(string name, string? schema)
    {
        Name = name;
        Schema = schema;
    }

    private string Name { get; }
    private string? Schema { get; }
    private string? LeftType { get; set; }
    private string? RightType { get; set; }
    private BlueTuskSchemaProgramName? Function { get; set; }
    private BlueTuskOperatorName? Commutator { get; set; }
    private BlueTuskOperatorName? Negator { get; set; }
    private BlueTuskSchemaProgramName? RestrictionFunction { get; set; }
    private BlueTuskSchemaProgramName? JoinFunction { get; set; }
    private bool SupportsHashJoins { get; set; }
    private bool SupportsMergeJoins { get; set; }

    public BlueTuskOperatorBuilder HasLeftType(string? storeType)
    {
        LeftType = storeType;
        return this;
    }

    public BlueTuskOperatorBuilder HasRightType(string storeType)
    {
        RightType = storeType;
        return this;
    }

    public BlueTuskOperatorBuilder UsesFunction(string name, string? schema = null)
    {
        Function = new BlueTuskSchemaProgramName(name, schema);
        return this;
    }

    public BlueTuskOperatorBuilder HasCommutator(string name, string? schema = null)
    {
        Commutator = new BlueTuskOperatorName(name, schema);
        return this;
    }

    public BlueTuskOperatorBuilder HasNegator(string name, string? schema = null)
    {
        Negator = new BlueTuskOperatorName(name, schema);
        return this;
    }

    public BlueTuskOperatorBuilder UsesRestrictionFunction(string name, string? schema = null)
    {
        RestrictionFunction = new BlueTuskSchemaProgramName(name, schema);
        return this;
    }

    public BlueTuskOperatorBuilder UsesJoinFunction(string name, string? schema = null)
    {
        JoinFunction = new BlueTuskSchemaProgramName(name, schema);
        return this;
    }

    public BlueTuskOperatorBuilder SupportsHashJoin(bool enabled = true)
    {
        SupportsHashJoins = enabled;
        return this;
    }

    public BlueTuskOperatorBuilder SupportsMergeJoin(bool enabled = true)
    {
        SupportsMergeJoins = enabled;
        return this;
    }

    internal BlueTuskOperatorDefinition Build() => new(
        Name,
        Schema,
        LeftType,
        RightType ?? throw new InvalidOperationException("An operator requires a right operand type."),
        Function ?? throw new InvalidOperationException("An operator requires an implementation function."),
        Commutator,
        Negator,
        RestrictionFunction,
        JoinFunction,
        SupportsHashJoins,
        SupportsMergeJoins);
}

public abstract class BlueTuskOperatorContainerBuilder<TBuilder>
    where TBuilder : BlueTuskOperatorContainerBuilder<TBuilder>
{
    private readonly List<BlueTuskOperatorMemberDefinition> _operators = [];
    private readonly List<BlueTuskOperatorFunctionDefinition> _functions = [];

    public TBuilder HasOperator(
        int strategyNumber,
        string name,
        string leftType,
        string rightType,
        string? schema = null,
        BlueTuskOperatorPurpose purpose = BlueTuskOperatorPurpose.Search,
        string? sortFamily = null,
        string? sortFamilySchema = null)
    {
        _operators.Add(new BlueTuskOperatorMemberDefinition(
            strategyNumber,
            new BlueTuskOperatorName(name, schema),
            leftType,
            rightType,
            purpose,
            sortFamily is null ? null : new BlueTuskSchemaProgramName(sortFamily, sortFamilySchema)));
        return (TBuilder)this;
    }

    public TBuilder HasFunction(
        int supportNumber,
        string name,
        string leftType,
        string rightType,
        IReadOnlyList<string> argumentTypes,
        string? schema = null)
    {
        _functions.Add(new BlueTuskOperatorFunctionDefinition(
            supportNumber,
            leftType,
            rightType,
            new BlueTuskSchemaProgramName(name, schema),
            argumentTypes));
        return (TBuilder)this;
    }

    internal IReadOnlyList<BlueTuskOperatorMemberDefinition> Operators => _operators.ToArray();
    internal IReadOnlyList<BlueTuskOperatorFunctionDefinition> Functions => _functions.ToArray();
}

public sealed class BlueTuskOperatorFamilyBuilder
    : BlueTuskOperatorContainerBuilder<BlueTuskOperatorFamilyBuilder>
{
    internal BlueTuskOperatorFamilyBuilder(string name, string? schema, string indexMethod)
    {
        Name = name;
        Schema = schema;
        IndexMethod = indexMethod;
    }

    private string Name { get; }
    private string? Schema { get; }
    private string IndexMethod { get; }

    internal BlueTuskOperatorFamilyDefinition Build() => new(
        Name,
        Schema,
        IndexMethod,
        Operators,
        Functions);
}

public sealed class BlueTuskOperatorClassBuilder
    : BlueTuskOperatorContainerBuilder<BlueTuskOperatorClassBuilder>
{
    internal BlueTuskOperatorClassBuilder(string name, string? schema, string dataType, string indexMethod)
    {
        Name = name;
        Schema = schema;
        DataType = dataType;
        IndexMethod = indexMethod;
    }

    private string Name { get; }
    private string? Schema { get; }
    private string DataType { get; }
    private string IndexMethod { get; }
    private bool IsDefault { get; set; }
    private BlueTuskSchemaProgramName? Family { get; set; }
    private string? StorageType { get; set; }

    public BlueTuskOperatorClassBuilder IsDefaultForType(bool enabled = true)
    {
        IsDefault = enabled;
        return this;
    }

    public BlueTuskOperatorClassBuilder IsInFamily(string name, string? schema = null)
    {
        Family = new BlueTuskSchemaProgramName(name, schema);
        return this;
    }

    public BlueTuskOperatorClassBuilder Stores(string? storeType)
    {
        StorageType = storeType;
        return this;
    }

    internal BlueTuskOperatorClassDefinition Build() => new(
        Name,
        Schema,
        IndexMethod,
        DataType,
        IsDefault,
        Family,
        Operators,
        Functions,
        StorageType);
}

public sealed class BlueTuskCastBuilder
{
    internal BlueTuskCastBuilder(string sourceType, string targetType)
    {
        SourceType = sourceType;
        TargetType = targetType;
    }

    private string SourceType { get; }
    private string TargetType { get; }
    private BlueTuskCastMethod Method { get; set; } = BlueTuskCastMethod.InOut;
    private BlueTuskCastFunctionDefinition? Function { get; set; }
    private BlueTuskCastContext Context { get; set; }

    public BlueTuskCastBuilder UsesFunction(
        string name,
        IReadOnlyList<string> argumentTypes,
        string? schema = null)
    {
        Method = BlueTuskCastMethod.Function;
        Function = new BlueTuskCastFunctionDefinition(
            new BlueTuskSchemaProgramName(name, schema), argumentTypes);
        return this;
    }

    public BlueTuskCastBuilder IsBinaryCoercible()
    {
        Method = BlueTuskCastMethod.Binary;
        Function = null;
        return this;
    }

    public BlueTuskCastBuilder UsesInputOutput()
    {
        Method = BlueTuskCastMethod.InOut;
        Function = null;
        return this;
    }

    public BlueTuskCastBuilder IsAssignment(bool enabled = true)
    {
        Context = enabled ? BlueTuskCastContext.Assignment : BlueTuskCastContext.Explicit;
        return this;
    }

    public BlueTuskCastBuilder IsImplicit(bool enabled = true)
    {
        Context = enabled ? BlueTuskCastContext.Implicit : BlueTuskCastContext.Explicit;
        return this;
    }

    internal BlueTuskCastDefinition Build() => new(SourceType, TargetType, Method, Function, Context);
}

public sealed class BlueTuskAggregateBuilder
{
    internal BlueTuskAggregateBuilder(string name, string? schema, string identityArgumentsSql)
    {
        Name = name;
        Schema = schema;
        IdentityArgumentsSql = identityArgumentsSql;
    }

    private string Name { get; }
    private string? Schema { get; }
    private string IdentityArgumentsSql { get; }
    private BlueTuskAggregateKind Kind { get; set; }
    private BlueTuskSchemaProgramName? TransitionFunction { get; set; }
    private string? StateType { get; set; }
    private int? StateSpace { get; set; }
    private BlueTuskSchemaProgramName? FinalFunction { get; set; }
    private bool FinalFunctionExtra { get; set; }
    private BlueTuskAggregateFinalFunctionModify FinalFunctionModify { get; set; }
    private BlueTuskSchemaProgramName? CombineFunction { get; set; }
    private BlueTuskSchemaProgramName? SerialFunction { get; set; }
    private BlueTuskSchemaProgramName? DeserialFunction { get; set; }
    private string? InitialCondition { get; set; }
    private BlueTuskSchemaProgramName? MovingTransitionFunction { get; set; }
    private BlueTuskSchemaProgramName? MovingInverseFunction { get; set; }
    private string? MovingStateType { get; set; }
    private int? MovingStateSpace { get; set; }
    private BlueTuskSchemaProgramName? MovingFinalFunction { get; set; }
    private bool MovingFinalFunctionExtra { get; set; }
    private BlueTuskAggregateFinalFunctionModify MovingFinalFunctionModify { get; set; }
    private string? MovingInitialCondition { get; set; }
    private BlueTuskOperatorName? SortOperator { get; set; }
    private BlueTuskAggregateParallelSafety ParallelSafety { get; set; }

    public BlueTuskAggregateBuilder UsesState(string transitionFunction, string stateType, string? schema = null)
    {
        TransitionFunction = new BlueTuskSchemaProgramName(transitionFunction, schema);
        StateType = stateType;
        return this;
    }

    public BlueTuskAggregateBuilder HasStateSpace(int? bytes)
    {
        StateSpace = bytes;
        return this;
    }

    public BlueTuskAggregateBuilder UsesFinalFunction(
        string name,
        string? schema = null,
        bool receivesExtraArguments = false,
        BlueTuskAggregateFinalFunctionModify modify = BlueTuskAggregateFinalFunctionModify.ReadOnly)
    {
        FinalFunction = new BlueTuskSchemaProgramName(name, schema);
        FinalFunctionExtra = receivesExtraArguments;
        FinalFunctionModify = modify;
        return this;
    }

    public BlueTuskAggregateBuilder SupportsPartialAggregation(
        string combineFunction,
        string? schema = null,
        string? serialFunction = null,
        string? deserialFunction = null)
    {
        CombineFunction = new BlueTuskSchemaProgramName(combineFunction, schema);
        SerialFunction = serialFunction is null ? null : new BlueTuskSchemaProgramName(serialFunction, schema);
        DeserialFunction = deserialFunction is null
            ? null
            : new BlueTuskSchemaProgramName(deserialFunction, schema);
        return this;
    }

    public BlueTuskAggregateBuilder HasInitialCondition(string? value)
    {
        InitialCondition = value;
        return this;
    }

    public BlueTuskAggregateBuilder SupportsMovingState(
        string transitionFunction,
        string inverseFunction,
        string stateType,
        string? schema = null,
        int? stateSpace = null,
        string? initialCondition = null)
    {
        MovingTransitionFunction = new BlueTuskSchemaProgramName(transitionFunction, schema);
        MovingInverseFunction = new BlueTuskSchemaProgramName(inverseFunction, schema);
        MovingStateType = stateType;
        MovingStateSpace = stateSpace;
        MovingInitialCondition = initialCondition;
        return this;
    }

    public BlueTuskAggregateBuilder UsesMovingFinalFunction(
        string name,
        string? schema = null,
        bool receivesExtraArguments = false,
        BlueTuskAggregateFinalFunctionModify modify = BlueTuskAggregateFinalFunctionModify.ReadOnly)
    {
        MovingFinalFunction = new BlueTuskSchemaProgramName(name, schema);
        MovingFinalFunctionExtra = receivesExtraArguments;
        MovingFinalFunctionModify = modify;
        return this;
    }

    public BlueTuskAggregateBuilder IsOrderedSet(bool hypothetical = false)
    {
        Kind = hypothetical ? BlueTuskAggregateKind.HypotheticalSet : BlueTuskAggregateKind.OrderedSet;
        return this;
    }

    public BlueTuskAggregateBuilder HasSortOperator(string name, string? schema = null)
    {
        SortOperator = new BlueTuskOperatorName(name, schema);
        return this;
    }

    public BlueTuskAggregateBuilder IsParallelSafe(BlueTuskAggregateParallelSafety safety)
    {
        ParallelSafety = safety;
        return this;
    }

    internal BlueTuskAggregateDefinition Build() => new(
        Name,
        Schema,
        IdentityArgumentsSql,
        Kind,
        TransitionFunction ?? throw new InvalidOperationException("An aggregate requires a transition function."),
        StateType ?? throw new InvalidOperationException("An aggregate requires a transition state type."),
        StateSpace,
        FinalFunction,
        FinalFunctionExtra,
        FinalFunctionModify,
        CombineFunction,
        SerialFunction,
        DeserialFunction,
        InitialCondition,
        MovingTransitionFunction,
        MovingInverseFunction,
        MovingStateType,
        MovingStateSpace,
        MovingFinalFunction,
        MovingFinalFunctionExtra,
        MovingFinalFunctionModify,
        MovingInitialCondition,
        SortOperator,
        ParallelSafety);
}
