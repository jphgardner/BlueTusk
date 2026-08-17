using System.Data;
using BlueTusk.Data;

namespace BlueTusk.Extensions.PgDurable;

/// <summary>Controls whether a durable instance starts in the caller's transaction or a new transaction.</summary>
public enum BlueTuskPgDurableTransactionMode
{
    Caller,
    New,
}

/// <summary>The lifecycle state reported by pg_durable for an instance.</summary>
public enum BlueTuskPgDurableStatus
{
    Unknown,
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Aggregated pg_durable execution metrics visible to the current role.</summary>
public sealed record BlueTuskPgDurableMetrics(
    long TotalInstances,
    long RunningInstances,
    long CompletedInstances,
    long FailedInstances,
    long TotalExecutions,
    long TotalEvents);

public static class BlueTuskPgDurableDataSourceExtensions
{
    /// <summary>Returns the installed pg_durable extension version.</summary>
    public static async ValueTask<string> GetPgDurableVersionAsync(
        this BlueTuskDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        _ = GetFeature(dataSource);
        await using var command = dataSource.CreateCommand(
            "SELECT extversion FROM pg_catalog.pg_extension WHERE extname = 'pg_durable'");
        var version = await command.ExecuteScalarAsync<string>(cancellationToken).ConfigureAwait(false);
        return version ?? throw new InvalidOperationException("The pg_durable extension is not installed.");
    }

    /// <summary>Starts a durable SQL workflow and returns its pg_durable instance ID.</summary>
    public static async ValueTask<string> StartPgDurableAsync(
        this BlueTuskDataSource dataSource,
        string workflow,
        string? label = null,
        string? database = null,
        BlueTuskPgDurableTransactionMode transactionMode = BlueTuskPgDurableTransactionMode.Caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);
        ValidateOptionalText(label, nameof(label));
        ValidateOptionalText(database, nameof(database));
        _ = GetFeature(dataSource);

        var mode = transactionMode switch
        {
            BlueTuskPgDurableTransactionMode.Caller => "caller",
            BlueTuskPgDurableTransactionMode.New => "new",
            _ => throw new ArgumentOutOfRangeException(nameof(transactionMode)),
        };
        await using var command = dataSource.CreateCommand(
            "SELECT \"df\".\"start\"($1::text, $2::text, $3::text, $4::text)");
        command.Parameters.Add(CreateTextParameter(workflow));
        command.Parameters.Add(CreateTextParameter(label));
        command.Parameters.Add(CreateTextParameter(database));
        command.Parameters.Add(CreateTextParameter(mode));
        var instanceId = await command.ExecuteScalarAsync<string>(cancellationToken).ConfigureAwait(false);
        return instanceId ?? throw new InvalidOperationException("pg_durable did not return an instance ID.");
    }

    /// <summary>Returns the current lifecycle state for a pg_durable instance.</summary>
    public static async ValueTask<BlueTuskPgDurableStatus> GetPgDurableStatusAsync(
        this BlueTuskDataSource dataSource,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ValidateInstanceId(instanceId);
        _ = GetFeature(dataSource);
        await using var command = dataSource.CreateCommand(
            "SELECT \"df\".\"status\"($1::text)");
        command.Parameters.Add(CreateTextParameter(instanceId));
        var status = await command.ExecuteScalarAsync<string>(cancellationToken).ConfigureAwait(false);
        return ParseStatus(status);
    }

