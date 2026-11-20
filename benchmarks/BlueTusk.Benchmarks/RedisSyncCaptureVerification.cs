using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTusk.Sync;
using BlueTusk.Sync.Redis;

namespace BlueTusk.Benchmarks;

internal static class RedisSyncCaptureVerification
{
    public static async Task RunAsync(int changes, string sourceTransactions)
    {
        var sourceIds = sourceTransactions.Split(',').Select(value => uint.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var rows = checked(changes * sourceIds.Length);
        if (changes is not (1 or 100 or 1_000) || rows < 1 || sourceIds.Distinct().Count() != sourceIds.Length)
        {
            throw new InvalidDataException("The Redis verifier requires the complete source transaction sequence.");
        }

        using var connection = await SyncCaptureSink.ConnectRedisAsync();
        var database = connection.GetDatabase();
        var root = "bluetusk:sync:{" + Fingerprint(SyncCaptureSink.PipelineId) + "}";
        var collection = Fingerprint(SyncCaptureSink.Collection);
        var documentKey = $"{root}:collection:{collection}";
        var sortedKey = $"{root}:collection-index:{collection}";
        var state = await database.HashGetAllAsync($"{root}:state");
        var fields = state.ToDictionary(entry => entry.Name.ToString(), entry => entry.Value.ToString(), StringComparer.Ordinal);
        if (!fields.TryGetValue("generation", out var generation) || generation != sourceIds.Length.ToString(CultureInfo.InvariantCulture) ||
            !fields.TryGetValue("transaction_id", out var xid) || xid != sourceIds[^1].ToString(CultureInfo.InvariantCulture) ||
            !fields.TryGetValue("checkpoint", out var checkpoint) || checkpoint.Length != 16 ||
            !ulong.TryParse(checkpoint, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var position) || position == 0 ||
            await database.KeyExistsAsync($"{root}:staged"))
        {
            throw new InvalidDataException("Redis did not recover the exact last source checkpoint or retained partial transactions.");
        }

        var registry = await database.SetMembersAsync($"{root}:collections");
        var documents = await database.HashGetAllAsync(documentKey);
        var index = await database.SortedSetRangeByRankWithScoresAsync(sortedKey);
        if (registry.Length != 2 || !registry.Any(key => key == documentKey) || !registry.Any(key => key == sortedKey) ||
            documents.Length != rows || index.Length != rows)
        {
            throw new InvalidDataException("Redis did not recover every document and its reconciliation index.");
        }

        var scores = index.ToDictionary(entry => entry.Element.ToString(), entry => entry.Score, StringComparer.Ordinal);
        foreach (var entry in documents)
        {
            var key = entry.Name.ToString();
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1 || id > rows ||
                key != id.ToString(CultureInfo.InvariantCulture) || !scores.TryGetValue(key, out var score) || score != SyncReconciler.GetKeyHash(key))
            {
                throw new InvalidDataException("Redis recovered a document key or sorted index that does not match the input.");
            }
            var document = RedisSyncDocumentReader.Decode((byte[])entry.Value!);
            var identity = document.StableSourceId.Split(':');
            if (document.ContentType != "application/json" || document.PartitionKey is not null ||
                Encoding.UTF8.GetString(document.Content.Span) != $"{{\"id\":{key}}}" || identity.Length != 4 ||
                !uint.TryParse(identity[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var recordedSourceId) ||
                recordedSourceId != sourceIds[(id - 1) / changes])
            {
                throw new InvalidDataException("Redis recovered a document whose payload or source identity differs from PostgreSQL.");
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            transactions = sourceIds.Length,
            documents = rows,
            finalSourceTransactionId = sourceIds[^1],
            recoveredFromAppendOnlyFile = true,
        }));
    }

    private static string Fingerprint(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
