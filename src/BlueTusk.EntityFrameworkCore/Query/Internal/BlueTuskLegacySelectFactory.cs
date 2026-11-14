#if !BLUETUSK_EF8_OR_GREATER
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

// Before EF8, scalar table-function select construction has no public provider
// seam. Use the frozen EF5-7 relational implementation's own table references
// and concrete columns so correlation, cloning, and alias remapping remain valid.
internal static class BlueTuskLegacySelectFactory
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo Identifiers = typeof(SelectExpression).GetField("_identifier", InstanceMembers)!;
#if BLUETUSK_EF6_OR_GREATER
    private static readonly MethodInfo AddTable = typeof(SelectExpression).GetMethod("AddTable", InstanceMembers)!;
    private static readonly FieldInfo References = typeof(SelectExpression).GetField("_tableReferences", InstanceMembers)!;
    private static readonly Type ReferenceType = typeof(SelectExpression).GetNestedType("TableReferenceExpression", BindingFlags.NonPublic)!;
    private static readonly ConstructorInfo ReferenceConstructor = ReferenceType.GetConstructors(InstanceMembers).Single();
    private static readonly PropertyInfo ReferenceAlias = ReferenceType.GetProperty("Alias", InstanceMembers)!;
    private static readonly ConstructorInfo ColumnConstructor = typeof(SelectExpression)
        .GetNestedType("ConcreteColumnExpression", BindingFlags.NonPublic)!.GetConstructors(InstanceMembers)
        .Single(constructor => constructor.GetParameters() is var parameters
            && parameters.Length == 5 && parameters[0].ParameterType == typeof(string));
#else
    private static readonly ConstructorInfo ColumnConstructor = typeof(ColumnExpression).GetConstructor(
        InstanceMembers, binder: null, [typeof(string), typeof(TableExpressionBase), typeof(Type),
            typeof(RelationalTypeMapping), typeof(bool)], modifiers: null)!;
#endif

    public static SelectExpression Create(ISqlExpressionFactory sqlExpressionFactory, TableExpressionBase table,
        string name, Type type, RelationalTypeMapping mapping, bool nullable, string identifierName,
        Type identifierType, RelationalTypeMapping identifierMapping)
    {
        var select = sqlExpressionFactory.Select(sqlExpressionFactory.Constant(1));
#pragma warning disable EF1001 // This is the legacy provider table-reference seam.
#if BLUETUSK_EF6_OR_GREATER
        AddTable.Invoke(select, [table, ReferenceConstructor.Invoke([select, table.Alias!])]);
#else
        ((List<TableExpressionBase>)select.Tables).Add(table);
#endif
#pragma warning restore EF1001
        var value = select.CreateColumnExpression(table, name, type, mapping, nullable);
        select.ReplaceBlueTuskProjection(new Dictionary<ProjectionMember, Expression> { [new ProjectionMember()] = value });
        var identifier = select.CreateColumnExpression(table, identifierName, identifierType, identifierMapping, false);
        ((IList)Identifiers.GetValue(select)!).Add((identifier, identifierMapping.Comparer));
        return select;
    }

    public static ColumnExpression CreateColumnExpression(this SelectExpression select, TableExpressionBase table,
        string name, Type type, RelationalTypeMapping mapping, bool nullable)
    {
#pragma warning disable EF1001 // The concrete column must share EF's table reference to preserve alias remapping.
#if BLUETUSK_EF6_OR_GREATER
        var reference = ((IEnumerable)References.GetValue(select)!).Cast<object>().Single(
            tableReference => (string?)ReferenceAlias.GetValue(tableReference) == table.Alias);
#pragma warning restore EF1001
        return (ColumnExpression)ColumnConstructor.Invoke([name, reference, type, mapping, nullable]);
#else
        return (ColumnExpression)ColumnConstructor.Invoke([name, table, type, mapping, nullable]);
#endif
    }

    public static void ReplaceBlueTuskProjection(this SelectExpression select, Dictionary<ProjectionMember, Expression> projection)
    {
#if BLUETUSK_EF6_OR_GREATER
        select.ReplaceProjection(projection);
#else
        select.ReplaceProjectionMapping(projection);
#endif
    }
}
#endif
