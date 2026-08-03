using System.Data.Common;
using System.Text;

namespace BlueTusk.Streams.Storage.PostgreSql;

public sealed record PostgreSqlStreamsStorageOptions
{
    public required DbDataSource ControlDataSource { get; init; }

    public string ControlSchema { get; init; } = "bluetusk_streams";

    public long MaxRelayStorageBytes { get; init; } = 100L * 1024 * 1024 * 1024;

    public int MaxEnvelopeBytes { get; init; } = 256 * 1024 * 1024;

    public TimeSpan ResumeRetentionWindow { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan MaxAcknowledgementAge { get; init; } = TimeSpan.FromMinutes(5);

    public long MaxWalLagBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    internal string QuotedControlSchema => QuoteIdentifier(ControlSchema);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(ControlDataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(ControlSchema);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRelayStorageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEnvelopeBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(ResumeRetentionWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxAcknowledgementAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxWalLagBytes);
        if (ControlSchema.Contains('\0') || Encoding.UTF8.GetByteCount(ControlSchema) > 63)
        {
            throw new ArgumentException(
                "The control schema must be a valid PostgreSQL identifier of at most 63 UTF-8 bytes.",
                nameof(ControlSchema));
        }
    }

    private static string QuoteIdentifier(string value) =>
        '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}

public sealed record PostgreSqlPublishedTable(string Schema, string Table)
{
    public PostgreSqlPublishedTable Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(Table);
        return this;
    }
}

public static class PostgreSqlRelayPublicationValidator
{
    public static void Validate(
        PostgreSqlStreamsStorageOptions options,
        IEnumerable<PostgreSqlPublishedTable> publishedTables)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(publishedTables);
        options.Validate();
        foreach (var table in publishedTables)
        {
            ArgumentNullException.ThrowIfNull(table);
            table.Validate();
            if (string.Equals(table.Schema, options.ControlSchema, StringComparison.Ordinal))
            {
                throw new PostgreSqlRelayPublicationException(
                    $"Publication table '{table.Schema}.{table.Table}' is inside the BlueTusk relay control schema. " +
                    "Use a separate control data source or exclude the complete control schema from the publication.");
            }
        }
    }
}

public sealed class PostgreSqlRelayPublicationException : Exception
{
    public PostgreSqlRelayPublicationException(string message)
        : base(message)
    {
    }
}