    /// <summary>Returns the completed root result as pg_durable JSON text, or null while no result exists.</summary>
    public static async ValueTask<string?> GetPgDurableResultAsync(
        this BlueTuskDataSource dataSource,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        ValidateInstanceId(instanceId);
        _ = GetFeature(dataSource);
        await using var command = dataSource.CreateCommand(
            "SELECT \"df\".\"result\"($1::text)");
        command.Parameters.Add(CreateTextParameter(instanceId));
        return await command.ExecuteScalarAsync<string>(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until a pg_durable instance reaches a terminal state and returns that state.</summary>
    public static async ValueTask<BlueTuskPgDurableStatus> AwaitPgDurableAsync(
        this BlueTuskDataSource dataSource,
        string instanceId,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        ValidateInstanceId(instanceId);
        if (timeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutSeconds),
                "A pg_durable wait timeout must be positive.");
        }

        _ = GetFeature(dataSource);
        await using var command = dataSource.CreateCommand(
            "SELECT \"df\".\"await_instance\"($1::text, $2::int4)");
        command.Parameters.Add(CreateTextParameter(instanceId));
        command.Parameters.Add(new BlueTuskParameter<int>(timeoutSeconds));
        var status = await command.ExecuteScalarAsync<string>(cancellationToken).ConfigureAwait(false);
        return ParseStatus(status);
    }

    /// <summary>Cancels a pg_durable instance and returns the server acknowledgement.</summary>
    public static async ValueTask<string> CancelPgDurableAsync(
        this BlueTuskDataSource dataSource,
        string instanceId,
        string reason = "Cancelled by user",
        CancellationToken cancellationToken = default)
    {
        ValidateInstanceId(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _ = GetFeature(dataSource);
        await using var command = dataSource.CreateCommand(
            "SELECT \"df\".\"cancel\"($1::text, $2::text)");
        command.Parameters.Add(CreateTextParameter(instanceId));
        command.Parameters.Add(CreateTextParameter(reason));
        var acknowledgement = await command.ExecuteScalarAsync<string>(cancellationToken).ConfigureAwait(false);
        return acknowledgement ?? throw new InvalidOperationException("pg_durable did not acknowledge cancellation.");
    }

    /// <summary>Sends an external signal to a waiting pg_durable instance.</summary>
    public static async ValueTask<string> SignalPgDurableAsync(
        this BlueTuskDataSource dataSource,
        string instanceId,
        string signalName,
        string signalData = "{}",
        CancellationToken cancellationToken = default)
    {
        ValidateInstanceId(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(signalName);
        ArgumentNullException.ThrowIfNull(signalData);
        _ = GetFeature(dataSource);
        await using var command = dataSource.CreateCommand(
            "SELECT \"df\".\"signal\"($1::text, $2::text, $3::text)");
        command.Parameters.Add(CreateTextParameter(instanceId));
        command.Parameters.Add(CreateTextParameter(signalName));
        command.Parameters.Add(CreateTextParameter(signalData));
        var acknowledgement = await command.ExecuteScalarAsync<string>(cancellationToken).ConfigureAwait(false);
        return acknowledgement ?? throw new InvalidOperationException("pg_durable did not acknowledge the signal.");
    }

    /// <summary>Returns aggregate pg_durable execution metrics visible to the current role.</summary>
    public static async ValueTask<BlueTuskPgDurableMetrics> GetPgDurableMetricsAsync(
        this BlueTuskDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        _ = GetFeature(dataSource);
        await using var command = dataSource.CreateCommand(
            "SELECT * FROM \"df\".\"metrics\"()");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("pg_durable metrics returned no row.");
        }

        return new BlueTuskPgDurableMetrics(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }

    private static BlueTuskPgDurableFeature GetFeature(BlueTuskDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return dataSource.Features.GetRequired<BlueTuskPgDurableFeature>(
            BlueTuskPgDurableFeature.RegistryName);
    }

    private static BlueTuskPgDurableStatus ParseStatus(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "pending" => BlueTuskPgDurableStatus.Pending,
            "running" => BlueTuskPgDurableStatus.Running,
            "completed" => BlueTuskPgDurableStatus.Completed,
            "failed" => BlueTuskPgDurableStatus.Failed,
            "cancelled" => BlueTuskPgDurableStatus.Cancelled,
            _ => BlueTuskPgDurableStatus.Unknown,
        };

    private static BlueTuskParameter CreateTextParameter(string? value) =>
        new(value) { DbType = DbType.String };

    private static void ValidateInstanceId(string instanceId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

    private static void ValidateOptionalText(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be empty or whitespace.", parameterName);
        }
    }
}
