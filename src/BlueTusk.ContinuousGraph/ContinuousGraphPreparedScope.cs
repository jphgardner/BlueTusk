using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Graphs;
using BlueTusk.EntityFrameworkCore.Storage.Internal;
using BlueTusk.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.ContinuousGraph;

internal sealed class ContinuousGraphPreparedScope<TResult, TKey>(
    string sql,
    object[] arguments,
    string[] parameterNames,
    int? timeout,
    Func<DbDataReader, TResult> materialize)
    where TResult : class
    where TKey : notnull
{
    private const string KeyParameterName = "@__bt_scoped_key";

    public static ContinuousGraphPreparedScope<TResult, TKey>? TryCreate(
        DbContext context,
        BlueTuskGraphQueryRootExpression root,
        object[] arguments,
        string keyProperty)
    {
        // Mapped entities and custom EF execution need the ordinary query pipeline.
        var interceptors = context.GetService<IInterceptors>();
        var coreOptions = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>();
        if (interceptors.Aggregate<IDbCommandInterceptor>() is not null ||
            interceptors.Aggregate<IDbConnectionInterceptor>() is not null ||
            coreOptions?.SingletonInterceptors?.Any(static interceptor => interceptor is IMaterializationInterceptor) == true ||
            context.GetService<IEnumerable<ISingletonInterceptor>>().Any(static interceptor => interceptor is IMaterializationInterceptor) ||
            context.Database.CurrentTransaction is not null ||
            context.GetService<IRelationalConnection>() is ITransactionEnlistmentManager { EnlistedTransaction: not null } ||
            context.Database.CreateExecutionStrategy().RetriesOnFailure ||
            context.Model.FindEntityType(typeof(TResult)) is not null ||
            root.EntityType.ConstructorBinding is not ConstructorBinding { ParameterBindings.Count: 0 } constructor ||
            root.EntityType.FindProperty(keyProperty) is not { IsNullable: false } key ||
            key.ClrType != typeof(TKey) ||
            BlueTuskGraphQueryCapture.Find(context, root) is not { } impact ||
            arguments.Any(static argument => argument is not (int or long or Guid)))
        {
            return null;
        }

        var properties = root.EntityType.GetProperties().ToArray();
        if (properties.Length != impact.Projections.Count)
        {
            return null;
        }

        var helper = context.GetService<ISqlGenerationHelper>();
        var reader = Expression.Parameter(typeof(DbDataReader), "reader");
        var bindings = new MemberBinding[properties.Length];
        var columns = new string[properties.Length];
        for (var index = 0; index < properties.Length; index++)
        {
            var property = properties[index];
            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
#pragma warning disable EF1001 // Use the same member EF uses when materializing the registered DTO.
            if (!property.TryGetMemberInfo(forMaterialization: true, forSet: true, out var member, out _) ||
#pragma warning restore EF1001
                member is FieldInfo { IsInitOnly: true } ||
                property.GetTypeMapping().Converter is not null ||
                !impact.Projections.Any(projection => projection.ResultProperty == property.Name) ||
                (type != typeof(int) && type != typeof(long) && type != typeof(Guid) &&
                 type != typeof(string) && type != typeof(bool)))
            {
                return null;
            }

            var ordinal = Expression.Constant(index);
            Expression read = Expression.Call(reader, nameof(DbDataReader.GetFieldValue), [type], ordinal);
            if (type != property.ClrType)
            {
                read = Expression.Convert(read, property.ClrType);
            }

            if (property.IsNullable)
            {
                read = Expression.Condition(
                    Expression.Call(reader, nameof(DbDataReader.IsDBNull), null, ordinal),
                    Expression.Default(property.ClrType), read);
            }

            bindings[index] = Expression.Bind(member!, read);
            columns[index] = "g." + helper.DelimitIdentifier(property.Name);
        }

        var names = new string[arguments.Length];
        for (var index = 0; index < names.Length; index++)
        {
            names[index] = FormattableString.Invariant($"@__bt_scoped_{index}");
        }

        var graphSql = string.Format(CultureInfo.InvariantCulture, root.Sql, (object[])names);
        var sql = $"SELECT {string.Join(", ", columns)} FROM ({graphSql}) AS g " +
            $"WHERE g.{helper.DelimitIdentifier(keyProperty)} = {KeyParameterName} LIMIT 2";
        var body = Expression.MemberInit(Expression.New(constructor.Constructor), bindings);
        var materialize = Expression.Lambda<Func<DbDataReader, TResult>>(body, reader).Compile();
        return new(sql, arguments.ToArray(), names, context.Database.GetCommandTimeout(), materialize);
    }

    public async ValueTask<IReadOnlyList<TResult>> ExecuteAsync(
        ContinuousGraphScopedConnection scope,
        LiveQueryExecutionContext execution,
        TKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dataSource = await scope.ResolveDataSourceAsync(execution, cancellationToken).ConfigureAwait(false)
            ?? throw new ContinuousGraphIncrementalException("The subscriber authorization returned no scoped data source.");
#pragma warning disable EF1001 // Reuse the BlueTusk provider's deferred-reset query connection.
        await using var connection = BlueTuskRelationalConnection.CreateScopedReadConnection(dataSource);
#pragma warning restore EF1001
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (scope.AuthorizeAsync is { } authorize)
        {
            await authorize(connection, execution, cancellationToken).ConfigureAwait(false);
        }

        if (connection.State != ConnectionState.Open)
        {
            throw new ContinuousGraphIncrementalException("Subscriber authorization must leave the scoped connection open.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (timeout is { } commandTimeout)
        {
            command.CommandTimeout = commandTimeout;
        }

        for (var index = 0; index < arguments.Length; index++)
        {
            command.Parameters.Add(new BlueTuskParameter(arguments[index]) { ParameterName = parameterNames[index] });
        }

        command.Parameters.Add(new BlueTuskParameter<TKey>(key) { ParameterName = KeyParameterName });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var row = materialize(reader);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Keep duplicate/out-of-scope validation in the ordinary evaluator.
            return [row, materialize(reader)];
        }

        return [row];
    }
}
