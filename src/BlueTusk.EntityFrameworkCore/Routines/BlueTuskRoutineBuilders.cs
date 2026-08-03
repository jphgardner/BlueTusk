using System.Globalization;
using System.Text;

namespace BlueTusk.EntityFrameworkCore.Routines;

/// <summary>Builds a model-authored PostgreSQL function.</summary>
public sealed class BlueTuskFunctionBuilder
{
    private readonly List<BlueTuskRoutineParameterDefinition> _parameters = [];
    private readonly List<BlueTuskRoutineConfigurationDefinition> _configuration = [];

    internal BlueTuskFunctionBuilder(
        string name,
        string? schema,
        string returnStoreType,
        string bodySql)
    {
        Name = name;
        Schema = schema;
        ReturnStoreType = returnStoreType;
        BodySql = bodySql;
    }

    private string Name { get; }

    private string? Schema { get; }

    private string ReturnStoreType { get; }

    private string BodySql { get; }

    private string Language { get; set; } = "sql";

    private bool ReturnsSet { get; set; }

    private BlueTuskFunctionVolatility Volatility { get; set; } = BlueTuskFunctionVolatility.Volatile;

    private BlueTuskFunctionParallelSafety ParallelSafety { get; set; } = BlueTuskFunctionParallelSafety.Unsafe;

    private bool IsStrictValue { get; set; }

    private bool IsSecurityDefinerValue { get; set; }

    private bool IsLeakproofValue { get; set; }

    private double? Cost { get; set; }

    private double? Rows { get; set; }

    /// <summary>Adds an ordered parameter. Store types and defaults are trusted model-time SQL.</summary>
    public BlueTuskFunctionBuilder HasParameter(
        string storeType,
        string? name = null,
        BlueTuskRoutineParameterMode mode = BlueTuskRoutineParameterMode.In,
        string? defaultSql = null)
    {
        _parameters.Add(new BlueTuskRoutineParameterDefinition(name, storeType, mode, defaultSql));
        return this;
    }

    /// <summary>Sets the trusted PostgreSQL implementation language name.</summary>
    public BlueTuskFunctionBuilder UseLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        Language = language;
        return this;
    }

    /// <summary>Marks the function as returning a set of its configured return store type.</summary>
    public BlueTuskFunctionBuilder ReturnsSetOf(bool returnsSet = true)
    {
        ReturnsSet = returnsSet;
        return this;
    }

    /// <summary>Sets the function's optimizer volatility promise.</summary>
    public BlueTuskFunctionBuilder HasVolatility(BlueTuskFunctionVolatility volatility)
    {
        Volatility = volatility;
        return this;
    }

    /// <summary>Marks whether PostgreSQL can skip execution when any input is null.</summary>
    public BlueTuskFunctionBuilder IsStrict(bool strict = true)
    {
        IsStrictValue = strict;
        return this;
    }

    /// <summary>Marks whether the function executes with its owner's privileges.</summary>
    public BlueTuskFunctionBuilder IsSecurityDefiner(bool securityDefiner = true)
    {
        IsSecurityDefinerValue = securityDefiner;
        return this;
    }

    /// <summary>Marks a superuser-reviewed function as leakproof.</summary>
    public BlueTuskFunctionBuilder IsLeakproof(bool leakproof = true)
    {
        IsLeakproofValue = leakproof;
        return this;
    }

    /// <summary>Sets the function's parallel-query safety classification.</summary>
    public BlueTuskFunctionBuilder HasParallelSafety(BlueTuskFunctionParallelSafety parallelSafety)
    {
        ParallelSafety = parallelSafety;
        return this;
    }

    /// <summary>Sets a positive planner execution cost.</summary>
    public BlueTuskFunctionBuilder HasCost(double cost)
    {
        ValidatePositiveFinite(cost, nameof(cost));
        Cost = cost;
        return this;
    }

    /// <summary>Sets a positive planner row estimate for a set-returning function.</summary>
    public BlueTuskFunctionBuilder HasRows(double rows)
    {
        ValidatePositiveFinite(rows, nameof(rows));
        Rows = rows;
        return this;
    }

    /// <summary>Adds a trusted routine-local <c>SET</c> assignment.</summary>
    public BlueTuskFunctionBuilder HasConfiguration(string name, string valueSql)
    {
        _configuration.Add(new BlueTuskRoutineConfigurationDefinition(name, valueSql));
        return this;
    }

    internal BlueTuskRoutineDefinition Build() => BlueTuskRoutineSqlComposer.ComposeFunction(
        Name,
        Schema,
        ReturnStoreType,
        BodySql,
        Language,
        ReturnsSet,
        Volatility,
        ParallelSafety,
        IsStrictValue,
        IsSecurityDefinerValue,
        IsLeakproofValue,
        Cost,
        Rows,
        _parameters,
        _configuration);

    private static void ValidatePositiveFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The value must be positive and finite.");
        }
    }
}

