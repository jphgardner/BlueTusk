using System.Linq.Expressions;
using BlueTusk.EntityFrameworkCore.Update.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore;

/// <summary>PostgreSQL-specific, model-driven <c>MERGE</c> operations.</summary>
public static class BlueTuskMergeExtensions
{
    /// <summary>
    /// Merges one source row, updating the selected properties when the match succeeds and inserting the
    /// initialized properties when it does not.
    /// </summary>
    public static int ExecuteMerge<TEntity, TMatch, TUpdate>(
        this DbContext context,
        Expression<Func<TEntity>> values,
        Expression<Func<TEntity, TMatch>> matchProperties,
        Expression<Func<TEntity, TUpdate>> updateProperties)
        where TEntity : class
        => Execute(
            context,
            BlueTuskMergeCommandFactory.Create(
                context,
                values,
                matchProperties,
                updateProperties,
                BlueTuskMergeMatchedAction.Update));

    /// <summary>
    /// Asynchronously merges one source row, updating the selected properties when the match succeeds and
    /// inserting the initialized properties when it does not.
    /// </summary>
    public static Task<int> ExecuteMergeAsync<TEntity, TMatch, TUpdate>(
        this DbContext context,
        Expression<Func<TEntity>> values,
        Expression<Func<TEntity, TMatch>> matchProperties,
        Expression<Func<TEntity, TUpdate>> updateProperties,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => ExecuteAsync(
            context,
            BlueTuskMergeCommandFactory.Create(
                context,
                values,
                matchProperties,
                updateProperties,
                BlueTuskMergeMatchedAction.Update),
            cancellationToken);

    /// <summary>Deletes a matched target row, or inserts the initialized properties when no row matches.</summary>
    public static int ExecuteMergeDelete<TEntity, TMatch>(
        this DbContext context,
        Expression<Func<TEntity>> values,
        Expression<Func<TEntity, TMatch>> matchProperties)
        where TEntity : class
        => Execute(
            context,
            BlueTuskMergeCommandFactory.Create(
                context,
                values,
                matchProperties,
                updateProperties: null,
                BlueTuskMergeMatchedAction.Delete));

    /// <summary>
    /// Asynchronously deletes a matched target row, or inserts the initialized properties when no row matches.
    /// </summary>
    public static Task<int> ExecuteMergeDeleteAsync<TEntity, TMatch>(
        this DbContext context,
        Expression<Func<TEntity>> values,
        Expression<Func<TEntity, TMatch>> matchProperties,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => ExecuteAsync(
            context,
            BlueTuskMergeCommandFactory.Create(
                context,
                values,
                matchProperties,
                updateProperties: null,
                BlueTuskMergeMatchedAction.Delete),
            cancellationToken);

    /// <summary>Leaves a matched target row unchanged, or inserts the initialized properties when no row matches.</summary>
    public static int ExecuteMergeDoNothing<TEntity, TMatch>(
        this DbContext context,
        Expression<Func<TEntity>> values,
        Expression<Func<TEntity, TMatch>> matchProperties)
        where TEntity : class
        => Execute(
            context,
            BlueTuskMergeCommandFactory.Create(
                context,
                values,
                matchProperties,
                updateProperties: null,
                BlueTuskMergeMatchedAction.DoNothing));

    /// <summary>
    /// Asynchronously leaves a matched target row unchanged, or inserts the initialized properties when no row matches.
    /// </summary>
    public static Task<int> ExecuteMergeDoNothingAsync<TEntity, TMatch>(
        this DbContext context,
        Expression<Func<TEntity>> values,
        Expression<Func<TEntity, TMatch>> matchProperties,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => ExecuteAsync(
            context,
            BlueTuskMergeCommandFactory.Create(
                context,
                values,
                matchProperties,
                updateProperties: null,
                BlueTuskMergeMatchedAction.DoNothing),
            cancellationToken);

    private static int Execute(DbContext context, BlueTuskMergeCommandPlan plan)
        => plan.Command.ExecuteNonQuery(CreateParameterObject(context, plan));

    private static Task<int> ExecuteAsync(
        DbContext context,
        BlueTuskMergeCommandPlan plan,
        CancellationToken cancellationToken)
        => plan.Command.ExecuteNonQueryAsync(CreateParameterObject(context, plan), cancellationToken);

    private static RelationalCommandParameterObject CreateParameterObject(
        DbContext context,
        BlueTuskMergeCommandPlan plan)
        => new(
            context.GetService<IRelationalConnection>(),
            plan.ParameterValues,
            readerColumns: null,
            context,
            context.GetService<IRelationalCommandDiagnosticsLogger>(),
            CommandSource.ExecuteSqlRaw);
}
