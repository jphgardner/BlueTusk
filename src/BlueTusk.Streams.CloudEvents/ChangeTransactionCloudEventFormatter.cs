using System.Globalization;
using System.Text.Json;

namespace BlueTusk.Streams.CloudEvents;

public static class ChangeTransactionCloudEventFormat
{
    public const int CurrentVersion = 1;

    public const string EventType = "io.bluetusk.streams.transaction.v1";

    public const string DataContentType =
        "application/vnd.bluetusk.change-transaction+binary;version=1";
}

public sealed record ChangeTransactionCloudEventOptions
{
    public string EventType { get; init; } = ChangeTransactionCloudEventFormat.EventType;

    public string DataContentType { get; init; } = ChangeTransactionCloudEventFormat.DataContentType;

    public int MaximumEventBytes { get; init; } = 384 * 1024 * 1024;

    public ChangeTransactionEnvelopeOptions Envelope { get; init; } = new();

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(EventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(DataContentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEventBytes);
        ArgumentNullException.ThrowIfNull(Envelope);
    }
}

public sealed record ChangeTransactionCloudEventMetadata(
    string SpecVersion,
    string Id,
    Uri Source,
    string Type,
    string Subject,
    DateTimeOffset Time,
    string DataContentType,
    string CommitEndPosition,
    uint TransactionId,
    int ChangeCount,
    int EnvelopeFormatVersion);

public sealed class ChangeTransactionCloudEventFormatter
{
    private readonly ChangeTransactionCloudEventOptions _options;

    public ChangeTransactionCloudEventFormatter(ChangeTransactionCloudEventOptions? options = null)
    {
        _options = options ?? new ChangeTransactionCloudEventOptions();
        _options.Validate();
    }

    public ChangeTransactionCloudEventMetadata Describe(ChangeTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var position = transaction.CommitEndPosition.Value.ToString("X16", CultureInfo.InvariantCulture);
        return new ChangeTransactionCloudEventMetadata(
            "1.0",
            $"{transaction.Source.Fingerprint}:{position}:{transaction.TransactionId}",
            new Uri($"urn:bluetusk:postgresql:{transaction.Source.Fingerprint}"),
            _options.EventType,
            $"slot/{Uri.EscapeDataString(transaction.Source.SlotName)}/transaction/{transaction.TransactionId}",
            transaction.CommitTimestamp,
            _options.DataContentType,
            transaction.CommitEndPosition.ToString(),
            transaction.TransactionId,
            transaction.Changes.Count,
            ChangeTransactionEnvelope.CurrentFormatVersion);
    }

    public async ValueTask WriteStructuredAsync(
        ChangeTransaction transaction,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The CloudEvents destination stream must be writable.", nameof(destination));
        }

        var metadata = Describe(transaction);
        var envelope = await ChangeTransactionEnvelopeCodec.EncodeAsync(
            transaction,
            _options.Envelope,
            cancellationToken).ConfigureAwait(false);
        var estimatedBytes = checked(((long)envelope.Data.Length + 2) / 3 * 4 + 2_048);
        if (estimatedBytes > _options.MaximumEventBytes)
        {
            throw new InvalidOperationException(
                $"The structured CloudEvent requires approximately {estimatedBytes} bytes; " +
                $"the configured maximum is {_options.MaximumEventBytes} bytes.");
        }

        using var writer = new Utf8JsonWriter(
            destination,
            new JsonWriterOptions { Indented = false, SkipValidation = false });
        writer.WriteStartObject();
        writer.WriteString("specversion", metadata.SpecVersion);
        writer.WriteString("id", metadata.Id);
        writer.WriteString("source", metadata.Source.OriginalString);
        writer.WriteString("type", metadata.Type);
        writer.WriteString("subject", metadata.Subject);
        writer.WriteString("time", metadata.Time);
        writer.WriteString("datacontenttype", metadata.DataContentType);
        writer.WriteString("bluetusklsn", metadata.CommitEndPosition);
        writer.WriteNumber("bluetuskxid", metadata.TransactionId);
        writer.WriteNumber("bluetuskchanges", metadata.ChangeCount);
        writer.WriteNumber("bluetuskformat", metadata.EnvelopeFormatVersion);
        writer.WriteBase64String("data_base64", envelope.Data.Span);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ToStructuredJsonAsync(
        ChangeTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        await WriteStructuredAsync(transaction, stream, cancellationToken).ConfigureAwait(false);
        return stream.ToArray();
    }
}
