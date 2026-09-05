using System.Text;
using Microsoft.EntityFrameworkCore.Update;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies)
    : UpdateSqlGenerator(dependencies)
{
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
}
