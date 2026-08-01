using System.Text;
using BlueTusk.Data;

namespace BlueTusk.Extensions.PgTrgm;

/// <summary>The complete scalar comparison result returned by pg_trgm.</summary>
public sealed record BlueTuskPgTrgmComparison(
    float Similarity,
    float WordSimilarity,
    float StrictWordSimilarity,
    bool IsSimilar,
    bool IsWordSimilar,
    bool IsStrictWordSimilar,
    string[] Trigrams);

public static class BlueTuskPgTrgmDataSourceExtensions
{
    /// <summary>
    /// Executes pg_trgm's similarity functions, threshold operators, and trigram extraction
    /// with strongly typed parameters.
    /// </summary>
    public static async ValueTask<BlueTuskPgTrgmComparison> ComparePgTrgmAsync(
        this BlueTuskDataSource dataSource,
        string query,
        string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(target);
        var feature = dataSource.Features.GetRequired<BlueTuskPgTrgmFeature>(
            BlueTuskPgTrgmFeature.RegistryName);
        var schema = DelimitIdentifier(feature.Schema);
        var sql = $"""
            SELECT
                {schema}."similarity"($1, $2),
                {schema}."word_similarity"($1, $2),
                {schema}."strict_word_similarity"($1, $2),
                $1 OPERATOR({schema}.%) $2,
                $1 OPERATOR({schema}.<%) $2,
                $1 OPERATOR({schema}.<<%) $2,
                {schema}."show_trgm"($1)
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new BlueTuskParameter<string>(query));
        command.Parameters.Add(new BlueTuskParameter<string>(target));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PostgreSQL pg_trgm comparison returned no row.");
        }

        return new BlueTuskPgTrgmComparison(
            reader.GetFieldValue<float>(0),
            reader.GetFieldValue<float>(1),
            reader.GetFieldValue<float>(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetFieldValue<string[]>(6));
    }

    private static string DelimitIdentifier(string identifier) =>
        new StringBuilder(identifier.Length + 2)
            .Append('"')
            .Append(identifier.Replace("\"", "\"\"", StringComparison.Ordinal))
            .Append('"')
            .ToString();
}
