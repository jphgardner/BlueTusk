using System.Data.Common;

namespace BlueTusk.Projections.LoadHarness;

internal static class Sql
{
    internal static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.CommandTimeout = 30;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
        }
        return command;
    }
    internal static async Task ExecuteAsync(DbDataSource source, string sql, CancellationToken token)
    { await using var connection = await source.OpenConnectionAsync(token); await using var command = Command(connection, null, sql); await command.ExecuteNonQueryAsync(token); }
}
