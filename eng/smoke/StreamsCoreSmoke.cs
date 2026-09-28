using BlueTusk.Spools.Smoke;
using BlueTusk.Streams;

var directory = Directory.CreateTempSubdirectory("bluetusk-spool-publish-").FullName;
try
{
    var table = new ChangeTable(1, "public", "smoke", 'd', [
        new ChangeColumn(0, "id", 23, -1, true),
        new ChangeColumn(1, "optional", 23, -1, false),
        new ChangeColumn(2, "label", 25, -1, false),
        new ChangeColumn(3, "state", 25, -1, false),
    ]);
    var row = new ChangeRow(table, [
        ChangeColumnValue.FromValue("17"u8, ChangeValueEncoding.Text),
        ChangeColumnValue.DatabaseNull,
        ChangeColumnValue.FromValue("mapped"u8, ChangeValueEncoding.Text),
        ChangeColumnValue.FromValue("Ready"u8, ChangeValueEncoding.Text),
    ]);
    var mapping = new ChangeEntityMappingBuilder<SmokeRow>().Build(table);
    var typed = mapping.MapRow(row);
    if (!typed.HasValue || typed.Value is not { Id: 17, Optional: null, Label: "mapped", State: SmokeState.Ready })
    {
        throw new InvalidOperationException("Convention mapping differs after publishing.");
    }
    var populatedRow = new ChangeRow(table, [
        ChangeColumnValue.FromValue("17"u8, ChangeValueEncoding.Text),
        ChangeColumnValue.FromValue("42"u8, ChangeValueEncoding.Text),
        ChangeColumnValue.FromValue("mapped"u8, ChangeValueEncoding.Text),
        ChangeColumnValue.FromValue("Ready"u8, ChangeValueEncoding.Text),
    ]);
    if (mapping.MapRow(populatedRow).Value?.Optional != 42)
    {
        throw new InvalidOperationException("Non-null nullable convention mapping differs after publishing.");
    }
    var explicitMapping = new ChangeEntityMappingBuilder<SmokeRow>().UseConventions(false)
        .Property(value => value.Id).Build(table);
    if (explicitMapping.MapRow(row).Value?.Id != 17) { throw new InvalidOperationException("Explicit mapping differs."); }
    var payload = new byte[4 * 1024 * 1024];
    for (var i = 0; i < payload.Length; i++) { payload[i] = unchecked((byte)i); }
    var spool = new FileTransactionSpool(new FileTransactionSpoolOptions
    {
        DirectoryPath = directory,
        MaxRecordBytes = payload.Length,
        MaxStorageBytes = payload.Length * 3L,
    });
    await using var writer = await spool.CreateAsync(new TransactionSpoolKey("publish-smoke", 1));
    await writer.AppendAsync("123456789"u8.ToArray());
    await writer.AppendAsync(payload);
    var reader = await writer.CompleteAsync();
    var count = 0;
    ReadOnlyMemory<byte> retained = default;
    await foreach (var record in reader.ReadRecordsAsync())
    {
        if (count++ == 0)
        {
            if (!record.Span.SequenceEqual("123456789"u8)) { throw new InvalidOperationException("Small replay differs."); }
        }
        else
        {
            if (!record.Span.SequenceEqual(payload)) { throw new InvalidOperationException("Large replay differs."); }
            retained = record;
        }
    }
    await reader.DisposeAsync();
    if (count != 2 || spool.ReservedBytes != 0 || Directory.EnumerateFiles(directory).Any() ||
        !retained.Span.SequenceEqual(payload)) { throw new InvalidOperationException("Spool lifecycle differs."); }
    Console.WriteLine("Streams consumer passed: convention/explicit mappings including nullable and enum values; small/4 MiB checksummed replay; retained mapped payload and released reservation.");
}
finally { Directory.Delete(directory, recursive: true); }

namespace BlueTusk.Spools.Smoke
{
    public enum SmokeState { Initial, Ready }
    public sealed class SmokeRow
    {
        public int Id { get; set; }
        public int? Optional { get; set; }
        public string? Label { get; set; }
        public SmokeState State { get; set; }
    }
}
