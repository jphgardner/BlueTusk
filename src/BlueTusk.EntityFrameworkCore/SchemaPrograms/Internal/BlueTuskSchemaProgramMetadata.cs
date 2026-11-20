using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.SchemaPrograms.Internal;

internal static partial class BlueTuskSchemaProgramMetadata
{
    public const string AnnotationName = "BlueTusk:SchemaPrograms";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static BlueTuskSchemaProgramDefinitionSet Get(IReadOnlyAnnotatable annotatable)
    {
        BlueTuskGuard.NotNull(annotatable);
        var json = annotatable.FindAnnotation(AnnotationName)?.Value as string;
        return string.IsNullOrWhiteSpace(json) ? BlueTuskSchemaProgramDefinitionSet.Empty : Deserialize(json);
    }

    public static string Serialize(BlueTuskSchemaProgramDefinitionSet definitions)
    {
        Validate(definitions);
        return JsonSerializer.Serialize(Normalize(definitions), SerializerOptions);
    }

    public static BlueTuskSchemaProgramDefinitionSet Deserialize(string json)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskSchemaProgramDefinitionSet>(json, SerializerOptions)
            ?? throw new ArgumentException("The schema-program definition set is empty.", nameof(json));
        Validate(definitions);
        return Normalize(definitions);
    }

    public static string Serialize<T>(T definition)
    {
        ValidateDefinition(definition);
        return JsonSerializer.Serialize(NormalizeDefinition(definition), SerializerOptions);
    }

    public static T DeserializeDefinition<T>(string json)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(json);
        var definition = JsonSerializer.Deserialize<T>(json, SerializerOptions)
            ?? throw new ArgumentException("The schema-program definition is empty.", nameof(json));
        ValidateDefinition(definition);
        return NormalizeDefinition(definition);
    }

    public static BlueTuskSchemaProgramDefinitionSet Normalize(BlueTuskSchemaProgramDefinitionSet definitions) => new(
        definitions.Operators.Select(Normalize)
            .OrderBy(item => item.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.LeftType, StringComparer.Ordinal)
            .ThenBy(item => item.RightType, StringComparer.Ordinal)
            .ToArray(),
        definitions.OperatorFamilies.Select(Normalize)
            .OrderBy(item => item.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.IndexMethod, StringComparer.Ordinal)
            .ToArray(),
        definitions.OperatorClasses.Select(Normalize)
            .OrderBy(item => item.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.IndexMethod, StringComparer.Ordinal)
            .ToArray(),
        definitions.Casts.Select(Normalize)
            .OrderBy(item => item.SourceType, StringComparer.Ordinal)
            .ThenBy(item => item.TargetType, StringComparer.Ordinal)
            .ToArray(),
        definitions.Aggregates.Select(Normalize)
            .OrderBy(item => item.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.IdentityArgumentsSql, StringComparer.Ordinal)
            .ToArray());

    public static BlueTuskOperatorDefinition Normalize(BlueTuskOperatorDefinition definition) => definition with
    {
        LeftType = NormalizeNullableFragment(definition.LeftType),
        RightType = NormalizeFragment(definition.RightType),
    };

    public static BlueTuskOperatorFamilyDefinition Normalize(BlueTuskOperatorFamilyDefinition definition) =>
        definition with
        {
            IndexMethod = definition.IndexMethod.Trim(),
            Operators = NormalizeOperators(definition.Operators),
            Functions = NormalizeFunctions(definition.Functions),
        };

    public static BlueTuskOperatorClassDefinition Normalize(BlueTuskOperatorClassDefinition definition) =>
        definition with
        {
            IndexMethod = definition.IndexMethod.Trim(),
            DataType = NormalizeFragment(definition.DataType),
            Operators = NormalizeOperators(definition.Operators),
            Functions = NormalizeFunctions(definition.Functions),
            StorageType = NormalizeNullableFragment(definition.StorageType),
        };

    public static BlueTuskCastDefinition Normalize(BlueTuskCastDefinition definition) => definition with
    {
        SourceType = NormalizeFragment(definition.SourceType),
        TargetType = NormalizeFragment(definition.TargetType),
        Function = definition.Function is null
            ? null
            : definition.Function with
            {
                ArgumentTypes = definition.Function.ArgumentTypes.Select(NormalizeFragment).ToArray(),
            },
    };

    public static BlueTuskAggregateDefinition Normalize(BlueTuskAggregateDefinition definition) => definition with
    {
        IdentityArgumentsSql = definition.IdentityArgumentsSql.Trim(),
        StateType = NormalizeFragment(definition.StateType),
        MovingStateType = NormalizeNullableFragment(definition.MovingStateType),
    };

    public static void Validate(BlueTuskSchemaProgramDefinitionSet definitions)
    {
        BlueTuskGuard.NotNull(definitions);
        BlueTuskGuard.NotNull(definitions.Operators);
        BlueTuskGuard.NotNull(definitions.OperatorFamilies);
        BlueTuskGuard.NotNull(definitions.OperatorClasses);
        BlueTuskGuard.NotNull(definitions.Casts);
        BlueTuskGuard.NotNull(definitions.Aggregates);
        ValidateUnique(definitions.Operators, OperatorKey.Create, "operator");
        ValidateUnique(definitions.OperatorFamilies, OperatorFamilyKey.Create, "operator family");
        ValidateUnique(definitions.OperatorClasses, OperatorClassKey.Create, "operator class");
        ValidateUnique(definitions.Casts, CastKey.Create, "cast");
        ValidateUnique(definitions.Aggregates, AggregateKey.Create, "aggregate");
        foreach (var definition in definitions.Operators)
        {
            Validate(definition);
        }

        foreach (var definition in definitions.OperatorFamilies)
        {
            Validate(definition);
        }

        foreach (var definition in definitions.OperatorClasses)
        {
            Validate(definition);
        }

        foreach (var definition in definitions.Casts)
        {
            Validate(definition);
        }

        foreach (var definition in definitions.Aggregates)
        {
            Validate(definition);
        }
    }

    public static void Validate(BlueTuskOperatorDefinition definition)
    {
        BlueTuskGuard.NotNull(definition);
        ValidateOperatorName(new BlueTuskOperatorName(definition.Name, definition.Schema));
        ValidateOptionalFragment(definition.LeftType, nameof(definition.LeftType));
        ValidateFragment(definition.RightType, nameof(definition.RightType));
        ValidateName(definition.Function);
        ValidateOptionalName(definition.Commutator);
        ValidateOptionalName(definition.Negator);
        ValidateOptionalName(definition.RestrictionFunction);
        ValidateOptionalName(definition.JoinFunction);
    }

    public static void Validate(BlueTuskOperatorFamilyDefinition definition)
    {
        BlueTuskGuard.NotNull(definition);
        ValidateNamedObject(definition.Name, definition.Schema);
        BlueTuskGuard.NotNullOrWhiteSpace(definition.IndexMethod);
        ValidateMembers(definition.Operators, definition.Functions);
    }

    public static void Validate(BlueTuskOperatorClassDefinition definition)
    {
        BlueTuskGuard.NotNull(definition);
        ValidateNamedObject(definition.Name, definition.Schema);
        BlueTuskGuard.NotNullOrWhiteSpace(definition.IndexMethod);
        ValidateFragment(definition.DataType, nameof(definition.DataType));
        ValidateOptionalName(definition.Family);
        ValidateMembers(definition.Operators, definition.Functions);
        ValidateOptionalFragment(definition.StorageType, nameof(definition.StorageType));
        if (definition.Operators.Count == 0 || definition.Functions.Count == 0)
        {
            throw new ArgumentException("An operator class requires operator and support-function members.",
                nameof(definition));
        }
    }

    public static void Validate(BlueTuskCastDefinition definition)
    {
        BlueTuskGuard.NotNull(definition);
        ValidateFragment(definition.SourceType, nameof(definition.SourceType));
        ValidateFragment(definition.TargetType, nameof(definition.TargetType));
        if (definition.Method == BlueTuskCastMethod.Function)
        {
            BlueTuskGuard.NotNull(definition.Function);
            ValidateName(definition.Function.Function);
            BlueTuskGuard.NotNull(definition.Function.ArgumentTypes);
            foreach (var argument in definition.Function.ArgumentTypes)
            {
                ValidateFragment(argument, nameof(definition.Function.ArgumentTypes));
            }
        }
        else if (definition.Function is not null)
        {
            throw new ArgumentException("Only a function-based cast can specify an implementation function.",
                nameof(definition));
        }
    }

    public static void Validate(BlueTuskAggregateDefinition definition)
    {
        BlueTuskGuard.NotNull(definition);
        ValidateNamedObject(definition.Name, definition.Schema);
        ValidateSignature(definition.IdentityArgumentsSql);
        ValidateName(definition.TransitionFunction);
        ValidateFragment(definition.StateType, nameof(definition.StateType));
        ValidateOptionalName(definition.FinalFunction);
        ValidateOptionalName(definition.CombineFunction);
        ValidateOptionalName(definition.SerialFunction);
        ValidateOptionalName(definition.DeserialFunction);
        ValidateOptionalName(definition.MovingTransitionFunction);
        ValidateOptionalName(definition.MovingInverseFunction);
        ValidateOptionalName(definition.MovingFinalFunction);
        ValidateOptionalName(definition.SortOperator);
        ValidateOptionalFragment(definition.MovingStateType, nameof(definition.MovingStateType));
        if ((definition.SerialFunction is null) != (definition.DeserialFunction is null))
        {
            throw new ArgumentException("Aggregate serialization and deserialization functions must be paired.",
                nameof(definition));
        }

        if (definition.StateSpace is <= 0 || definition.MovingStateSpace is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(definition),
                "Aggregate state-space estimates must be positive when specified.");
        }

        if (definition.FinalFunction is null &&
            (definition.FinalFunctionExtra ||
             definition.FinalFunctionModify != BlueTuskAggregateFinalFunctionModify.ReadOnly))
        {
            throw new ArgumentException(
                "Aggregate final-function options require a final function.",
                nameof(definition));
        }

        var movingCount = new object?[]
        {
            definition.MovingTransitionFunction,
            definition.MovingInverseFunction,
            definition.MovingStateType,
        }.Count(value => value is not null);
        if (movingCount is > 0 and < 3)
        {
            throw new ArgumentException(
                "Moving aggregate state requires transition, inverse-transition, and state-type values.",
                nameof(definition));
        }


        var hasMovingOptions = definition.MovingStateSpace is not null ||
            definition.MovingFinalFunction is not null ||
            definition.MovingFinalFunctionExtra ||
            definition.MovingFinalFunctionModify != BlueTuskAggregateFinalFunctionModify.ReadOnly ||
            definition.MovingInitialCondition is not null;
        if (hasMovingOptions && movingCount != 3)
        {
            throw new ArgumentException(
                "Moving aggregate options require a complete moving-state definition.",
                nameof(definition));
        }

        if (definition.MovingFinalFunction is null &&
            (definition.MovingFinalFunctionExtra ||
             definition.MovingFinalFunctionModify != BlueTuskAggregateFinalFunctionModify.ReadOnly))
        {
            throw new ArgumentException(
                "Moving final-function options require a moving final function.",
                nameof(definition));
        }

        if (definition.Kind == BlueTuskAggregateKind.Ordinary &&
            definition.IdentityArgumentsSql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An ordinary aggregate signature cannot contain ORDER BY.",
                nameof(definition));
        }

        if (definition.Kind != BlueTuskAggregateKind.Ordinary &&
            !definition.IdentityArgumentsSql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An ordered-set aggregate signature must contain ORDER BY.",
                nameof(definition));
        }
    }

    private static void ValidateDefinition<T>(T definition)
    {
        switch (definition)
        {
            case BlueTuskOperatorDefinition value: Validate(value); break;
            case BlueTuskOperatorFamilyDefinition value: Validate(value); break;
            case BlueTuskOperatorClassDefinition value: Validate(value); break;
            case BlueTuskCastDefinition value: Validate(value); break;
            case BlueTuskAggregateDefinition value: Validate(value); break;
            default: throw new ArgumentException("Unsupported schema-program definition type.", nameof(definition));
        }
    }

    private static T NormalizeDefinition<T>(T definition) => definition switch
    {
        BlueTuskOperatorDefinition value => (T)(object)Normalize(value),
        BlueTuskOperatorFamilyDefinition value => (T)(object)Normalize(value),
        BlueTuskOperatorClassDefinition value => (T)(object)Normalize(value),
        BlueTuskCastDefinition value => (T)(object)Normalize(value),
        BlueTuskAggregateDefinition value => (T)(object)Normalize(value),
        _ => throw new ArgumentException("Unsupported schema-program definition type.", nameof(definition)),
    };

    private static BlueTuskOperatorMemberDefinition[] NormalizeOperators(
        IReadOnlyList<BlueTuskOperatorMemberDefinition> operators) => operators
        .Select(item => item with
        {
            LeftType = NormalizeFragment(item.LeftType),
            RightType = NormalizeFragment(item.RightType),
        })
        .OrderBy(item => item.StrategyNumber)
        .ThenBy(item => item.LeftType, StringComparer.Ordinal)
        .ThenBy(item => item.RightType, StringComparer.Ordinal)
        .ToArray();

    private static BlueTuskOperatorFunctionDefinition[] NormalizeFunctions(
        IReadOnlyList<BlueTuskOperatorFunctionDefinition> functions) => functions
        .Select(item => item with
        {
            LeftType = NormalizeFragment(item.LeftType),
            RightType = NormalizeFragment(item.RightType),
            ArgumentTypes = item.ArgumentTypes.Select(NormalizeFragment).ToArray(),
        })
        .OrderBy(item => item.SupportNumber)
        .ThenBy(item => item.LeftType, StringComparer.Ordinal)
        .ThenBy(item => item.RightType, StringComparer.Ordinal)
        .ToArray();

    private static void ValidateMembers(
        IReadOnlyList<BlueTuskOperatorMemberDefinition> operators,
        IReadOnlyList<BlueTuskOperatorFunctionDefinition> functions)
    {
        BlueTuskGuard.NotNull(operators);
        BlueTuskGuard.NotNull(functions);
        ValidateUnique(operators, item => (item.StrategyNumber, item.LeftType, item.RightType), "operator member");
        ValidateUnique(functions, item => (item.SupportNumber, item.LeftType, item.RightType), "support function");
        foreach (var item in operators)
        {
            if (item.StrategyNumber <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(operators), "Strategy numbers must be positive.");
            }

            ValidateOperatorName(item.Operator);
            ValidateFragment(item.LeftType, nameof(item.LeftType));
            ValidateFragment(item.RightType, nameof(item.RightType));
            ValidateOptionalName(item.SortFamily);
            if ((item.Purpose == BlueTuskOperatorPurpose.OrderBy) != (item.SortFamily is not null))
            {
                throw new ArgumentException("ORDER BY operator members require exactly one sort family.",
                    nameof(operators));
            }
        }

        foreach (var item in functions)
        {
            if (item.SupportNumber <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(functions), "Support numbers must be positive.");
            }

            ValidateFragment(item.LeftType, nameof(item.LeftType));
            ValidateFragment(item.RightType, nameof(item.RightType));
            ValidateName(item.Function);
            BlueTuskGuard.NotNull(item.ArgumentTypes);
            foreach (var argument in item.ArgumentTypes)
            {
                ValidateFragment(argument, nameof(item.ArgumentTypes));
            }
        }
    }

    private static void ValidateName(BlueTuskSchemaProgramName value) => ValidateNamedObject(value.Name, value.Schema);
    private static void ValidateOptionalName(BlueTuskSchemaProgramName? value)
    {
        if (value is not null) ValidateName(value);
    }

    private static void ValidateOptionalName(BlueTuskOperatorName? value)
    {
        if (value is not null) ValidateOperatorName(value);
    }

    private static void ValidateOperatorName(BlueTuskOperatorName value)
    {
        BlueTuskGuard.NotNull(value);
        if (!OperatorRegex().IsMatch(value.Name) || value.Name is "=>" or "!=")
        {
            throw new ArgumentException($"'{value.Name}' is not a valid PostgreSQL operator symbol.");
        }

        var hasInvalidEnding = value.Name.Length > 1 &&
            (value.Name.EndsWith('+') || value.Name.EndsWith('-')) &&
            !value.Name.Any(character => "~!@#%^&|`?".Contains(character));
        if (value.Name.Contains("--", StringComparison.Ordinal) ||
            value.Name.Contains("/*", StringComparison.Ordinal) ||
            hasInvalidEnding)
        {
            throw new ArgumentException($"'{value.Name}' is not a valid PostgreSQL operator symbol.");
        }

        if (value.Schema is not null) BlueTuskGuard.NotNullOrWhiteSpace(value.Schema);
    }

    private static void ValidateNamedObject(string name, string? schema)
    {
        BlueTuskGuard.NotNullOrWhiteSpace(name);
        if (schema is not null) BlueTuskGuard.NotNullOrWhiteSpace(schema);
    }

    private static void ValidateSignature(string value)
    {
        BlueTuskGuard.NotNull(value);
        ValidateSafeFragment(value, nameof(value), allowEmpty: true);
    }

    private static void ValidateFragment(string value, string parameterName) =>
        ValidateSafeFragment(value, parameterName, allowEmpty: false);

    private static void ValidateOptionalFragment(string? value, string parameterName)
    {
        if (value is not null) ValidateFragment(value, parameterName);
    }

    private static void ValidateSafeFragment(string value, string parameterName, bool allowEmpty)
    {
        BlueTuskGuard.NotNull(value, parameterName);
        if (!allowEmpty) BlueTuskGuard.NotNullOrWhiteSpace(value, parameterName);
        if (value.Contains(';') || value.Contains("--", StringComparison.Ordinal) ||
            value.Contains("/*", StringComparison.Ordinal) || value.Contains("*/", StringComparison.Ordinal) ||
            value.Contains('\0'))
        {
            throw new ArgumentException("PostgreSQL type and signature fragments cannot contain statement delimiters or comments.",
                parameterName);
        }
    }

    private static string NormalizeFragment(string value) => value.Trim();
    private static string? NormalizeNullableFragment(string? value) => value?.Trim();

    private static void ValidateUnique<T, TKey>(IReadOnlyList<T> items, Func<T, TKey> key, string kind)
        where TKey : notnull
    {
        var keys = new HashSet<TKey>();
        foreach (var item in items)
        {
            if (!keys.Add(key(item))) throw new ArgumentException($"A {kind} is configured more than once.");
        }
    }

