using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlueTusk.ControlPlane;

/// <summary>
/// PostgreSQL-backed desired state, observed state, fencing leases, and schema migration.
/// </summary>
public sealed class PostgreSqlManagedDeploymentStore :
    IManagedDeploymentStore,
    IManagedDeploymentLeaseStore
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentDocumentFormatVersion = 1;

    private static readonly Regex SchemaPattern = new(
        "^[A-Za-z_][A-Za-z0-9_$]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string SelectColumns =
        "SELECT desired_document::text, document_format, state, observed_generation, " +
        "status_revision, fencing_token, desired_fingerprint, applied_plan_fingerprint, " +
        "provider_resource_id, diagnostic_code, updated_at";

    private readonly DbDataSource _dataSource;
    private readonly string _controlSchema;
    private readonly string _schema;

    public PostgreSqlManagedDeploymentStore(
        DbDataSource dataSource,
        string controlSchema = "bluetusk_control")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlSchema);
        if (!SchemaPattern.IsMatch(controlSchema))
        {
            throw new ArgumentException(
                "The control schema must be one unquoted PostgreSQL identifier.",
                nameof(controlSchema));
        }

        _dataSource = dataSource;
        _controlSchema = controlSchema;
        _schema = $"\"{controlSchema}\"";
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        await using (var migrationLock = connection.CreateCommand())
        {
            migrationLock.Transaction = transaction;
            migrationLock.CommandText =
                "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@key, 0))";
            AddParameter(migrationLock, "key", _controlSchema + ":managed-hosting");
            _ = await migrationLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            $"CREATE SCHEMA IF NOT EXISTS {_schema}",
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            transaction,
            $"""
             CREATE TABLE IF NOT EXISTS {_schema}.managed_hosting_metadata (
                 singleton boolean PRIMARY KEY DEFAULT TRUE CHECK (singleton),
                 schema_version integer NOT NULL CHECK (schema_version > 0)
             )
             """,
            cancellationToken).ConfigureAwait(false);
        await using (var metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText =
                $"""
                 INSERT INTO {_schema}.managed_hosting_metadata (singleton, schema_version)
                 VALUES (TRUE, @version)
                 ON CONFLICT (singleton) DO NOTHING
                 """;
            AddParameter(metadata, "version", CurrentSchemaVersion);
            _ = await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var version = await GetSchemaVersionAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (version != CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Managed-hosting schema version {version} is not supported by this process.");
        }

        string[] statements =
        [
            $"""
             CREATE TABLE IF NOT EXISTS {_schema}.managed_deployments (
                 deployment_id text PRIMARY KEY,
                 tenant_id text NOT NULL,
                 provider text NOT NULL,
                 region text NOT NULL,
                 desired_generation bigint NOT NULL CHECK (desired_generation > 0),
                 document_format integer NOT NULL CHECK (document_format > 0),
                 desired_document jsonb NOT NULL,
                 state text NOT NULL,
                 observed_generation bigint NOT NULL CHECK (observed_generation >= 0),
                 status_revision bigint NOT NULL CHECK (status_revision >= 0),
                 fencing_token bigint NULL CHECK (fencing_token > 0),
                 desired_fingerprint text NULL,
                 applied_plan_fingerprint text NULL,
                 provider_resource_id text NULL,
                 diagnostic_code text NULL,
                 updated_at timestamptz NOT NULL
             )
             """,
            $"""
             CREATE INDEX IF NOT EXISTS managed_deployments_tenant_idx
             ON {_schema}.managed_deployments (tenant_id, deployment_id)
             """,
            $"""
             CREATE TABLE IF NOT EXISTS {_schema}.managed_deployment_leases (
                 deployment_id text PRIMARY KEY,
                 owner text NULL,
                 last_fencing_token bigint NOT NULL CHECK (last_fencing_token >= 0),
                 expires_at timestamptz NULL,
                 CHECK (
                     (owner IS NULL AND expires_at IS NULL) OR
                     (owner IS NOT NULL AND expires_at IS NOT NULL)
                 )
             )
             """,
        ];
        foreach (var statement in statements)
        {
            await ExecuteAsync(connection, transaction, statement, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> GetSchemaVersionAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await GetSchemaVersionAsync(connection, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ManagedDeployment?> GetAsync(
        string deploymentId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(deploymentId, 128, nameof(deploymentId));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + $" FROM {_schema}.managed_deployments WHERE deployment_id = @id";
        AddParameter(command, "id", deploymentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDeployment(reader)
            : null;
    }

    public async IAsyncEnumerable<ManagedDeployment> ListAsync(
        string? tenantId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (tenantId is not null)
        {
            ValidateIdentifier(tenantId, 128, nameof(tenantId));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = tenantId is null
            ? SelectColumns +
              $" FROM {_schema}.managed_deployments ORDER BY deployment_id"
            : SelectColumns +
              $" FROM {_schema}.managed_deployments WHERE tenant_id = @tenant ORDER BY deployment_id";
        if (tenantId is not null)
        {
            AddParameter(command, "tenant", tenantId);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return ReadDeployment(reader);
        }
    }

    public async ValueTask<ManagedDeployment> PutAsync(
        ManagedDeploymentSpec spec,
        long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        ManagedDeploymentValidation.Validate(spec);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedGeneration);

        var document = JsonSerializer.Serialize(spec, JsonOptions);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        var existing = await ReadForUpdateAsync(
            connection,
            transaction,
            spec.DeploymentId,
            cancellationToken).ConfigureAwait(false);
        ManagedDeployment result;
        if (existing is null)
        {
            if (expectedGeneration != 0 || spec.Generation != 1)
            {
                throw Conflict(
                    "A new deployment must use expected generation zero and generation one.");
            }

            var now = DateTimeOffset.UtcNow;
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                $"""
                 INSERT INTO {_schema}.managed_deployments (
                     deployment_id, tenant_id, provider, region, desired_generation,
                     document_format, desired_document, state, observed_generation,
                     status_revision, updated_at)
                 SELECT
                     @id, @tenant, @provider, @region, @generation,
                     @format, CAST(@document AS jsonb), @state, 0, 0, @updated
                 FROM {_schema}.managed_hosting_metadata
                 WHERE singleton AND schema_version = @schema_version
                 """;
            AddSpecParameters(insert, spec, document);
            AddParameter(insert, "state", ManagedDeploymentState.Pending.ToString());
            AddParameter(insert, "updated", now);
            AddParameter(insert, "schema_version", CurrentSchemaVersion);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "Managed-hosting writes require the current schema version.");
            }

            result = new ManagedDeployment(
                Clone(spec),
                new ManagedDeploymentStatus(
                    ManagedDeploymentState.Pending,
                    0,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    now));
        }
        else
        {
            if (existing.Spec.Generation != expectedGeneration)
            {
                throw Conflict("Desired-state generation no longer matches.");
            }

            if (spec.Generation == existing.Spec.Generation)
            {
                if (!string.Equals(
                        ManagedDeploymentValidation.GetFingerprint(existing.Spec),
                        ManagedDeploymentValidation.GetFingerprint(spec),
                        StringComparison.Ordinal))
                {
                    throw Conflict("A generation cannot identify two different desired states.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }

            if (spec.Generation != checked(existing.Spec.Generation + 1))
            {
                throw Conflict("Desired-state generations must increase by exactly one.");
            }

            if (!string.Equals(spec.TenantId, existing.Spec.TenantId, StringComparison.Ordinal) ||
                !string.Equals(spec.Provider, existing.Spec.Provider, StringComparison.Ordinal) ||
                !string.Equals(spec.Region, existing.Spec.Region, StringComparison.Ordinal))
            {
                throw new ManagedDeploymentValidationException(
                    "deployment-placement-immutable",
                    "Tenant, provider, and region cannot change after deployment creation.");
            }

            var now = DateTimeOffset.UtcNow;
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                $"""
                 UPDATE {_schema}.managed_deployments
                 SET desired_generation = @generation,
                     document_format = @format,
                     desired_document = CAST(@document AS jsonb),
                     state = @state,
                     updated_at = @updated
                 WHERE deployment_id = @id
                   AND desired_generation = @expected_generation
                 """;
            AddSpecParameters(update, spec, document);
            AddParameter(update, "state", ManagedDeploymentState.Pending.ToString());
            AddParameter(update, "updated", now);
            AddParameter(update, "expected_generation", expectedGeneration);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw Conflict("Desired state changed while it was being updated.");
            }

            result = new ManagedDeployment(
                Clone(spec),
                existing.Status with
                {
                    State = ManagedDeploymentState.Pending,
                    UpdatedAt = now,
                });
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<ManagedDeployment> UpdateStatusAsync(
        string deploymentId,
        ManagedDeploymentStatus status,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(deploymentId, 128, nameof(deploymentId));
        ValidateStatus(status, expectedRevision);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             UPDATE {_schema}.managed_deployments
             SET state = @state,
                 observed_generation = @observed_generation,
                 status_revision = @revision,
                 fencing_token = @fencing_token,
                 desired_fingerprint = @desired_fingerprint,
                 applied_plan_fingerprint = @applied_plan_fingerprint,
                 provider_resource_id = @provider_resource_id,
                 diagnostic_code = @diagnostic_code,
                 updated_at = @updated_at
             WHERE deployment_id = @id
               AND desired_generation = @observed_generation
               AND status_revision = @expected_revision
               AND EXISTS (
                   SELECT 1
                   FROM {_schema}.managed_deployment_leases lease
                   WHERE lease.deployment_id = @id
                     AND lease.owner IS NOT NULL
                     AND lease.last_fencing_token = @fencing_token
                     AND lease.expires_at > pg_catalog.clock_timestamp())
               AND EXISTS (
                   SELECT 1
                   FROM {_schema}.managed_hosting_metadata
                   WHERE singleton AND schema_version = @schema_version)
             RETURNING desired_document::text, document_format
             """;
        AddParameter(command, "state", status.State.ToString());
        AddParameter(command, "observed_generation", status.ObservedGeneration);
        AddParameter(command, "revision", status.Revision);
        AddParameter(command, "fencing_token", status.FencingToken);
        AddParameter(command, "desired_fingerprint", status.DesiredFingerprint);
        AddParameter(command, "applied_plan_fingerprint", status.AppliedPlanFingerprint);
        AddParameter(command, "provider_resource_id", status.ProviderResourceId);
        AddParameter(command, "diagnostic_code", status.DiagnosticCode);
        AddParameter(command, "updated_at", status.UpdatedAt);
        AddParameter(command, "id", deploymentId);
        AddParameter(command, "expected_revision", expectedRevision);
        AddParameter(command, "schema_version", CurrentSchemaVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Conflict("Observed state or desired generation changed.");
        }

        var spec = DeserializeSpec(reader.GetString(0), reader.GetInt32(1));
        return new ManagedDeployment(spec, status);
    }

    public async ValueTask<ManagedDeploymentLease?> TryAcquireAsync(
        string deploymentId,
        string owner,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseInput(deploymentId, owner, duration);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             INSERT INTO {_schema}.managed_deployment_leases (
                 deployment_id, owner, last_fencing_token, expires_at)
             VALUES (
                 @id, @owner, 1, pg_catalog.clock_timestamp() + @duration)
             ON CONFLICT (deployment_id) DO UPDATE
             SET owner = EXCLUDED.owner,
                 last_fencing_token =
                     {_schema}.managed_deployment_leases.last_fencing_token + 1,
                 expires_at = EXCLUDED.expires_at
             WHERE {_schema}.managed_deployment_leases.owner IS NULL
                OR {_schema}.managed_deployment_leases.expires_at <=
                   pg_catalog.clock_timestamp()
             RETURNING last_fencing_token, expires_at
             """;
        AddParameter(command, "id", deploymentId);
        AddParameter(command, "owner", owner);
        AddParameter(command, "duration", duration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ManagedDeploymentLease(
                deploymentId,
                owner,
                reader.GetInt64(0),
                ReadTimestamp(reader, 1))
            : null;
    }

    public async ValueTask<ManagedDeploymentLease> RenewAsync(
        ManagedDeploymentLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLeaseInput(lease.DeploymentId, lease.Owner, duration);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             UPDATE {_schema}.managed_deployment_leases
             SET expires_at = pg_catalog.clock_timestamp() + @duration
             WHERE deployment_id = @id
               AND owner = @owner
               AND last_fencing_token = @token
               AND expires_at > pg_catalog.clock_timestamp()
             RETURNING expires_at
             """;
        AddParameter(command, "id", lease.DeploymentId);
        AddParameter(command, "owner", lease.Owner);
        AddParameter(command, "token", lease.FencingToken);
        AddParameter(command, "duration", duration);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
        {
            throw new ManagedDeploymentLeaseException(
                "The managed deployment lease expired or was fenced.");
        }

        return lease with { ExpiresAt = ConvertTimestamp(value) };
    }

    public async ValueTask ReleaseAsync(
        ManagedDeploymentLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             UPDATE {_schema}.managed_deployment_leases
             SET owner = NULL, expires_at = NULL
             WHERE deployment_id = @id
               AND owner = @owner
               AND last_fencing_token = @token
             """;
        AddParameter(command, "id", lease.DeploymentId);
        AddParameter(command, "owner", lease.Owner);
        AddParameter(command, "token", lease.FencingToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ManagedDeploymentLeaseException(
                "The managed deployment lease was already fenced.");
        }
    }

    private async ValueTask<ManagedDeployment?> ReadForUpdateAsync(
        DbConnection connection,
        DbTransaction transaction,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            SelectColumns +
            $" FROM {_schema}.managed_deployments WHERE deployment_id = @id FOR UPDATE";
        AddParameter(command, "id", deploymentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadDeployment(reader)
            : null;
    }

    private static ManagedDeployment ReadDeployment(DbDataReader reader)
    {
        var spec = DeserializeSpec(reader.GetString(0), reader.GetInt32(1));
        if (!Enum.TryParse<ManagedDeploymentState>(reader.GetString(2), out var state) ||
            !Enum.IsDefined(state))
        {
            throw new InvalidOperationException("Stored managed deployment state is invalid.");
        }

        return new ManagedDeployment(
            spec,
            new ManagedDeploymentStatus(
                state,
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                ReadTimestamp(reader, 10)));
    }

    private static ManagedDeploymentSpec DeserializeSpec(string document, int format)
    {
        if (format != CurrentDocumentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Managed deployment document format {format} is not supported.");
        }

        var spec = JsonSerializer.Deserialize<ManagedDeploymentSpec>(document, JsonOptions)
            ?? throw new InvalidOperationException("Managed deployment document is empty.");
        ManagedDeploymentValidation.Validate(spec);
        return Clone(spec);
    }

    private static ManagedDeploymentSpec Clone(ManagedDeploymentSpec spec) =>
        spec with
        {
            Workloads = Array.AsReadOnly(
                spec.Workloads.Select(
                    workload => workload with
                    {
                        SecretReferences =
                            Array.AsReadOnly(workload.SecretReferences.ToArray()),
                        Settings = ManagedHostingCollections.Copy(workload.Settings),
                    }).ToArray()),
            Labels = ManagedHostingCollections.Copy(spec.Labels),
        };

    private static void AddSpecParameters(
        DbCommand command,
        ManagedDeploymentSpec spec,
        string document)
    {
        AddParameter(command, "id", spec.DeploymentId);
        AddParameter(command, "tenant", spec.TenantId);
        AddParameter(command, "provider", spec.Provider);
        AddParameter(command, "region", spec.Region);
        AddParameter(command, "generation", spec.Generation);
        AddParameter(command, "format", CurrentDocumentFormatVersion);
        AddParameter(command, "document", document);
    }

    private static void ValidateStatus(ManagedDeploymentStatus status, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (expectedRevision < 0 ||
            status.Revision != checked(expectedRevision + 1) ||
            status.ObservedGeneration <= 0 ||
            !Enum.IsDefined(status.State) ||
            status.FencingToken is <= 0)
        {
            throw new ArgumentException("Managed deployment status is invalid.", nameof(status));
        }

        ValidateOptional(status.DesiredFingerprint, 256, nameof(status.DesiredFingerprint));
        ValidateOptional(status.AppliedPlanFingerprint, 256, nameof(status.AppliedPlanFingerprint));
        ValidateOptional(status.ProviderResourceId, 2048, nameof(status.ProviderResourceId));
        ValidateOptional(status.DiagnosticCode, 128, nameof(status.DiagnosticCode));
    }

    private static void ValidateLeaseInput(
        string deploymentId,
        string owner,
        TimeSpan duration)
    {
        ValidateIdentifier(deploymentId, 128, nameof(deploymentId));
        ValidateIdentifier(owner, 512, nameof(owner));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
    }

    private static void ValidateIdentifier(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Value cannot exceed {maximumLength} characters.",
                parameterName);
        }
    }

    private static void ValidateOptional(string? value, int maximumLength, string parameterName)
    {
        if (value is not null && (value.Length == 0 || value.Length > maximumLength))
        {
            throw new ArgumentException("Optional value is invalid.", parameterName);
        }
    }

    private async ValueTask<int> GetSchemaVersionAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT schema_version FROM {_schema}.managed_hosting_metadata WHERE singleton";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? throw new InvalidOperationException("Managed-hosting schema is not initialized.")
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset ReadTimestamp(DbDataReader reader, int ordinal) =>
        ConvertTimestamp(reader.GetValue(ordinal));

    private static DateTimeOffset ConvertTimestamp(object value) =>
        value switch
        {
            DateTimeOffset offset => offset,
            DateTime dateTime => new DateTimeOffset(
                DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture),
        };

    private static ManagedDeploymentConcurrencyException Conflict(string message) =>
        new(message);
}
