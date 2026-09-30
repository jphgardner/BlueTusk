using System.Data.Common;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Data;

namespace BlueTusk.Schema;

public sealed class SchemaConcurrentIndexInvalidException : InvalidOperationException
{
    public SchemaConcurrentIndexInvalidException(SchemaRelationIdentity relation, string indexName)
        : base($"Index {relation.Schema}.{indexName} on {relation.Schema}.{relation.Name} is invalid or unfinished; inspect and repair it before retrying.") { }
}

public sealed class SchemaConcurrentIndexDriftException : InvalidOperationException
{
    public SchemaConcurrentIndexDriftException(SchemaRelationIdentity relation, string indexName)
        : base($"Index identity or definition for {relation.Schema}.{indexName} differs from the reviewed declaration.") { }
}

/// <summary>Reconciles named PostgreSQL indexes before and after standalone concurrent builds.</summary>
public sealed class PostgreSqlConcurrentIndexDeployer
{
    private readonly BlueTuskDataSource _source;
    private readonly PostgreSqlSchemaDeploymentCoordinator _coordinator;
    private readonly int _commandTimeout;

    public PostgreSqlConcurrentIndexDeployer(BlueTuskDataSource source,
        PostgreSqlSchemaDeploymentCoordinator coordinator, int maximumBuildSeconds = 3600)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(coordinator);
        if (!ReferenceEquals(source, coordinator.Source))
        { throw new ArgumentException("Index builds and their journal must use the same data source instance.", nameof(source)); }
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBuildSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBuildSeconds, 86400);
        _source = source; _coordinator = coordinator; _commandTimeout = maximumBuildSeconds;
    }

    /// <summary>Explicit recovery entry point: inspect each index, build only absent names, then journal physical evidence.</summary>
    public async ValueTask<string> ReconcileAndApplyAsync(SchemaConcurrentIndexPlan plan,
        SchemaDeploymentLease lease, SchemaDeploymentDefinition definition, string stepId,
        SchemaDeploymentStepAttempt pending, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(pending);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (definition.Plan.BeforeFingerprint != plan.BeforeFingerprint ||
            definition.Plan.AfterFingerprint != plan.AfterFingerprint ||
            !definition.ActionIndex.TryGetValue(stepId, out var action) ||
            definition.Plan.StepIndex[stepId].Phase != SchemaDeploymentPhase.Expand ||
            action.Kind != SchemaDeploymentActionKind.External || action.Content != plan.ActionContent ||
            pending.StepId != stepId || pending.State != SchemaDeploymentAttemptState.Pending ||
            pending.ActionFingerprint != action.Fingerprint || pending.Attempt < 1)
        { throw new InvalidOperationException("The index plan, deployment manifest and pending step do not match."); }
        if (leaseDuration < TimeSpan.FromSeconds(1) || leaseDuration > TimeSpan.FromMinutes(5))
        { throw new ArgumentOutOfRangeException(nameof(leaseDuration)); }
        var actual = await _coordinator.ReadStepAsync(lease.DeploymentId, definition, stepId, cancellationToken)
            .ConfigureAwait(false);
        if (actual is not { State: SchemaDeploymentAttemptState.Pending } || actual.Attempt != pending.Attempt ||
            actual.ActionFingerprint != pending.ActionFingerprint ||
            !await _coordinator.RenewAsync(lease, definition, leaseDuration, cancellationToken).ConfigureAwait(false))
        { throw new InvalidOperationException("The pending index attempt or database-clock lease is no longer current."); }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(_commandTimeout));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(bounded.Token);
        var heartbeat = HeartbeatAsync(lease, definition, leaseDuration, stop);
        string? evidence = null;
        Exception? failure = null;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, "BlueTusk.Schema.ConcurrentIndexObserved:1"); Append(hash, plan.Fingerprint);
            foreach (var build in plan.Builds)
            {
                var state = await ProbeAsync(build, stop.Token).ConfigureAwait(false);
                if (state == IndexState.Absent)
                {
                    try { await BuildAsync(build, stop.Token).ConfigureAwait(false); }
                    catch (Exception) when (!stop.IsCancellationRequested)
                    {
                        // A lost response can follow a successful build. Never infer success from a command result alone.
                        if (await ProbeAsync(build, stop.Token).ConfigureAwait(false) != IndexState.Valid) { throw; }
                    }
                    if (await ProbeAsync(build, stop.Token).ConfigureAwait(false) != IndexState.Valid)
                    { throw new SchemaConcurrentIndexDriftException(build.Relation, build.IndexName); }
                }
                Append(hash, build.Relation.Schema); Append(hash, build.Relation.Name);
                Append(hash, build.IndexName); Append(hash, build.ExpectedDefinition); Append(hash, "valid-ready-live");
            }
            if (!await _coordinator.RenewAsync(lease, definition, leaseDuration, stop.Token).ConfigureAwait(false))
            { throw new InvalidOperationException("The index deployment lost its database-clock lease."); }
            evidence = Convert.ToHexStringLower(hash.GetHashAndReset());
            await _coordinator.CompleteExternalStepAsync(lease, definition, stepId, pending.Attempt,
                evidence, cancellationToken: stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) { failure = exception; }
        stop.Cancel();
        try { await heartbeat.ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception exception) { failure ??= exception; }
        if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        return evidence!;
    }

    private async Task HeartbeatAsync(SchemaDeploymentLease lease, SchemaDeploymentDefinition definition,
        TimeSpan leaseDuration, CancellationTokenSource stop)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(250, leaseDuration.TotalMilliseconds / 3));
        try
        {
            while (true)
            {
                await Task.Delay(interval, stop.Token).ConfigureAwait(false);
                if (!await _coordinator.RenewAsync(lease, definition, leaseDuration, stop.Token).ConfigureAwait(false))
                { throw new InvalidOperationException("The index deployment lost its database-clock lease."); }
            }
        }
        catch
        {
            stop.Cancel();
            throw;
        }
    }

    private async ValueTask BuildAsync(SchemaConcurrentIndexBuild build, CancellationToken token)
    {
        await using var connection = await _source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "SET search_path = pg_catalog"; setup.CommandTimeout = 30;
            _ = await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await using var command = (BlueTuskCommand)connection.CreateCommand();
        command.CommandText = build.CreateSql; command.CommandTimeout = _commandTimeout;
        command.ExecutionMode = BlueTuskCommandExecutionMode.Extended;
        await command.PrepareAsync(token).ConfigureAwait(false);
        _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async ValueTask<IndexState> ProbeAsync(SchemaConcurrentIndexBuild build, CancellationToken token)
    {
        await using var connection = await _source.OpenConnectionAsync(token).ConfigureAwait(false);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "SET search_path = pg_catalog"; setup.CommandTimeout = 30;
            _ = await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = """
            SELECT c.relkind::text, table_namespace.nspname, relation.relname,
                i.indisvalid, i.indisready, i.indislive, pg_catalog.pg_get_indexdef(c.oid)
            FROM pg_catalog.pg_class AS c
            JOIN pg_catalog.pg_namespace AS index_namespace ON index_namespace.oid = c.relnamespace
            LEFT JOIN pg_catalog.pg_index AS i ON i.indexrelid = c.oid
            LEFT JOIN pg_catalog.pg_class AS relation ON relation.oid = i.indrelid
            LEFT JOIN pg_catalog.pg_namespace AS table_namespace ON table_namespace.oid = relation.relnamespace
            WHERE index_namespace.nspname = @schema AND c.relname = @index
            """;
        Add(command, "schema", build.Relation.Schema); Add(command, "index", build.IndexName);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) { return IndexState.Absent; }
        if (reader.GetString(0) != "i" || reader.IsDBNull(1) || reader.IsDBNull(2) ||
            reader.GetString(1) != build.Relation.Schema || reader.GetString(2) != build.Relation.Name ||
            reader.IsDBNull(3) || reader.GetString(6) != build.ExpectedDefinition)
        { throw new SchemaConcurrentIndexDriftException(build.Relation, build.IndexName); }
        return reader.GetBoolean(3) && reader.GetBoolean(4) && reader.GetBoolean(5)
            ? IndexState.Valid : throw new SchemaConcurrentIndexInvalidException(build.Relation, build.IndexName);
    }

    private static void Add(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length); hash.AppendData(bytes);
    }

    private enum IndexState { Absent, Valid }
}
