using System.Data.Common;
using System.Globalization;
using System.Text;

namespace BlueTusk.Search.PgVector;

/// <summary>Schema-qualified pgvector storage and cosine distance. Embeddings must be finite, dimension-matched and nonzero.</summary>
public sealed class PgVectorSearchAdapter : IPostgreSqlSearchVectorAdapter
{
    private readonly string _quotedExtensionSchema;

    public PgVectorSearchAdapter(int dimensions, string extensionSchema = "public")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 16_000);
        _quotedExtensionSchema = Quote(extensionSchema);
        ExtensionSchema = extensionSchema;
        Dimensions = dimensions;
    }

    public string ExtensionSchema { get; }
    public int Dimensions { get; }
    public string StorageContract => string.Create(CultureInfo.InvariantCulture, $"pgvector:cosine:{ExtensionSchema}:{Dimensions}:v1");
    public string ColumnTypeSql => string.Create(CultureInfo.InvariantCulture, $"{_quotedExtensionSchema}.vector({Dimensions})");

    public string ParameterSql(string trustedParameterSql) => $"CAST({trustedParameterSql} AS {_quotedExtensionSchema}.vector)";

    public string DistanceSql(string trustedColumnSql, string trustedParameterSql) =>
        $"{trustedColumnSql} OPERATOR({_quotedExtensionSchema}.<=>) {ParameterSql(trustedParameterSql)}";

    public string Encode(ReadOnlySpan<float> vector)
    {
        if (vector.Length != Dimensions)
        {
            throw new ArgumentException("Embedding dimensions differ from the configured index.", nameof(vector));
        }

        var builder = new StringBuilder(vector.Length * 12 + 2);
        builder.Append('[');
        var nonzero = false;
        for (var i = 0; i < vector.Length; i++)
        {
            if (!float.IsFinite(vector[i]))
            {
                throw new ArgumentException("Embeddings require finite elements.", nameof(vector));
            }

            nonzero |= vector[i] != 0;
            if (i != 0)
            {
                builder.Append(',');
            }

            builder.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        if (!nonzero)
        {
            throw new ArgumentException("Cosine retrieval does not support zero-norm embeddings.", nameof(vector));
        }

        return builder.Append(']').ToString();
    }

    public async ValueTask ValidateInstallationAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT n.nspname,e.extversion FROM pg_extension e JOIN pg_namespace n ON n.oid=e.extnamespace WHERE e.extname='vector'";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !string.Equals(reader.GetString(0), ExtensionSchema, StringComparison.Ordinal) ||
            !Version.TryParse(reader.GetString(1), out var version) || version < new Version(0, 8, 0))
        {
            throw new InvalidOperationException("pgvector 0.8.0 or newer must be installed in the configured extension schema before initializing Search.");
        }
    }

    /// <summary>Builds an optional HNSW index during deployment. Retrieval stays exact unless the caller explicitly selects approximate search.</summary>
    public async ValueTask CreateHnswIndexAsync(DbDataSource source, string searchSchema, string indexName = "chunks_embedding_cosine", int connectionsPerLayer = 16, int constructionCandidates = 64, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Dimensions > 2000)
        {
            throw new InvalidOperationException("pgvector HNSW vector indexes support at most 2000 dimensions; use a different storage adapter for larger indexed embeddings.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(connectionsPerLayer, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(connectionsPerLayer, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(constructionCandidates, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(constructionCandidates, 1000);
        var schema = Quote(searchSchema);
        var name = Quote(indexName);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ValidateInstallationAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"CREATE INDEX IF NOT EXISTS {name} ON {schema}.chunks USING hnsw (embedding {_quotedExtensionSchema}.vector_cosine_ops) WITH (m={connectionsPerLayer},ef_construction={constructionCandidates})");
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Quote(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(value) > 63)
        {
            throw new ArgumentException("PostgreSQL identifiers must fit within 63 UTF-8 bytes and cannot contain NUL.", nameof(value));
        }

        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
