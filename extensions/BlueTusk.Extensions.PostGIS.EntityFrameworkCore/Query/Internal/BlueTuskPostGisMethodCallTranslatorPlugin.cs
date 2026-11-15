using System.Reflection;
using BlueTusk.EntityFrameworkCore.Query;
using BlueTusk.EntityFrameworkCore.Storage;
using BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Storage.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using NetTopologySuite.Geometries;

namespace BlueTusk.Extensions.PostGIS.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskPostGisMethodCallTranslatorPlugin : IMethodCallTranslatorPlugin
{
    public BlueTuskPostGisMethodCallTranslatorPlugin(
        ISqlExpressionFactory sqlExpressionFactory,
        IRelationalTypeMappingSource typeMappingSource,
        BlueTuskPostGisTypeMappingOptions options)
    {
        Translators =
        [
            new BlueTuskPostGisMethodCallTranslator(
                sqlExpressionFactory,
                typeMappingSource,
                options.Schema),
        ];
    }

    public IEnumerable<IMethodCallTranslator> Translators { get; }
}

internal sealed class BlueTuskPostGisMethodCallTranslator(
    ISqlExpressionFactory sqlExpressionFactory,
    IRelationalTypeMappingSource typeMappingSource,
    string schema)
    : IMethodCallTranslator
{
    private static readonly Dictionary<string, string> GeometryMethods =
        new(StringComparer.Ordinal)
        {
            [nameof(Geometry.Contains)] = "st_contains",
            [nameof(Geometry.CoveredBy)] = "st_coveredby",
            [nameof(Geometry.Covers)] = "st_covers",
            [nameof(Geometry.Crosses)] = "st_crosses",
            [nameof(Geometry.Disjoint)] = "st_disjoint",
            [nameof(Geometry.EqualsTopologically)] = "st_equals",
            [nameof(Geometry.Intersects)] = "st_intersects",
            [nameof(Geometry.Overlaps)] = "st_overlaps",
            [nameof(Geometry.Touches)] = "st_touches",
            [nameof(Geometry.Within)] = "st_within",
            [nameof(Geometry.Distance)] = "st_distance",
            [nameof(Geometry.IsWithinDistance)] = "st_dwithin",
            [nameof(Geometry.Buffer)] = "st_buffer",
            [nameof(Geometry.ConvexHull)] = "st_convexhull",
            [nameof(Geometry.Difference)] = "st_difference",
            [nameof(Geometry.Intersection)] = "st_intersection",
            [nameof(Geometry.SymmetricDifference)] = "st_symdifference",
        };

    private static readonly HashSet<string> GeographyMethods =
        new(StringComparer.Ordinal)
        {
            nameof(Geometry.CoveredBy),
            nameof(Geometry.Covers),
            nameof(Geometry.Intersects),
            nameof(Geometry.Distance),
            nameof(Geometry.IsWithinDistance),
        };

    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        if (method.DeclaringType == typeof(BlueTuskPostGisDbFunctionsExtensions))
        {
            return TranslateDbFunction(method, arguments);
        }

        if (instance is null ||
            method.DeclaringType is null ||
            !typeof(Geometry).IsAssignableFrom(method.DeclaringType))
        {
            return null;
        }

        if (method.Name == nameof(Geometry.Equals) &&
            method.GetParameters() is [{ ParameterType: var parameterType }] &&
            parameterType == typeof(Geometry))
        {
            return TranslateSpatialFunction("st_equals", instance, arguments, method.ReturnType);
        }

        if (method.Name == nameof(Geometry.Union))
        {
            return TranslateSpatialFunction(
                arguments.Count == 0 ? "st_unaryunion" : "st_union",
                instance,
                arguments,
                method.ReturnType,
                geometryOnly: true);
        }

        if (!GeometryMethods.TryGetValue(method.Name, out var functionName) ||
            method.Name == nameof(Geometry.Buffer) &&
            (arguments.Count != 1 || arguments[0].Type != typeof(double)))
        {
            return null;
        }

        var geographyAllowed = GeographyMethods.Contains(method.Name);
        return TranslateSpatialFunction(
            functionName,
            instance,
            arguments,
            method.ReturnType,
            geometryOnly: !geographyAllowed);
    }

    private SqlExpression TranslateDbFunction(
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments)
    {
        var functionArguments = arguments.Skip(1).ToArray();
        return method.Name switch
        {
            nameof(BlueTuskPostGisDbFunctionsExtensions.BoundingBoxIntersects) =>
                TranslateBoundingBox(functionArguments),
            nameof(BlueTuskPostGisDbFunctionsExtensions.IsWithinDistance) =>
                TranslateStandaloneFunction("st_dwithin", functionArguments, method.ReturnType),
            nameof(BlueTuskPostGisDbFunctionsExtensions.Transform) =>
                TranslateStandaloneFunction(
                    "st_transform",
                    functionArguments,
                    method.ReturnType,
                    geometryOnly: true),
            nameof(BlueTuskPostGisDbFunctionsExtensions.MakeValid) =>
                TranslateStandaloneFunction(
                    "st_makevalid",
                    functionArguments,
                    method.ReturnType,
                    geometryOnly: true),
            nameof(BlueTuskPostGisDbFunctionsExtensions.Force2D) =>
                TranslateStandaloneFunction(
                    "st_force2d",
                    functionArguments,
                    method.ReturnType,
                    geometryOnly: true),
            nameof(BlueTuskPostGisDbFunctionsExtensions.AsGeoJson) =>
                TranslateStandaloneFunction(
                    "st_asgeojson",
                    functionArguments,
                    method.ReturnType,
                    geometryOnly: true),
            _ => throw new InvalidOperationException($"Unknown PostGIS function '{method.Name}'."),
        };
    }

    private SqlExpression TranslateSpatialFunction(
        string functionName,
        SqlExpression instance,
        IReadOnlyList<SqlExpression> arguments,
        Type resultType,
        bool geometryOnly = false) =>
        TranslateStandaloneFunction(
            functionName,
            [instance, .. arguments],
            resultType,
            geometryOnly);

    private SqlExpression TranslateStandaloneFunction(
        string functionName,
        IReadOnlyList<SqlExpression> arguments,
        Type resultType,
        bool geometryOnly = false)
    {
        var spatialMapping = FindSpatialMapping(arguments)
            ?? throw new InvalidOperationException(
                $"PostGIS function '{functionName}' requires a mapped geometry or geography operand.");
        ValidateSpatialMappings(arguments, functionName);
        if (geometryOnly && IsGeography(spatialMapping))
        {
            throw new InvalidOperationException(
                $"PostGIS function '{functionName}' is only supported for geometry operands; " +
                "the query operand is mapped as geography.");
        }

        var mappedArguments = arguments
            .Select(argument => typeof(Geometry).IsAssignableFrom(argument.Type)
                ? sqlExpressionFactory.ApplyTypeMapping(argument, spatialMapping)
                : sqlExpressionFactory.ApplyDefaultTypeMapping(argument)!)
            .ToArray();
        var resultMapping = FindResultMapping(resultType, spatialMapping);
        return sqlExpressionFactory.Function(
            schema,
            functionName,
            mappedArguments,
            nullable: true,
            argumentsPropagateNullability: Enumerable.Repeat(true, mappedArguments.Length),
            resultType,
            resultMapping);
    }

    private SqlExpression TranslateBoundingBox(SqlExpression[] arguments)
    {
        var spatialMapping = FindSpatialMapping(arguments)
            ?? throw new InvalidOperationException(
                "PostGIS bounding-box intersection requires a mapped geometry or geography operand.");
        var left = sqlExpressionFactory.ApplyTypeMapping(arguments[0], spatialMapping);
        var right = sqlExpressionFactory.ApplyTypeMapping(arguments[1], spatialMapping);
        var boolMapping = typeMappingSource.FindMapping(typeof(bool))
            ?? throw new InvalidOperationException("BlueTusk requires a Boolean mapping.");
        return BlueTuskSqlExpressionFactory.BinaryOperator(
            left,
            right,
            "&&",
            typeof(bool),
            boolMapping);
    }

    private static RelationalTypeMapping? FindSpatialMapping(IEnumerable<SqlExpression> arguments) =>
        arguments.FirstOrDefault(argument =>
            typeof(Geometry).IsAssignableFrom(argument.Type) && argument.TypeMapping is not null)
            ?.TypeMapping;

    private static void ValidateSpatialMappings(
        IEnumerable<SqlExpression> arguments,
        string functionName)
    {
        var categories = arguments
            .Where(argument => typeof(Geometry).IsAssignableFrom(argument.Type))
            .Select(argument => argument.TypeMapping)
            .OfType<RelationalTypeMapping>()
            .Select(IsGeography)
            .Distinct()
            .Take(2)
            .Count();
        if (categories > 1)
        {
            throw new InvalidOperationException(
                $"PostGIS function '{functionName}' cannot mix geometry and geography operands. " +
                "Map both operands to the same spatial store type or cast explicitly in SQL.");
        }
    }

    private RelationalTypeMapping? FindResultMapping(
        Type resultType,
        RelationalTypeMapping spatialMapping)
    {
        if (!typeof(Geometry).IsAssignableFrom(resultType))
        {
            return typeMappingSource.FindMapping(resultType);
        }

        var typeName = IsGeography(spatialMapping) ? "geography" : "geometry";
        return typeMappingSource.FindMapping(
            resultType,
            BlueTuskSqlIdentifier.Delimit(typeName, schema),
            keyOrIndex: false);
    }

    private static bool IsGeography(RelationalTypeMapping mapping) =>
        mapping.StoreType.Contains("geography", StringComparison.OrdinalIgnoreCase);
}
