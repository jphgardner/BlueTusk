using System.Data.Common;
using System.Text;

namespace BlueTusk.Streams.Storage.PostgreSql;

public interface IChangeRelayEnvelopeProtectionProvider
{
    string CurrentProtectorId { get; }

    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(string protectorId, ReadOnlySpan<byte> protectedData);
}

public sealed record PostgreSqlStreamsStorageOptions
{
    public required DbDataSource ControlDataSource { get; init; }

    public string ControlSchema { get; init; } = "bluetusk_streams";

    public long MaxRelayStorageBytes { get; init; } = 100L * 1024 * 1024 * 1024;

    public int MaxEnvelopeBytes { get; init; } = 256 * 1024 * 1024;

    public TimeSpan ResumeRetentionWindow { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan RemovedConsumerGroupRetentionWindow { get; init; } = TimeSpan.FromHours(1);

    public int MinimumRetainedTransactions { get; init; }

    public int RetentionDeleteBatchSize { get; init; } = 1_000;

    public int MaxCompactionBatches { get; init; } = 100;

    public TimeSpan MaxAcknowledgementAge { get; init; } = TimeSpan.FromMinutes(5);

    public long MaxWalLagBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    public IChangeRelayEnvelopeProtectionProvider? EnvelopeProtection { get; init; }

    internal string QuotedControlSchema => QuoteIdentifier(ControlSchema);

    internal void Validate()
    {
        BlueTuskGuard.NotNull(ControlDataSource);
        BlueTuskGuard.NotNullOrWhiteSpace(ControlSchema);
        BlueTuskGuard.NotNegativeOrZero(MaxRelayStorageBytes);
        BlueTuskGuard.NotNegativeOrZero(MaxEnvelopeBytes);
        BlueTuskGuard.NotLessThan(ResumeRetentionWindow, TimeSpan.Zero);
        BlueTuskGuard.NotLessThan(RemovedConsumerGroupRetentionWindow, TimeSpan.Zero);
        BlueTuskGuard.NotNegative(MinimumRetainedTransactions);
        BlueTuskGuard.NotNegativeOrZero(RetentionDeleteBatchSize);
        BlueTuskGuard.NotNegativeOrZero(MaxCompactionBatches);
        BlueTuskGuard.NotLessThanOrEqual(MaxAcknowledgementAge, TimeSpan.Zero);
        BlueTuskGuard.NotNegativeOrZero(MaxWalLagBytes);
        if (ControlSchema.Contains('\0') || Encoding.UTF8.GetByteCount(ControlSchema) > 63)
        {
            throw new ArgumentException(
                "The control schema must be a valid PostgreSQL identifier of at most 63 UTF-8 bytes.",
                nameof(ControlSchema));
        }

        if (EnvelopeProtection is not null)
        {
            BlueTuskGuard.NotNullOrWhiteSpace(EnvelopeProtection.CurrentProtectorId);
            if (Encoding.UTF8.GetByteCount(EnvelopeProtection.CurrentProtectorId) > 200)
            {
                throw new ArgumentException(
                    "The current relay envelope protector ID cannot exceed 200 UTF-8 bytes.",
                    nameof(EnvelopeProtection));
            }
        }
    }

    private static string QuoteIdentifier(string value) =>
        '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}

public sealed record PostgreSqlPublishedTable(string Schema, string Table)
{
    public PostgreSqlPublishedTable Validate()
    {
        BlueTuskGuard.NotNullOrWhiteSpace(Schema);
        BlueTuskGuard.NotNullOrWhiteSpace(Table);
        return this;
    }
}

public static class PostgreSqlRelayPublicationValidator
{
    public static void Validate(
        PostgreSqlStreamsStorageOptions options,
        IEnumerable<PostgreSqlPublishedTable> publishedTables)
    {
        BlueTuskGuard.NotNull(options);
        BlueTuskGuard.NotNull(publishedTables);
        options.Validate();
        foreach (var table in publishedTables)
        {
            BlueTuskGuard.NotNull(table);
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
