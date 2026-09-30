using System.Text;

namespace BlueTusk.Streams.Tests;

public sealed class TransactionSpoolCompatibilityTests
{
    private static readonly int[] RecordLengths = [0, 1, 7, 8, 9, 15, 16, 17, 255, 256, 257, 65536, 4194304];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writer_preserves_every_byte_of_the_legacy_ieee_crc32_format(bool protectedRecords)
    {
        var directory = Directory.CreateTempSubdirectory("bluetusk-spool-format-").FullName;
        try
        {
            var protector = protectedRecords ? new TestProtector() : null;
            var spool = new FileTransactionSpool(new FileTransactionSpoolOptions
            {
                DirectoryPath = directory,
                MaxStorageBytes = 16 * 1024 * 1024,
                MaxRecordBytes = 8 * 1024 * 1024,
                Protector = protector,
            });
            var key = new TransactionSpoolKey("source-Δ", 913);
            var records = RecordLengths.Select(CreatePayload).ToArray();
            await using var writer = await spool.CreateAsync(key);
            foreach (var payload in records)
            {
                // Offset memory exercises both nonzero origins and vector tails.
                var backing = new byte[payload.Length + 31];
                payload.CopyTo(backing, 17);
                await writer.AppendAsync(backing.AsMemory(17, payload.Length));
            }
            await using var reader = await writer.CompleteAsync();
            var actual = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(directory, "*.ready")));
            var encodedRecords = records.Select(record => protector?.Protect(record) ?? record).ToArray();
            Assert.Equal(BuildLegacyFile(key, protector?.Id ?? "none", encodedRecords), actual);
            AssertLegacyRecords(actual, records.Length, protector?.Id ?? "none");
            var index = 0;
            await foreach (var record in reader.ReadRecordsAsync())
            {
                Assert.True(records[index++].AsSpan().SequenceEqual(record.Span));
            }
            Assert.Equal(records.Length, index);
            await reader.DisposeAsync();
            Assert.Equal(0, spool.ReservedBytes);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static byte[] CreatePayload(int length)
    {
        var result = new byte[length];
        for (var i = 0; i < result.Length; i++) { result[i] = unchecked((byte)(i * 31 + length)); }
        return result;
    }

    internal static void AssertLegacyRecords(byte[] bytes, int expectedCount, string protectorId)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        Assert.Equal(0x50535442u, reader.ReadUInt32());
        Assert.Equal(1, reader.ReadInt32());
        _ = reader.ReadUInt32();
        _ = reader.ReadBytes(reader.ReadInt32());
        Assert.Equal(protectorId, Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())));
        for (var i = 0; i < expectedCount; i++)
        {
            var length = reader.ReadInt32();
            var crc = reader.ReadUInt32();
            Assert.InRange(length, 0, 8 * 1024 * 1024);
            var payload = reader.ReadBytes(length);
            Assert.Equal(length, payload.Length);
            Assert.Equal(ReferenceCrc32(payload), crc);
        }
        Assert.Equal(-1, reader.ReadInt32());
        Assert.Equal(expectedCount, reader.ReadInt32());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
    }

    private static byte[] BuildLegacyFile(TransactionSpoolKey key, string protectorId, byte[][] records)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x50535442u);
        writer.Write(1);
        writer.Write(key.TransactionId);
        var source = Encoding.UTF8.GetBytes(key.SourceFingerprint);
        writer.Write(source.Length);
        writer.Write(source);
        var protector = Encoding.UTF8.GetBytes(protectorId);
        writer.Write(protector.Length);
        writer.Write(protector);
        foreach (var record in records)
        {
            writer.Write(record.Length);
            writer.Write(ReferenceCrc32(record));
            writer.Write(record);
        }
        writer.Write(-1);
        writer.Write(records.Length);
        writer.Flush();
        return stream.ToArray();
    }

    // Deliberately independent bitwise reference for the preexisting IEEE
    // polynomial, initialization and complement; not the optimized library.
    private static uint ReferenceCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
        }
        return ~crc;
    }

    internal sealed class TestProtector : ITransactionSpoolProtector
    {
        public string Id => "test-xor-not-encryption";
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            var result = plaintext.ToArray();
            for (var i = 0; i < result.Length; i++) { result[i] ^= 0xa5; }
            return result;
        }
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => Protect(protectedData);
    }
}