#if NET7_0_OR_GREATER
    [GeneratedRegex("^[+\\-*/<>=~!@#%^&|`?]+$", RegexOptions.CultureInvariant)]
    private static partial Regex OperatorRegex();
#else
    private static readonly Regex _operatorRegex = new("^[+\\-*/<>=~!@#%^&|`?]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static Regex OperatorRegex() => _operatorRegex;
#endif

    internal readonly record struct OperatorKey(string? Schema, string Name, string? LeftType, string RightType)
    {
        public static OperatorKey Create(BlueTuskOperatorDefinition value) =>
            new(value.Schema, value.Name, value.LeftType, value.RightType);
    }

    internal readonly record struct OperatorFamilyKey(string? Schema, string Name, string IndexMethod)
    {
        public static OperatorFamilyKey Create(BlueTuskOperatorFamilyDefinition value) =>
            new(value.Schema, value.Name, value.IndexMethod);
    }

    internal readonly record struct OperatorClassKey(string? Schema, string Name, string IndexMethod)
    {
        public static OperatorClassKey Create(BlueTuskOperatorClassDefinition value) =>
            new(value.Schema, value.Name, value.IndexMethod);
    }

    internal readonly record struct CastKey(string SourceType, string TargetType)
    {
        public static CastKey Create(BlueTuskCastDefinition value) => new(value.SourceType, value.TargetType);
    }

    internal readonly record struct AggregateKey(string? Schema, string Name, string IdentityArgumentsSql)
    {
        public static AggregateKey Create(BlueTuskAggregateDefinition value) =>
            new(value.Schema, value.Name, value.IdentityArgumentsSql);
    }
}