/// <summary>Builds a model-authored PostgreSQL procedure.</summary>
public sealed class BlueTuskProcedureBuilder
{
    private readonly List<BlueTuskRoutineParameterDefinition> _parameters = [];
    private readonly List<BlueTuskRoutineConfigurationDefinition> _configuration = [];

    internal BlueTuskProcedureBuilder(string name, string? schema, string bodySql)
    {
        Name = name;
        Schema = schema;
        BodySql = bodySql;
    }

    private string Name { get; }

    private string? Schema { get; }

    private string BodySql { get; }

    private string Language { get; set; } = "sql";

    private bool IsSecurityDefinerValue { get; set; }

    /// <summary>Adds an ordered parameter. Store types and defaults are trusted model-time SQL.</summary>
    public BlueTuskProcedureBuilder HasParameter(
        string storeType,
        string? name = null,
        BlueTuskRoutineParameterMode mode = BlueTuskRoutineParameterMode.In,
        string? defaultSql = null)
    {
        _parameters.Add(new BlueTuskRoutineParameterDefinition(name, storeType, mode, defaultSql));
        return this;
    }

    /// <summary>Sets the trusted PostgreSQL implementation language name.</summary>
    public BlueTuskProcedureBuilder UseLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        Language = language;
        return this;
    }

    /// <summary>Marks whether the procedure executes with its owner's privileges.</summary>
    public BlueTuskProcedureBuilder IsSecurityDefiner(bool securityDefiner = true)
    {
        IsSecurityDefinerValue = securityDefiner;
        return this;
    }

    /// <summary>Adds a trusted routine-local <c>SET</c> assignment.</summary>
    public BlueTuskProcedureBuilder HasConfiguration(string name, string valueSql)
    {
        _configuration.Add(new BlueTuskRoutineConfigurationDefinition(name, valueSql));
        return this;
    }

    internal BlueTuskRoutineDefinition Build() => BlueTuskRoutineSqlComposer.ComposeProcedure(
        Name,
        Schema,
        BodySql,
        Language,
        IsSecurityDefinerValue,
        _parameters,
        _configuration);
}

