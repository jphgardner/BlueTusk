namespace BlueTusk.Client;

/// <summary>One unnamed statement in an extended-query batch.</summary>
public sealed record BlueTuskBatchQuery(
    string Sql,
    IReadOnlyList<BlueTuskExtendedQueryParameter> Parameters,
    bool UseBinaryResults = true);

internal readonly record struct BlueTuskBatchCommandExecution
{
    internal BlueTuskBatchCommandExecution(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters)
    {
        Sql = sql;
        Parameters = parameters;
        SingleParameter = default;
    }

    internal BlueTuskBatchCommandExecution(
        string sql,
        BlueTuskExtendedQueryParameter parameter)
    {
        Sql = sql;
        Parameters = null;
        SingleParameter = parameter;
    }

    internal string Sql { get; }

    internal IReadOnlyList<BlueTuskExtendedQueryParameter>? Parameters { get; }

    internal BlueTuskExtendedQueryParameter SingleParameter { get; }

    internal int ParameterCount => Parameters?.Count ?? (SingleParameter.TypeOid == 0 ? 0 : 1);

    internal BlueTuskExtendedQueryParameter GetParameter(int index)
    {
        if (Parameters is not null)
        {
            return Parameters[index];
        }

        ArgumentOutOfRangeException.ThrowIfNotEqual(index, 0);
        if (SingleParameter.TypeOid == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return SingleParameter;
    }

    internal IReadOnlyList<BlueTuskExtendedQueryParameter> MaterializeParameters() =>
        Parameters ?? (SingleParameter.TypeOid == 0 ? [] : [SingleParameter]);
}

/// <summary>One named prepared statement execution in an extended-query batch.</summary>
public sealed record BlueTuskPreparedBatchQuery(
    string StatementName,
    IReadOnlyList<BlueTuskExtendedQueryParameter> Parameters,
    bool UseBinaryResults = true);
