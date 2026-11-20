using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BlueTusk.Sync;
using BlueTusk.Sync.S3;
using Parquet.Serialization;

namespace BlueTusk.Benchmarks;

internal static class S3SyncCaptureVerification
{
    internal const string Bucket = "bluetusk-sync-capture";
    internal const string Prefix = "sync-capture";
    private const string ConfigurationKey = Prefix + "/_bluetusk/configuration.json";

    internal static AmazonS3Client Connect() => new(
        new BasicAWSCredentials(Required("BLUETUSK_BENCHMARK_S3_ACCESS_KEY"), Required("BLUETUSK_BENCHMARK_S3_SECRET_KEY")),
        new AmazonS3Config { ServiceURL = Required("BLUETUSK_BENCHMARK_S3_ENDPOINT"), ForcePathStyle = true, MaxErrorRetry = 0 });

    public static async Task CreateBucketAsync()
    {
        using var client = Connect();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, deadline.Token);
    }

    public static async Task RunAsync(int changes, string sourceTransactions)
    {
        var source = sourceTransactions.Split(',').Select(value =>
        {
            var fields = value.Split(':');
            return (Id: uint.Parse(fields[0], CultureInfo.InvariantCulture),
                Commit: DateTimeOffset.UnixEpoch.AddTicks(checked(long.Parse(fields[1], CultureInfo.InvariantCulture) * 10)));
        }).ToArray();
        if (changes is not (1 or 100 or 1_000) || source.Length == 0 ||
            source.Select(transaction => transaction.Id).Distinct().Count() != source.Length)
        {
            throw new InvalidDataException("The S3 verifier requires the complete ordered PostgreSQL transaction sequence.");
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(900, 45 + source.Length)));
        var token = deadline.Token;
        using var client = Connect();
        var listing = new ListObjectsV2Request
        {
            BucketName = Bucket,
            Prefix = Prefix + "/",
        };
        var keys = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await client.ListObjectsV2Async(listing, token);
            foreach (var item in page.S3Objects)
            {
                if (!keys.Add(item.Key)) throw new InvalidDataException("The recovered S3 object list contains a duplicate.");
            }
            if (page.IsTruncated is not true) break;
            if (string.IsNullOrWhiteSpace(page.NextContinuationToken) || page.NextContinuationToken == listing.ContinuationToken)
            {
                throw new InvalidDataException("The recovered S3 object list is incomplete.");
            }
            listing.ContinuationToken = page.NextContinuationToken;
        } while (true);

        var manifests = keys.Where(key => key.StartsWith(Prefix + "/commits/transactions/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        if (keys.Count != 1 + 2 * source.Length ||
            manifests.Length != source.Length || !keys.Contains(ConfigurationKey))
        {
            throw new InvalidDataException("S3 did not recover exactly one data object and commit manifest per source transaction.");
        }

        var configuration = JsonSerializer.Deserialize<S3SyncConfiguration>(
            await ReadImmutableAsync(client, ConfigurationKey, token)) ??
            throw new InvalidDataException("The recovered S3 pipeline configuration is empty.");
        var transform = SyncTransformVersion.Create("sync-capture", "1");
        if (configuration.PipelineId != SyncCaptureSink.PipelineId ||
            configuration.TransformFingerprint != transform.Fingerprint ||
            string.IsNullOrWhiteSpace(configuration.SourceFingerprint))
        {
            throw new InvalidDataException("S3 recovered the wrong pipeline configuration.");
        }

        var deliveryIds = new HashSet<string>(StringComparer.Ordinal);
        ulong previousPosition = 0;
        for (var batch = 0; batch < manifests.Length; batch++)
        {
            using var manifest = JsonDocument.Parse(await ReadImmutableAsync(client, manifests[batch], token));
            var root = manifest.RootElement;
            var transaction = root.GetProperty("metadata");
            var expected = source[batch];
            var position = transaction.GetProperty("commitEndPosition").GetString()!;
            var lsn = ulong.Parse(position, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            var relativeKey = FormattableString.Invariant($"/transactions/{position}-{expected.Id:x8}");
            var dataKey = Prefix + "/data" + relativeKey + ".parquet";
            var deliveryId = root.GetProperty("deliveryId").GetString()!;
            if (root.GetProperty("formatVersion").GetInt32() != 1 || root.GetProperty("event").GetString() != "transaction" ||
                root.GetProperty("pipelineId").GetString() != configuration.PipelineId ||
                root.GetProperty("sourceFingerprint").GetString() != configuration.SourceFingerprint ||
                root.GetProperty("transformName").GetString() != transform.Name ||
                root.GetProperty("transformFingerprint").GetString() != transform.Fingerprint ||
                transaction.GetProperty("outcome").GetString() != "Committed" ||
                transaction.GetProperty("TransactionId").GetUInt32() != expected.Id ||
                transaction.GetProperty("CommitTimestamp").GetDateTimeOffset() != expected.Commit ||
                position != lsn.ToString("x16", CultureInfo.InvariantCulture) || lsn <= previousPosition ||
                !deliveryId.StartsWith("bt1-", StringComparison.Ordinal) || !deliveryIds.Add(deliveryId) ||
                manifests[batch] != Prefix + "/commits" + relativeKey + ".json" ||
                root.GetProperty("dataKey").GetString() != dataKey || !keys.Contains(dataKey) ||
                root.GetProperty("rowCount").GetInt32() != changes)
            {
                throw new InvalidDataException("A recovered S3 commit marker differs from its source transaction.");
            }

            var bytes = await ReadImmutableAsync(client, dataKey, token);
            if (root.GetProperty("dataSha256").GetString() != Hash(bytes))
            {
                throw new InvalidDataException("A recovered Parquet file differs from the bytes recorded by its commit marker.");
            }
            using var stream = new MemoryStream(bytes, writable: false);
            var read = await ParquetSerializer.DeserializeAsync<S3SyncParquetCodec.S3SyncParquetRow>(stream, cancellationToken: token);
            if (read.Data.Count != changes || read.CustomMetadata["bluetusk.format-version"] != "1" ||
                read.CustomMetadata["bluetusk.delivery-id"] != deliveryId || read.CustomMetadata["bluetusk.event"] != "transaction" ||
                read.CustomMetadata["bluetusk.pipeline"] != configuration.PipelineId ||
                read.CustomMetadata["bluetusk.transform"] != configuration.TransformFingerprint)
            {
                throw new InvalidDataException("A recovered Parquet file has unexpected rows or transaction metadata.");
            }

            for (var ordinal = 0; ordinal < changes; ordinal++)
            {
                var row = read.Data[ordinal];
                var key = (batch * (long)changes + ordinal + 1).ToString(CultureInfo.InvariantCulture);
                var stableId = FormattableString.Invariant($"{configuration.SourceFingerprint}:{position}:{expected.Id:x8}:{ordinal}");
                if (row.DeliveryId != deliveryId || row.Ordinal != ordinal || row.StableId != stableId ||
                    row.Kind != "Upsert" || row.Collection != SyncCaptureSink.Collection || row.Key != key ||
                    row.ContentType != "application/json" || row.PartitionKey is not null ||
                    Encoding.UTF8.GetString(row.Content) != $"{{\"id\":{key}}}")
                {
                    throw new InvalidDataException("S3 did not recover every ordered mutation in its original Parquet object.");
                }
            }
            previousPosition = lsn;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            transactions = manifests.Length,
            documents = checked(manifests.Length * changes),
            finalSourceTransactionId = source[^1].Id,
            committedPosition = previousPosition.ToString("x16", CultureInfo.InvariantCulture),
            recoveredFromS3 = true,
        }));
    }

    private static async Task<byte[]> ReadImmutableAsync(AmazonS3Client client, string key, CancellationToken cancellationToken)
    {
        using var response = await client.GetObjectAsync(Bucket, key, cancellationToken);
        using var stream = new MemoryStream();
        await response.ResponseStream.CopyToAsync(stream, cancellationToken);
        var bytes = stream.ToArray();
        if (response.Metadata["x-amz-meta-bluetusk-sha256"] != Hash(bytes))
        {
            throw new InvalidDataException("A recovered S3 object does not match its immutable metadata hash.");
        }
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ??
        throw new InvalidOperationException("The capture requires an isolated S3 fixture endpoint and credentials.");
}
