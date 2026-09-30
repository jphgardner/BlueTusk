using System.Diagnostics.CodeAnalysis;
using BlueTusk.Streams;

namespace BlueTusk.Events.Streams;

/// <summary>Strict decoder for the append-only published-retention control relation.</summary>
internal sealed class EventPublishedRetentionChangeDecoder
{
    private readonly string _schema;
    private readonly EventOutboxChangeDecoder _bytes = new(maximumEventBytes: 32);

    internal EventPublishedRetentionChangeDecoder(string schema)
    {
        _schema = schema;
    }

    internal bool TryDecode(Change change, [NotNullWhen(true)] out PublishedRetentionControl? control)
    {
        control = null;
        var row = change switch
        {
            InsertChange insert => insert.NewRow,
            UpdateChange update when Matches(update.OldRow.Table) || Matches(update.NewRow.Table) =>
                throw new InvalidOperationException("Published retention control rows are append-only."),
            DeleteChange delete when Matches(delete.OldRow.Table) =>
                throw new InvalidOperationException("Published retention control rows cannot be deleted."),
            TruncateChange truncate when truncate.Tables.Any(Matches) =>
                throw new InvalidOperationException("Published retention control rows cannot be truncated."),
            _ => null
        };
        if (row is null || !Matches(row.Table)) { return false; }
        try
        {
            var epoch = EventOutboxChangeDecoder.Uuid(row, "retention_epoch");
            var stream = new EventStreamKey(EventOutboxChangeDecoder.Text(row, "tenant_id"),
                EventOutboxChangeDecoder.Text(row, "stream_id"));
            var first = EventOutboxChangeDecoder.Integer(row, "first_sequence", 8);
            var through = EventOutboxChangeDecoder.Integer(row, "through_sequence", 8);
            var digest = _bytes.Payload(row, "archive_manifest_sha256");
            var source = new EventPublishedSourceIdentity(
                EventOutboxChangeDecoder.Text(row, "source_system_identifier"),
                EventOutboxChangeDecoder.Text(row, "source_database"),
                EventOutboxChangeDecoder.Integer(row, "source_timeline", 8),
                EventOutboxChangeDecoder.Text(row, "source_slot"),
                EventOutboxChangeDecoder.Text(row, "source_publication"));
            var databaseOid = EventOutboxChangeDecoder.UnsignedInteger(row, "source_database_oid");
            var publicationOid = EventOutboxChangeDecoder.UnsignedInteger(row, "source_publication_oid");
            var revision = EventOutboxChangeDecoder.Integer(row, "membership_revision", 8);
            if (epoch == Guid.Empty || first <= 0 || through < first || digest.Length != 32 ||
                source.Timeline <= 0 || string.IsNullOrWhiteSpace(source.SystemIdentifier) ||
                string.IsNullOrWhiteSpace(source.DatabaseName) || string.IsNullOrWhiteSpace(source.SlotName) ||
                string.IsNullOrWhiteSpace(source.PublicationName) || source.SystemIdentifier.Length > 200 ||
                source.DatabaseName.Length > 200 || source.SlotName.Length > 63 || source.PublicationName.Length > 63 ||
                source.SystemIdentifier.Contains('\0') || source.DatabaseName.Contains('\0') ||
                source.SlotName.Contains('\0') || source.PublicationName.Contains('\0') ||
                databaseOid == 0 || publicationOid == 0 || revision <= 0)
            {
                throw new EventOutboxDecodingException("published retention control");
            }
            control = new PublishedRetentionControl(epoch, stream, first, through, digest, source,
                databaseOid, publicationOid, revision);
            return true;
        }
        catch (KeyNotFoundException)
        {
            throw new EventOutboxDecodingException("published retention required column missing");
        }
    }

    private bool Matches(ChangeTable table) =>
        string.Equals(table.Schema, _schema, StringComparison.Ordinal) &&
        string.Equals(table.Name, "published_retention_intents", StringComparison.Ordinal);
}
