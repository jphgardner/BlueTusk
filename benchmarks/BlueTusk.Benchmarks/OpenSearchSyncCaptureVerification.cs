using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTusk.Sync;

namespace BlueTusk.Benchmarks;

internal static class OpenSearchSyncCaptureVerification
{
    public static async Task RunAsync(int changes, string sourceTransactions, string role)
    {
        var source = sourceTransactions.Split(',').Select(value => uint.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var rows = checked(changes * source.Length);
        if (changes is not (1 or 100 or 1_000) || rows < 1 || source.Distinct().Count() != source.Length ||
            role is not ("candidate" or "reference"))
        {
            throw new InvalidDataException("The OpenSearch verifier requires the complete PostgreSQL transaction sequence.");
        }

        var pipeline = Fingerprint(SyncCaptureSink.PipelineId)[..24];
        var transform = SyncTransformVersion.Create("sync-capture", "1").Fingerprint;
        var generation = transform[..16];
        var prefix = SyncCaptureSink.OpenSearchPrefix;
        var collection = SyncCaptureSink.Collection;
        var documentIndex = $"{prefix}-p{pipeline}-c{Fingerprint(collection)[..24]}-g{generation}";
        var reconcileIndex = $"{documentIndex}-reconcile";
        var controlIndex = $"{prefix}-control";
        const string stageIndex = "sync-benchmark-stage";
        var indexes = new List<string> { documentIndex, reconcileIndex, controlIndex };
        if (role == "reference") indexes.Add(stageIndex);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        using var client = SyncCaptureSink.CreateOpenSearchClient();
        var settings = await client.GetFromJsonAsync<JsonElement>(
            $"{string.Join(',', indexes)}/_settings?include_defaults=true&flat_settings=true", token);
        foreach (var index in indexes)
        {
            var settingsEntry = settings.GetProperty(index);
            var effective = settingsEntry.GetProperty("settings");
            var durability = effective.TryGetProperty("index.translog.durability", out var configured)
                ? configured : settingsEntry.GetProperty("defaults").GetProperty("index.translog.durability");
            if (durability.GetString() != "request" || effective.GetProperty("index.number_of_shards").GetString() != "1" ||
                effective.GetProperty("index.number_of_replicas").GetString() != "0")
            {
                throw new InvalidDataException("Recovered OpenSearch indexes differ from the synchronous single-node fixture.");
            }
        }

        using (var refresh = await client.PostAsync($"{string.Join(',', indexes)}/_refresh", null, token))
        {
            refresh.EnsureSuccessStatusCode();
        }
        foreach (var index in new[] { documentIndex, reconcileIndex })
        {
            var count = await client.GetFromJsonAsync<JsonElement>($"{index}/_count", token);
            if (count.GetProperty("count").GetInt32() != rows)
            {
                throw new InvalidDataException($"OpenSearch did not recover exactly {rows} records from '{index}'.");
            }
        }
        if (role == "reference" && (await client.GetFromJsonAsync<JsonElement>($"{stageIndex}/_count", token)).GetProperty("count").GetInt32() != 0)
        {
            throw new InvalidDataException("OpenSearch retained unfinalized staged fragments.");
        }

        var positions = new long[source.Length];
        foreach (var batch in Enumerable.Range(1, rows).Chunk(256))
        {
            var requests = batch.SelectMany(id =>
            {
                var identity = Fingerprint(collection + "\n" + id.ToString(CultureInfo.InvariantCulture));
                return new[] { new { _index = documentIndex, _id = identity }, new { _index = reconcileIndex, _id = identity } };
            }).ToArray();
            using var response = await client.PostAsJsonAsync("_mget", new { docs = requests }, token);
            response.EnsureSuccessStatusCode();
            var records = (await response.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("docs");
            if (records.GetArrayLength() != requests.Length) throw new InvalidDataException("The OpenSearch recovery read returned incomplete results.");

            for (var offset = 0; offset < batch.Length; offset++)
            {
                var id = batch[offset];
                var key = id.ToString(CultureInfo.InvariantCulture);
                var document = records[2 * offset];
                var reconciliation = records[2 * offset + 1];
                var documentSource = GetSource(document, documentIndex, requests[2 * offset]._id);
                var sidecar = GetSource(reconciliation, reconcileIndex, requests[2 * offset + 1]._id);
                var version = document.GetProperty("_version").GetInt64();
                var transactionIndex = (id - 1) / changes;
                if (documentSource.GetProperty("id").GetInt32() != id || documentSource.EnumerateObject().Count() != 1 ||
                    reconciliation.GetProperty("_version").GetInt64() != version || version <= 0 ||
                    (positions[transactionIndex] != 0 && positions[transactionIndex] != version) ||
                    sidecar.GetProperty("recordType").GetString() != "document" || sidecar.GetProperty("formatVersion").GetInt32() != 2 ||
                    sidecar.GetProperty("pipelineHash").GetString() != pipeline || sidecar.GetProperty("generation").GetString() != generation ||
                    sidecar.GetProperty("collection").GetString() != collection || sidecar.GetProperty("key").GetString() != key ||
                    sidecar.GetProperty("keyHash").GetUInt32() != SyncReconciler.GetKeyHash(key) ||
                    sidecar.GetProperty("contentHash").GetString() != Fingerprint($"{{\"id\":{key}}}") ||
                    sidecar.GetProperty("contentType").GetString() != "application/json" ||
                    sidecar.GetProperty("partitionKey").ValueKind != JsonValueKind.Null)
                {
                    throw new InvalidDataException("A recovered OpenSearch document or reconciliation record differs from the committed input.");
                }
                positions[transactionIndex] = version;
            }
        }
        if (positions.Zip(positions.Skip(1)).Any(pair => pair.First >= pair.Second))
        {
            throw new InvalidDataException("Recovered OpenSearch versions do not preserve PostgreSQL commit order.");
        }

        var checkpointId = $"checkpoint-{pipeline}-{generation}";
        var checkpoint = await client.GetFromJsonAsync<JsonElement>($"{controlIndex}/_doc/{checkpointId}", token);
        var checkpointSource = GetSource(checkpoint, controlIndex, checkpointId);
        if (checkpoint.GetProperty("_version").GetInt64() != positions[^1] ||
            checkpointSource.GetProperty("recordType").GetString() != "checkpoint" || checkpointSource.GetProperty("formatVersion").GetInt32() != 2 ||
            checkpointSource.GetProperty("pipelineHash").GetString() != pipeline || checkpointSource.GetProperty("generation").GetString() != generation ||
            checkpointSource.GetProperty("transformFingerprint").GetString() != transform ||
            checkpointSource.GetProperty("position").GetInt64() != positions[^1] || checkpointSource.GetProperty("transactionId").GetUInt32() != source[^1])
        {
            throw new InvalidDataException("The recovered OpenSearch checkpoint does not match the last complete PostgreSQL transaction.");
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            transactions = source.Length,
            documents = rows,
            finalSourceTransactionId = source[^1],
            recoveredFromOpenSearch = true,
        }));
    }

    private static JsonElement GetSource(JsonElement record, string index, string id)
    {
        if (!record.GetProperty("found").GetBoolean() || record.GetProperty("_index").GetString() != index || record.GetProperty("_id").GetString() != id)
        {
            throw new InvalidDataException("An expected OpenSearch document was missing after restart.");
        }
        return record.GetProperty("_source");
    }

    private static string Fingerprint(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
