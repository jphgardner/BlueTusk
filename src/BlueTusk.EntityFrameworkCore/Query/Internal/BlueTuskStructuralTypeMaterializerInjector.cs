// Derived from Entity Framework Core 10.0.11 (src/EFCore/Query/ShapedQueryCompilingExpressionVisitor.cs),
// Copyright (c) .NET Foundation and Contributors, licensed under the MIT license.
// BlueTusk change: materialize shadow properties of non-collection complex types into the entity shadow snapshot.

#pragma warning disable EF1001 // Internal EF Core API usage.
#pragma warning disable EF9100 // Precompiled-query support APIs, used exactly as EF Core uses them.

using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using static System.Linq.Expressions.Expression;

namespace BlueTusk.EntityFrameworkCore.Query.Internal;

internal sealed class BlueTuskStructuralTypeMaterializerInjector(
    ShapedQueryCompilingExpressionVisitor shapedQueryCompiler,
    IStructuralTypeMaterializerSource materializerSource,
    ILiftableConstantFactory liftableConstantFactory,
    QueryTrackingBehavior queryTrackingBehavior,
    bool supportsPrecompiledQuery)
    : ExpressionVisitor
{
    private static readonly ConstructorInfo MaterializationContextConstructor
        = typeof(MaterializationContext).GetConstructors().Single(ci => ci.GetParameters().Length == 2);

    private static readonly PropertyInfo DbContextMemberInfo
        = typeof(QueryContext).GetProperty(nameof(QueryContext.Context))!;

    private static readonly PropertyInfo EntityMemberInfo
        = typeof(InternalEntityEntry).GetProperty(nameof(InternalEntityEntry.Entity))!;

    private static readonly PropertyInfo EntityTypeMemberInfo
        = typeof(InternalEntityEntry).GetProperty(nameof(InternalEntityEntry.EntityType))!;

    private static readonly MethodInfo TryGetEntryMethodInfo
        = typeof(QueryContext).GetMethods()
            .Single(mi => mi.GetParameters().Length == 4 && mi.Name == nameof(QueryContext.TryGetEntry));

    private static readonly MethodInfo StartTrackingMethodInfo
        = typeof(QueryContext).GetMethod(
            nameof(QueryContext.StartTracking), [typeof(IEntityType), typeof(object), typeof(ISnapshot).MakeByRefType()])!;

    private static readonly MethodInfo CreateNullKeyValueInNoTrackingQueryMethod
        = typeof(ShapedQueryCompilingExpressionVisitor)
            .GetMethod(nameof(ShapedQueryCompilingExpressionVisitor.CreateNullKeyValueInNoTrackingQuery))!;

    private static readonly MethodInfo EntityTypeFindPrimaryKeyMethod =
        typeof(IEntityType).GetMethod(nameof(IEntityType.FindPrimaryKey), [])!;

    private readonly bool _queryStateManager =
        queryTrackingBehavior is QueryTrackingBehavior.TrackAll or QueryTrackingBehavior.NoTrackingWithIdentityResolution;

    private readonly HashSet<IEntityType> _visitedEntityTypes = [];
    private readonly BlueTuskMaterializationConditionConstantLifter _materializationConditionConstantLifter = new(liftableConstantFactory);
    private int _currentEntityIndex;

    public Expression Inject(Expression expression)
    {
        var result = Visit(expression);

        if (queryTrackingBehavior == QueryTrackingBehavior.TrackAll)
        {
            foreach (var entityType in _visitedEntityTypes)
            {
                if (entityType.FindOwnership() is { } ownership
                    && !ContainsOwner(ownership.PrincipalEntityType))
                {
                    throw new InvalidOperationException(CoreStrings.OwnedEntitiesCannotBeTrackedWithoutTheirOwner);
                }
            }

            bool ContainsOwner(IEntityType? owner)
                => owner != null && (_visitedEntityTypes.Contains(owner) || ContainsOwner(owner.BaseType));
        }

        return result;
    }

    protected override Expression VisitExtension(Expression extensionExpression)
        => extensionExpression is StructuralTypeShaperExpression shaper
            ? ProcessStructuralTypeShaper(shaper)
            : base.VisitExtension(extensionExpression);

    private BlockExpression ProcessStructuralTypeShaper(StructuralTypeShaperExpression shaper)
    {
        _currentEntityIndex++;

        var expressions = new List<Expression>();
        var variables = new List<ParameterExpression>();

        var typeBase = shaper.StructuralType;
        var clrType = shaper.Type;

        var materializationContextVariable = Variable(
            typeof(MaterializationContext),
            "materializationContext" + _currentEntityIndex);
        variables.Add(materializationContextVariable);
        expressions.Add(
            Assign(
                materializationContextVariable,
                New(
                    MaterializationContextConstructor,
                    shaper.ValueBufferExpression,
                    MakeMemberAccess(QueryCompilationContext.QueryContextParameter, DbContextMemberInfo))));

        var valueBufferExpression = Call(materializationContextVariable, MaterializationContext.GetValueBufferMethod);

        var primaryKey = typeBase is IEntityType entityType ? entityType.FindPrimaryKey() : null;

        var concreteEntityTypeVariable = Variable(
            typeBase is IEntityType ? typeof(IEntityType) : typeof(IComplexType),
            "entityType" + _currentEntityIndex);
        variables.Add(concreteEntityTypeVariable);

        var instanceVariable = Variable(clrType, "instance" + _currentEntityIndex);
        variables.Add(instanceVariable);
        expressions.Add(Assign(instanceVariable, Default(clrType)));

        if (_queryStateManager
            && primaryKey != null)
        {
            var entryVariable = Variable(typeof(InternalEntityEntry), "entry" + _currentEntityIndex);
            var hasNullKeyVariable = Variable(typeof(bool), "hasNullKey" + _currentEntityIndex);
            variables.Add(entryVariable);
            variables.Add(hasNullKeyVariable);

            var resolverPrm = Parameter(typeof(MaterializerLiftableConstantContext), "c");
            expressions.Add(
                Assign(
                    entryVariable,
                    Call(
                        QueryCompilationContext.QueryContextParameter,
                        TryGetEntryMethodInfo,
                        supportsPrecompiledQuery
                            ? liftableConstantFactory.CreateLiftableConstant(
                                primaryKey,
                                Lambda<Func<MaterializerLiftableConstantContext, object>>(
                                    Call(
                                        LiftableConstantExpressionHelpers.BuildMemberAccessForEntityOrComplexType(
                                            typeBase, resolverPrm),
                                        EntityTypeFindPrimaryKeyMethod),
                                    resolverPrm),
                                /*typeBase.Name +*/ "key",
                                typeof(IKey))
                            : Constant(primaryKey),
                        NewArrayInit(
                            typeof(object),
                            primaryKey.Properties
                                .Select(p => valueBufferExpression.CreateValueBufferReadValueExpression(
                                    typeof(object),
                                    p.GetIndex(),
                                    p))),
                        Constant(!shaper.IsNullable),
                        hasNullKeyVariable)));

            expressions.Add(
                IfThen(
                    Not(hasNullKeyVariable),
                    IfThenElse(
                        NotEqual(entryVariable, Default(typeof(InternalEntityEntry))),
                        Block(
                            Assign(concreteEntityTypeVariable, MakeMemberAccess(entryVariable, EntityTypeMemberInfo)),
                            Assign(
                                instanceVariable, Convert(
                                    MakeMemberAccess(entryVariable, EntityMemberInfo),
                                    clrType))),
                        MaterializeEntity(
                            shaper, materializationContextVariable, concreteEntityTypeVariable, instanceVariable,
                            entryVariable))));
        }
        else
        {
            if (primaryKey != null)
            {
                if (shaper.IsNullable)
                {
                    expressions.Add(
                        IfThen(
                            primaryKey.Properties.Select(p => NotEqual(
                                    valueBufferExpression.CreateValueBufferReadValueExpression(typeof(object), p.GetIndex(), p),
                                    Constant(null)))
                                .Aggregate(AndAlso),
                            MaterializeEntity(
                                shaper, materializationContextVariable, concreteEntityTypeVariable,
                                instanceVariable,
                                null)));
                }
                else
                {
                    var keyValuesVariable = Variable(typeof(object[]), "keyValues" + _currentEntityIndex);
                    var resolverPrm = Parameter(typeof(MaterializerLiftableConstantContext), "c");

                    expressions.Add(
                        IfThenElse(
                            primaryKey.Properties.Select(p => NotEqual(
                                    valueBufferExpression.CreateValueBufferReadValueExpression(typeof(object), p.GetIndex(), p),
                                    Constant(null)))
                                .Aggregate(AndAlso),
                            MaterializeEntity(
                                shaper, materializationContextVariable, concreteEntityTypeVariable,
                                instanceVariable,
                                null),
                            Block(
                                [keyValuesVariable],
                                Assign(
                                    keyValuesVariable,
                                    NewArrayInit(
                                        typeof(object),
                                        primaryKey.Properties.Select(p => valueBufferExpression.CreateValueBufferReadValueExpression(
                                            typeof(object), p.GetIndex(), p)))),
                                Call(
                                    CreateNullKeyValueInNoTrackingQueryMethod,
                                    supportsPrecompiledQuery
                                        ? liftableConstantFactory.CreateLiftableConstant(
                                            typeBase,
                                            LiftableConstantExpressionHelpers.BuildMemberAccessLambdaForStructuralType(typeBase),
                                            typeBase.Name + "EntityType",
                                            typeof(IEntityType))
                                        : Constant(typeBase),
                                    supportsPrecompiledQuery
                                        ? liftableConstantFactory.CreateLiftableConstant(
                                            primaryKey.Properties,
                                            Lambda<Func<MaterializerLiftableConstantContext, object>>(
                                                Property(
                                                    Call(
                                                        LiftableConstantExpressionHelpers.BuildMemberAccessForEntityOrComplexType(
                                                            typeBase, resolverPrm),
                                                        EntityTypeFindPrimaryKeyMethod),
                                                    nameof(IKey.Properties)),
                                                resolverPrm),
                                            typeBase.Name + "PrimaryKeyProperties",
                                            typeof(IReadOnlyList<IProperty>))
                                        : Constant(primaryKey.Properties),
                                    keyValuesVariable))));
                }
            }
            else
            {
                expressions.Add(
                    MaterializeEntity(
                        shaper, materializationContextVariable, concreteEntityTypeVariable, instanceVariable,
                        null));
            }
        }

        expressions.Add(instanceVariable);
        return Block(variables, expressions);
    }

    private BlockExpression MaterializeEntity(
        StructuralTypeShaperExpression shaper,
        ParameterExpression materializationContextVariable,
        ParameterExpression concreteEntityTypeVariable,
        ParameterExpression instanceVariable,
        ParameterExpression? entryVariable)
    {
        var structuralType = shaper.StructuralType;

        var expressions = new List<Expression>();
        var variables = new List<ParameterExpression>();

        var shadowValuesVariable = Variable(
            typeof(ISnapshot),
            "shadowSnapshot" + _currentEntityIndex);
        variables.Add(shadowValuesVariable);
        expressions.Add(
            Assign(
                shadowValuesVariable,
                supportsPrecompiledQuery
                    ? liftableConstantFactory.CreateLiftableConstant(
                        Snapshot.Empty,
                        static _ => Snapshot.Empty,
                        "emptySnapshot",
                        typeof(ISnapshot))
                    : Constant(Snapshot.Empty, typeof(ISnapshot))));

        var returnType = shaper.Type;
        var valueBufferExpression = Call(materializationContextVariable, MaterializationContext.GetValueBufferMethod);

        var materializationConditionBody = ReplacingExpressionVisitor.Replace(
            shaper.MaterializationCondition.Parameters[0],
            valueBufferExpression,
            shaper.MaterializationCondition.Body);

        var expressionContext = (returnType, shaper.IsNullable, materializationContextVariable, concreteEntityTypeVariable, shadowValuesVariable);
        expressions.Add(Assign(concreteEntityTypeVariable, materializationConditionBody));

        var (primaryKey, concreteStructuralTypes) = structuralType is IEntityType entityType
            ? (entityType.FindPrimaryKey(), entityType.GetConcreteDerivedTypesInclusive().Cast<ITypeBase>().ToArray())
            : (null, [structuralType]);

        var switchCases = new SwitchCase[concreteStructuralTypes.Length];
        for (var i = 0; i < concreteStructuralTypes.Length; i++)
        {
            var concreteStructuralType = concreteStructuralTypes[i];
            switchCases[i] = SwitchCase(
                CreateFullMaterializeExpression(concreteStructuralTypes[i], expressionContext),
                supportsPrecompiledQuery
                    ? liftableConstantFactory.CreateLiftableConstant(
                        concreteStructuralTypes[i],
                        LiftableConstantExpressionHelpers.BuildMemberAccessLambdaForStructuralType(concreteStructuralType),
                        concreteStructuralType.ShortName() + (structuralType is IEntityType ? "EntityType" : "ComplexType"),
                        structuralType is IEntityType ? typeof(IEntityType) : typeof(IComplexType))
                    : Constant(concreteStructuralTypes[i], structuralType is IEntityType ? typeof(IEntityType) : typeof(IComplexType)));
        }

        var materializationExpression = Switch(
            concreteEntityTypeVariable,
            Default(returnType),
            switchCases);

        expressions.Add(Assign(instanceVariable, materializationExpression));

        shapedQueryCompiler.AddStructuralTypeInitialization(shaper, instanceVariable, variables, expressions);

        if (_queryStateManager && primaryKey is not null)
        {
            if (structuralType is IEntityType entityType2)
            {
                foreach (var et in entityType2.GetAllBaseTypes().Concat(entityType2.GetDerivedTypesInclusive()))
                {
                    _visitedEntityTypes.Add(et);
                }
            }

            var recordsComplexShadowValues = structuralType is IEntityType recordingEntityType
                && BlueTuskComplexShadowValues.HasRecordedProperties(recordingEntityType);
            if (recordsComplexShadowValues
                && BlueTuskComplexShadowValues.HasRecordedEntityShadowValues((IEntityType)structuralType))
            {
                expressions.Add(
                    Assign(
                        shadowValuesVariable,
                        Call(
                            BlueTuskComplexShadowValues.MergeEntityShadowValuesMethod,
                            concreteEntityTypeVariable,
                            Convert(instanceVariable, typeof(object)),
                            shadowValuesVariable)));
            }

            expressions.Add(
                Assign(
                    entryVariable!,
                    Condition(
                        Equal(concreteEntityTypeVariable, Default(typeof(IEntityType))),
                        Default(typeof(InternalEntityEntry)),
                        Call(
                            QueryCompilationContext.QueryContextParameter,
                            StartTrackingMethodInfo,
                            concreteEntityTypeVariable,
                            instanceVariable,
                            shadowValuesVariable))));

            if (recordsComplexShadowValues)
            {
                expressions.Add(
                    Call(BlueTuskComplexShadowValues.ApplyCollectionElementShadowValuesMethod, entryVariable!));
            }
        }

        expressions.Add(instanceVariable);

        return Block(
            returnType,
            variables,
            expressions);
    }

    private BlockExpression CreateFullMaterializeExpression(
        ITypeBase concreteStructuralType,
        (Type ReturnType,
            bool IsNullable,
            ParameterExpression MaterializationContextVariable,
            ParameterExpression ConcreteEntityTypeVariable,
            ParameterExpression ShadowValuesVariable) materializeExpressionContext)
    {
        var (returnType,
            nullable,
            materializationContextVariable,
            _,
            shadowValuesVariable) = materializeExpressionContext;

        var blockExpressions = new List<Expression>(2);

        var materializer = materializerSource
            .CreateMaterializeExpression(
                new StructuralTypeMaterializerSourceParameters(
                    concreteStructuralType, "instance", returnType, nullable, queryTrackingBehavior), materializationContextVariable);

        if (_queryStateManager
            && concreteStructuralType is IRuntimeEntityType { ShadowPropertyCount: > 0 } runtimeEntityType)
        {
            var valueBufferExpression = Call(
                materializationContextVariable, MaterializationContext.GetValueBufferMethod);

            // BlueTusk: EF Core reads only entity-level shadow members here (dotnet/efcore#35613), so shadow properties of
            // non-collection complex types, which share the entity's shadow snapshot, were never materialized. Read every
            // snapshottable shadow member, in shadow-index order, so the snapshot is complete.
            var shadowProperties = GetSnapshottableShadowMembers(runtimeEntityType)
                .OrderBy(e => e.GetShadowIndex())
                .ToList();
            if (shadowProperties.Count != runtimeEntityType.ShadowPropertyCount)
            {
                // Unknown snapshot layout: keep EF Core's entity-level behavior.
                shadowProperties = ((IEnumerable<IPropertyBase>)runtimeEntityType.GetProperties())
                    .Concat(runtimeEntityType.GetNavigations())
                    .Concat(runtimeEntityType.GetSkipNavigations())
                    .Where(n => n.IsShadowProperty())
                    .OrderBy(e => e.GetShadowIndex())
                    .ToList();
            }

            if (shadowProperties.Any(p => p is not IProperty property
                || property.DeclaringType is not IComplexType complexType
                || property != complexType.FindDiscriminatorProperty()))
            {
                blockExpressions.Add(
                    Assign(
                        shadowValuesVariable,
                        BlueTusk.EntityFrameworkCore.ChangeTracking.Internal.BlueTuskShadowValuesFactoryFactory.Instance.CreateConstructorExpression(
                            runtimeEntityType,
                            NewArrayInit(
                                typeof(object),
                                shadowProperties.Select(
                                    p =>
                                        Convert(
                                            // Shadow values of JSON-mapped complex types are not columns; they are merged
                                            // from the JSON shaper before tracking (BlueTuskComplexShadowValues).
                                            BlueTuskComplexShadowValues.IsRecordedEntityShadowProperty(p)
                                                ? Default(p.ClrType)
                                                : valueBufferExpression.CreateValueBufferReadValueExpression(
                                                    p.ClrType, p.GetIndex(), p), typeof(object)))))));
            }
        }

        materializer = materializer.Type == returnType
            ? materializer
            : Convert(materializer, returnType);
        blockExpressions.Add(materializer);

        return Block(blockExpressions);
    }

    private static IEnumerable<IPropertyBase> GetSnapshottableShadowMembers(IEntityType entityType)
    {
        foreach (var navigation in ((IEnumerable<IPropertyBase>)entityType.GetNavigations()).Concat(entityType.GetSkipNavigations()))
        {
            if (navigation.IsShadowProperty())
            {
                yield return navigation;
            }
        }

        foreach (var property in GetStructuralShadowMembers(entityType))
        {
            yield return property;
        }
    }

    private static IEnumerable<IPropertyBase> GetStructuralShadowMembers(ITypeBase structuralType)
    {
        foreach (var property in structuralType.GetProperties())
        {
            if (property.IsShadowProperty())
            {
                yield return property;
            }
        }

        foreach (var complexProperty in structuralType.GetComplexProperties())
        {
            if (complexProperty.IsShadowProperty())
            {
                yield return complexProperty;
            }

            if (complexProperty.IsCollection)
            {
                continue;
            }

            foreach (var property in GetStructuralShadowMembers(complexProperty.ComplexType))
            {
                yield return property;
            }
        }
    }
}