internal static class BlueTuskRoutineSqlComposer
{
    public static BlueTuskRoutineDefinition ComposeFunction(
        string name,
        string? schema,
        string returnStoreType,
        string bodySql,
        string language,
        bool returnsSet,
        BlueTuskFunctionVolatility volatility,
        BlueTuskFunctionParallelSafety parallelSafety,
        bool isStrict,
        bool isSecurityDefiner,
        bool isLeakproof,
        double? cost,
        double? rows,
        IReadOnlyList<BlueTuskRoutineParameterDefinition> parameters,
        IReadOnlyList<BlueTuskRoutineConfigurationDefinition> configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(returnStoreType);
        ValidateCommon(name, schema, bodySql, language, parameters, configuration);
        if (rows is not null && !returnsSet)
        {
            throw new ArgumentException("A ROWS estimate is valid only for a set-returning function.", nameof(rows));
        }

        var arguments = ComposeArguments(parameters, includeDefaults: true);
        var identity = ComposeArguments(parameters, includeDefaults: false);
        var inputTypes = ComposeInputTypes(parameters);
        var result = returnsSet ? $"SETOF {returnStoreType}" : returnStoreType;
        var sql = new StringBuilder()
            .Append("CREATE OR REPLACE FUNCTION ")
            .Append(QualifiedName(name, schema))
            .Append('(').Append(arguments).AppendLine(")")
            .Append("RETURNS ").AppendLine(result)
            .Append("LANGUAGE ").AppendLine(QuoteIdentifier(language))
            .AppendLine(volatility.ToString().ToUpperInvariant())
            .AppendLine(isStrict ? "RETURNS NULL ON NULL INPUT" : "CALLED ON NULL INPUT")
            .AppendLine(isSecurityDefiner ? "SECURITY DEFINER" : "SECURITY INVOKER")
            .Append("PARALLEL ").AppendLine(parallelSafety.ToString().ToUpperInvariant());
        if (isLeakproof)
        {
            sql.AppendLine("LEAKPROOF");
        }

        if (cost is not null)
        {
            sql.Append("COST ").AppendLine(cost.Value.ToString("R", CultureInfo.InvariantCulture));
        }

        if (rows is not null)
        {
            sql.Append("ROWS ").AppendLine(rows.Value.ToString("R", CultureInfo.InvariantCulture));
        }

        AppendConfiguration(sql, configuration);
        AppendBody(sql, bodySql);
        return new BlueTuskRoutineDefinition(
            BlueTuskRoutineKind.Function,
            name,
            schema,
            inputTypes,
            identity,
            arguments,
            result,
            sql.ToString().Trim(),
            HasTrackedBodyDependencies: false);
    }

    public static BlueTuskRoutineDefinition ComposeProcedure(
        string name,
        string? schema,
        string bodySql,
        string language,
        bool isSecurityDefiner,
        IReadOnlyList<BlueTuskRoutineParameterDefinition> parameters,
        IReadOnlyList<BlueTuskRoutineConfigurationDefinition> configuration)
    {
        ValidateCommon(name, schema, bodySql, language, parameters, configuration);
        var arguments = ComposeArguments(parameters, includeDefaults: true);
        var sql = new StringBuilder()
            .Append("CREATE OR REPLACE PROCEDURE ")
            .Append(QualifiedName(name, schema))
            .Append('(').Append(arguments).AppendLine(")")
            .Append("LANGUAGE ").AppendLine(QuoteIdentifier(language))
            .AppendLine(isSecurityDefiner ? "SECURITY DEFINER" : "SECURITY INVOKER");
        AppendConfiguration(sql, configuration);
        AppendBody(sql, bodySql);
        return new BlueTuskRoutineDefinition(
            BlueTuskRoutineKind.Procedure,
            name,
            schema,
            ComposeInputTypes(parameters),
            ComposeArguments(parameters, includeDefaults: false),
            arguments,
            ResultSql: null,
            sql.ToString().Trim(),
            HasTrackedBodyDependencies: false);
    }

