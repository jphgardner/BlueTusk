using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Storage.Internal;

internal sealed class BlueTuskDatabaseCreator(RelationalDatabaseCreatorDependencies dependencies)
    : RelationalDatabaseCreator(dependencies)
{
    public override bool Exists()
    {
        try
        {
            Dependencies.Connection.Open(errorsExpected: true);
            Dependencies.Connection.Close();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public override void Create()
        => throw new NotSupportedException("Physical database creation is not available in this preview.");

    public override void Delete()
        => throw new NotSupportedException("Physical database deletion is not available in this preview.");

    public override bool HasTables()
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS c
                JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
                WHERE c.relkind IN ('r', 'p')
                  AND n.nspname NOT IN ('pg_catalog', 'information_schema'))
            """;

        Dependencies.Connection.Open();
        try
        {
            using var command = Dependencies.Connection.DbConnection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar() is true;
        }
        finally
        {
            Dependencies.Connection.Close();
        }
    }
}