internal sealed class BlueTuskMaterializationConditionConstantLifter(ILiftableConstantFactory liftableConstantFactory) : ExpressionVisitor
{
    private static readonly MethodInfo ServiceProviderGetService =
        typeof(IServiceProvider).GetMethod(nameof(IServiceProvider.GetService), [typeof(Type)])!;

    protected override Expression VisitConstant(ConstantExpression constantExpression)
        => constantExpression switch
        {
            { Value: IEntityType entityTypeValue } => liftableConstantFactory.CreateLiftableConstant(
                constantExpression.Value,
                LiftableConstantExpressionHelpers.BuildMemberAccessLambdaForStructuralType(entityTypeValue),
                entityTypeValue.ShortName() + "EntityType",
                constantExpression.Type),
            { Value: IComplexType complexTypeValue } => liftableConstantFactory.CreateLiftableConstant(
                constantExpression.Value,
                LiftableConstantExpressionHelpers.BuildMemberAccessLambdaForStructuralType(complexTypeValue),
                complexTypeValue.ShortName() + "ComplexType",
                constantExpression.Type),
            { Value: IProperty propertyValue } => liftableConstantFactory.CreateLiftableConstant(
                constantExpression.Value,
                LiftableConstantExpressionHelpers.BuildMemberAccessLambdaForProperty(propertyValue),
                propertyValue.Name + "Property",
                constantExpression.Type),
            _ => base.VisitConstant(constantExpression)
        };

    protected override Expression VisitBinary(BinaryExpression binaryExpression)
    {
        var left = Visit(binaryExpression.Left);
        var right = Visit(binaryExpression.Right);
        var conversion = (LambdaExpression?)Visit(binaryExpression.Conversion);

        return binaryExpression.NodeType is ExpressionType.Assign
            && left is MemberExpression { Member: FieldInfo { IsInitOnly: true } } initFieldMember
                ? initFieldMember.Assign(right)
                : binaryExpression.Update(left, conversion, right);
    }

    protected override Expression VisitExtension(Expression node)
        => node is LiftableConstantExpression ? node : base.VisitExtension(node);
}