    private static void ValidateCommon(
        string name,
        string? schema,
        string bodySql,
        string language,
        IReadOnlyList<BlueTuskRoutineParameterDefinition> parameters,
        IReadOnlyList<BlueTuskRoutineConfigurationDefinition> configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (schema is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(bodySql);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(configuration);
        var inputDefaultSeen = false;
        var variadicSeen = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            ArgumentException.ThrowIfNullOrWhiteSpace(parameter.StoreType);
            if (parameter.Name is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(parameter.Name);
                if (!names.Add(parameter.Name))
                {
                    throw new ArgumentException($"Routine parameter name '{parameter.Name}' is duplicated.", nameof(parameters));
                }
            }

            if (variadicSeen && parameter.Mode != BlueTuskRoutineParameterMode.Out)
            {
                throw new ArgumentException("Only OUT parameters may follow a VARIADIC parameter.", nameof(parameters));
            }

            if (parameter.Mode == BlueTuskRoutineParameterMode.Variadic)
            {
                variadicSeen = true;
            }

            var acceptsDefault = parameter.Mode is BlueTuskRoutineParameterMode.In or BlueTuskRoutineParameterMode.InOut;
            if (parameter.DefaultSql is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(parameter.DefaultSql);
                if (!acceptsDefault)
                {
                    throw new ArgumentException("Only IN and INOUT parameters may have defaults.", nameof(parameters));
                }

                inputDefaultSeen = true;
            }
            else if (acceptsDefault && inputDefaultSeen)
            {
                throw new ArgumentException(
                    "Every input parameter following one with a default must also have a default.",
                    nameof(parameters));
            }
        }

        var configurationNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in configuration)
        {
            ArgumentNullException.ThrowIfNull(setting);
            ValidateConfigurationName(setting.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(setting.ValueSql);
            if (!configurationNames.Add(setting.Name))
            {
                throw new ArgumentException($"Routine configuration '{setting.Name}' is duplicated.", nameof(configuration));
            }
        }
    }

    private static string ComposeArguments(
        IReadOnlyList<BlueTuskRoutineParameterDefinition> parameters,
        bool includeDefaults) =>
        string.Join(", ", parameters.Select(parameter =>
        {
            var builder = new StringBuilder().Append(parameter.Mode switch
            {
                BlueTuskRoutineParameterMode.In => "IN ",
                BlueTuskRoutineParameterMode.Out => "OUT ",
                BlueTuskRoutineParameterMode.InOut => "INOUT ",
                BlueTuskRoutineParameterMode.Variadic => "VARIADIC ",
                _ => throw new InvalidOperationException($"Unknown routine parameter mode '{parameter.Mode}'."),
            });
            if (parameter.Name is not null)
            {
                builder.Append(QuoteIdentifier(parameter.Name)).Append(' ');
            }

            builder.Append(parameter.StoreType);
            if (includeDefaults && parameter.DefaultSql is not null)
            {
                builder.Append(" DEFAULT ").Append(parameter.DefaultSql);
            }

            return builder.ToString();
        }));

    private static string ComposeInputTypes(IReadOnlyList<BlueTuskRoutineParameterDefinition> parameters) =>
        string.Join(", ", parameters
            .Where(parameter => parameter.Mode != BlueTuskRoutineParameterMode.Out)
            .Select(parameter => parameter.StoreType));

    private static void AppendConfiguration(
        StringBuilder builder,
        IReadOnlyList<BlueTuskRoutineConfigurationDefinition> configuration)
    {
        foreach (var setting in configuration)
        {
            builder.Append("SET ").Append(setting.Name).Append(" TO ").AppendLine(setting.ValueSql);
        }
    }

    private static void AppendBody(StringBuilder builder, string bodySql)
    {
        var tagIndex = 0;
        string tag;
        do
        {
            tag = tagIndex == 0 ? "$bluetusk$" : $"$bluetusk_{tagIndex}$";
            tagIndex++;
        }
        while (bodySql.Contains(tag, StringComparison.Ordinal));

        builder.Append("AS ").AppendLine(tag)
            .AppendLine(bodySql)
            .Append(tag);
    }

    private static string QualifiedName(string name, string? schema) =>
        schema is null
            ? QuoteIdentifier(name)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}";

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static void ValidateConfigurationName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Split('.').Any(segment =>
                segment.Length == 0 ||
                !char.IsAsciiLetter(segment[0]) && segment[0] != '_' ||
                segment.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')))
        {
            throw new ArgumentException(
                $"Routine configuration name '{name}' contains an unsupported character.",
                nameof(name));
        }
    }
}
