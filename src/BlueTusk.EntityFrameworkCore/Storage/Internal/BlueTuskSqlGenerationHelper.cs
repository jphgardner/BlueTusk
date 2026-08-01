using System.Text;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskSqlGenerationHelper(RelationalSqlGenerationHelperDependencies dependencies)
    : RelationalSqlGenerationHelper(dependencies)
{
    public override string DelimitIdentifier(string identifier) =>
        BlueTuskSqlIdentifier.Delimit(identifier);

    public override void DelimitIdentifier(StringBuilder builder, string identifier) =>
        BlueTuskSqlIdentifier.Append(builder, identifier);

    public override string DelimitIdentifier(string name, string? schema) =>
        BlueTuskSqlIdentifier.Delimit(name, schema);

    public override void DelimitIdentifier(StringBuilder builder, string name, string? schema) =>
        BlueTuskSqlIdentifier.Append(builder, name, schema);
}
