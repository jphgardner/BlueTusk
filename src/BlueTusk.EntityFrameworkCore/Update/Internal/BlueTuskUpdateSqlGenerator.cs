using System.Text;
using Microsoft.EntityFrameworkCore.Update;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies)
    : UpdateSqlGenerator(dependencies)
{
#if BLUETUSK_EF7_OR_GREATER
    internal IUpdateSqlGenerator ReturningRowCountFallback { get; } = new ReturningRowCountGenerator(dependencies);

    public override ResultSetMapping AppendInsertOperation(StringBuilder commandStringBuilder,
        IReadOnlyModificationCommand command, int commandPosition, out bool requiresTransaction)
    {
        var mapping = base.AppendInsertOperation(commandStringBuilder, command, commandPosition, out requiresTransaction);
        return mapping == ResultSetMapping.NoResults
            ? ResultSetMapping.LastInResultSet | ResultSetMapping.ResultSetWithRowsAffectedOnly
            : mapping;
    }

    protected override void AppendReturningClause(StringBuilder commandStringBuilder,
        IReadOnlyList<IColumnModification> operations, string? additionalValues = null)
    {
        // PostgreSQL's CommandComplete already reports the affected row count.
        // Only transmit rows when tracked server-generated values need reading.
        if (operations.Count != 0)
        {
            base.AppendReturningClause(commandStringBuilder, operations, additionalValues);
        }
    }

    private sealed class ReturningRowCountGenerator(UpdateSqlGeneratorDependencies dependencies)
        : UpdateSqlGenerator(dependencies)
    {
        public override ResultSetMapping AppendInsertOperation(StringBuilder builder,
            IReadOnlyModificationCommand command, int position, out bool requiresTransaction)
        {
            var mapping = base.AppendInsertOperation(builder, command, position, out requiresTransaction);
            return mapping == ResultSetMapping.NoResults
                ? ResultSetMapping.LastInResultSet | ResultSetMapping.ResultSetWithRowsAffectedOnly : mapping;
        }

        protected override void AppendReturningClause(StringBuilder builder,
            IReadOnlyList<IColumnModification> operations, string? additionalValues = null)
            => base.AppendReturningClause(builder, operations, operations.Count == 0 ? additionalValues ?? "1" : additionalValues);
    }
#else
    public override ResultSetMapping AppendInsertOperation(
        StringBuilder builder, IReadOnlyModificationCommand command, int commandPosition)
    {
        var writes = command.ColumnModifications.Where(column => column.IsWrite).ToArray();
        AppendInsertCommandHeader(builder, command.TableName, command.Schema, writes);
        AppendValuesHeader(builder, writes);
        AppendValues(builder, command.TableName, command.Schema, writes);
        return AppendLegacyResult(builder, command);
    }

    public override ResultSetMapping AppendUpdateOperation(
        StringBuilder builder, IReadOnlyModificationCommand command, int commandPosition)
    {
        var writes = command.ColumnModifications.Where(column => column.IsWrite).ToArray();
        var conditions = command.ColumnModifications.Where(column => column.IsCondition).ToArray();
        AppendUpdateCommandHeader(builder, command.TableName, command.Schema, writes);
        AppendWhereClause(builder, conditions);
        return AppendLegacyResult(builder, command);
    }

    public override ResultSetMapping AppendDeleteOperation(
        StringBuilder builder, IReadOnlyModificationCommand command, int commandPosition)
    {
        var conditions = command.ColumnModifications.Where(column => column.IsCondition).ToArray();
        AppendDeleteCommandHeader(builder, command.TableName, command.Schema);
        AppendWhereClause(builder, conditions);
        return AppendLegacyResult(builder, command);
    }

    public override void AppendNextSequenceValueOperation(StringBuilder builder, string name, string? schema) =>
        builder.Append("SELECT nextval('")
            .Append(SqlGenerationHelper.DelimitIdentifier(name, schema).Replace("'", "''", StringComparison.Ordinal))
            .Append("')");

    protected override void AppendIdentityWhereCondition(StringBuilder builder, IColumnModification column) =>
        throw new NotSupportedException("The PostgreSQL update generator reads generated values through RETURNING.");

    protected override void AppendRowsAffectedWhereCondition(StringBuilder builder, int expectedRowsAffected) =>
        throw new NotSupportedException("The PostgreSQL update generator reads row counts through RETURNING.");

    private ResultSetMapping AppendLegacyResult(StringBuilder builder, IReadOnlyModificationCommand command)
    {
        var readColumns = command.ColumnModifications.Where(column => column.IsRead).ToArray();
        builder.AppendLine().Append("RETURNING ");
        if (readColumns.Length == 0)
        {
            builder.Append('1');
        }
        else
        {
            builder.AppendJoin(", ", readColumns.Select(column => SqlGenerationHelper.DelimitIdentifier(column.ColumnName)));
        }

        builder.Append(SqlGenerationHelper.StatementTerminator).AppendLine();
        return ResultSetMapping.LastInResultSet;
    }
#endif
}
