using System.Text;
using Microsoft.EntityFrameworkCore.Update;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskUpdateSqlGenerator(UpdateSqlGeneratorDependencies dependencies)
    : UpdateSqlGenerator(dependencies)
{
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
        => base.AppendReturningClause(commandStringBuilder, operations,
            operations.Count == 0 ? additionalValues ?? "1" : additionalValues);
}
