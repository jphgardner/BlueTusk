using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.Sync.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync.Webhooks.Tests;

public sealed class WebhookSyncDestinationTests
{
    private static readonly ChangeSourceIdentity Source =
        new("webhook-system", "webhook-database", "webhook-slot", "public:orders");

    private static readonly byte[] SigningKey = Enumerable.Range(1, 32)
        .Select(static value => (byte)value)
        .ToArray();

    [Fact]
    public async Task Whole_transactions_are_signed_and_receiver_deduplication_is_authoritative()
    {
        using var receiver = new FakeReceiverHandler(SigningKey);
        using var client = new HttpClient(receiver);
        var destination = CreateDestination(client);
        var transform = SyncTransformVersion.Create("orders", "v1");
        Assert.Equal(
            SyncProvisionStatus.Ready,
            (await destination.ProvisionAsync(new SyncProvisionRequest(
                "orders",
                Source,
                transform))).Status);

        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            42,
            new BlueTuskLogSequenceNumber(105));
        var changeId = new ChangeId(Source, new BlueTuskLogSequenceNumber(105), 42, 0);
        var batch = new SyncTransactionBatch(
            "orders",
            transform,
            delivery.Transaction,
            [new SyncMutation(
                changeId,
                SyncMutationKind.Upsert,
                "orders",
                "42",
                "{\"state\":\"Created\"}"u8.ToArray(),
                "application/json")]);
        var changedRedelivery = new SyncTransactionBatch(
            "orders",
            transform,
            delivery.Transaction,
            [new SyncMutation(
                changeId,
                SyncMutationKind.Upsert,
                "orders",
                "42",
                "{\"state\":\"Wrong\"}"u8.ToArray(),
                "application/json")]);

        var applied = await destination.ApplyTransactionAsync(batch);
        var duplicate = await destination.ApplyTransactionAsync(changedRedelivery);

        Assert.Equal(SyncApplyStatus.Applied, applied.Status);
        Assert.Equal(SyncApplyStatus.AlreadyApplied, duplicate.Status);
        Assert.Equal(new BlueTuskLogSequenceNumber(105), duplicate.DurablePosition);
        Assert.Equal(2, receiver.Requests.Count(request => request.Event == "transaction"));
        var requests = receiver.Requests.Where(request => request.Event == "transaction").ToArray();
        Assert.Equal(requests[0].DeliveryId, requests[1].DeliveryId);
        Assert.All(requests, request => Assert.True(request.SignatureValid));
        using var document = JsonDocument.Parse(requests[0].Body);
        Assert.Equal("transaction", document.RootElement.GetProperty("event").GetString());
        Assert.Single(document.RootElement.GetProperty("mutations").EnumerateArray());

        var changedTransform = SyncTransformVersion.Create("orders", "v2");
        var replacement = CreateDestination(client);
        var mismatch = await replacement.ProvisionAsync(
            new SyncProvisionRequest("orders", Source, changedTransform));
        Assert.Equal(SyncProvisionStatus.RebuildRequired, mismatch.Status);
        Assert.Equal(transform.Fingerprint, mismatch.ExistingTransformFingerprint);
    }

    [Fact]
    public async Task Transient_failures_retry_but_ambiguous_success_never_advances_progress()
    {
        using var receiver = new FakeReceiverHandler(SigningKey)
        {
            TransientTransactionFailures = 2,
        };
        using var client = new HttpClient(receiver);
        var destination = CreateDestination(client, maximumAttempts: 3);
        var transform = SyncTransformVersion.Create("orders", "v1");
        _ = await destination.ProvisionAsync(new SyncProvisionRequest("orders", Source, transform));
        var batch = await CreateBatchAsync(transform);

        Assert.Equal(SyncApplyStatus.Applied, (await destination.ApplyTransactionAsync(batch)).Status);
        Assert.Equal(3, receiver.TransactionAttempts);

        using var ambiguousReceiver = new FakeReceiverHandler(SigningKey)
        {
            OmitTransactionAcknowledgement = true,
        };
        using var ambiguousClient = new HttpClient(ambiguousReceiver);
        var ambiguous = CreateDestination(ambiguousClient, maximumAttempts: 1);
        _ = await ambiguous.ProvisionAsync(new SyncProvisionRequest("orders", Source, transform));
        var exception = await Assert.ThrowsAsync<WebhookSyncProtocolException>(async () =>
            await ambiguous.ApplyTransactionAsync(batch));
        Assert.Contains("ambiguous success", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_passes_shared_destination_conformance()
    {
        using var receiver = new FakeReceiverHandler(SigningKey);
        using var client = new HttpClient(receiver);
        var result = await SyncDestinationConformanceSuite.VerifyAsync(
            new WebhookConformanceHarness(client, receiver));

        Assert.Equal("Signed webhook", result.DestinationName);
        Assert.False(result.QuarantineVerified);
        Assert.True(result.Capabilities.HasFlag(SyncDestinationCapabilities.TransactionalBatches));
    }

    [Fact]
    public void Http_and_short_signing_keys_are_rejected_by_default()
    {
        using var client = new HttpClient(new FakeReceiverHandler(SigningKey));
        Assert.Throws<ArgumentException>(() => new WebhookSyncDestination(new WebhookSyncOptions
        {
            Client = client,
            Endpoint = new Uri("http://receiver.test/sync"),
            KeyId = "key-1",
            SigningKey = SigningKey,
        }));
        Assert.Throws<ArgumentException>(() => new WebhookSyncDestination(new WebhookSyncOptions
        {
            Client = client,
            Endpoint = new Uri("https://receiver.test/sync"),
            KeyId = "key-1",
            SigningKey = new byte[16],
        }));
    }

    private static WebhookSyncDestination CreateDestination(
        HttpClient client,
        int maximumAttempts = 1) =>
        new(new WebhookSyncOptions
        {
            Client = client,
            Endpoint = new Uri("http://receiver.test/sync"),
            KeyId = "key-1",
            SigningKey = SigningKey,
            AllowInsecureHttp = true,
            MaximumAttempts = maximumAttempts,
            InitialRetryDelay = TimeSpan.Zero,
            MaximumRetryDelay = TimeSpan.Zero,
        });

    private static async ValueTask<SyncTransactionBatch> CreateBatchAsync(
        SyncTransformVersion transform)
    {
        var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            Source,
            43,
            new BlueTuskLogSequenceNumber(106));
        await using (delivery.ConfigureAwait(false))
        {
            return new SyncTransactionBatch(
                "orders",
                transform,
                delivery.Transaction,
                [new SyncMutation(
                    new ChangeId(Source, new BlueTuskLogSequenceNumber(106), 43, 0),
                    SyncMutationKind.Upsert,
                    "orders",
                    "43",
                    "{}"u8.ToArray(),
                    "application/json")]);
        }
    }

    private sealed class WebhookConformanceHarness(
        HttpClient client,
        FakeReceiverHandler receiver) : ISyncDestinationConformanceHarness
    {
        public string PipelineId => "conformance";

        public ChangeSourceIdentity Source { get; } =
            new("conformance-system", "conformance-database", "conformance-slot", "public:conformance");

        public ValueTask<ISyncDestination> CreateDestinationAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISyncDestination>(new WebhookSyncDestination(
                new WebhookSyncOptions
                {
                    Client = client,
                    Endpoint = new Uri("http://receiver.test/sync"),
                    KeyId = "key-1",
                    SigningKey = SigningKey,
                    AllowInsecureHttp = true,
                    MaximumAttempts = 1,
                }));
        }

        public ValueTask VerifyDurableStateAsync(
            SyncDestinationConformanceStage stage,
            ISyncDestination destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsType<WebhookSyncDestination>(destination);
            var expected = stage is SyncDestinationConformanceStage.SnapshotApplied or
                SyncDestinationConformanceStage.SnapshotRestart
                ? 4
                : 5;
            Assert.Equal(expected, receiver.UniqueNonProvisionDeliveries);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeReceiverHandler(byte[] signingKey) : HttpMessageHandler
    {
        private readonly HashSet<string> _deliveries = new(StringComparer.Ordinal);
        private string? _transformFingerprint;

        internal List<CapturedRequest> Requests { get; } = [];

        internal int TransientTransactionFailures { get; init; }

        internal bool OmitTransactionAcknowledgement { get; init; }

        internal int TransactionAttempts { get; private set; }

        internal int UniqueNonProvisionDeliveries { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var eventName = Header(request, WebhookSyncProtocol.EventHeader);
            var deliveryId = Header(request, WebhookSyncProtocol.DeliveryIdHeader);
            var timestamp = Header(request, WebhookSyncProtocol.TimestampHeader);
            var signature = Header(request, WebhookSyncProtocol.SignatureHeader);
            var valid = VerifySignature(signingKey, timestamp, body, signature);
            Requests.Add(new CapturedRequest(eventName, deliveryId, body, valid));
            if (!valid)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (eventName == "transaction")
            {
                TransactionAttempts++;
                if (TransactionAttempts <= TransientTransactionFailures)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
            }

            using var document = JsonDocument.Parse(body);
            var requestedTransform = document.RootElement.GetProperty("transform")
                .GetProperty("fingerprint")
                .GetString()!;
            if (eventName == "provision" && _transformFingerprint is null)
            {
                _transformFingerprint = requestedTransform;
            }

            var duplicate = !_deliveries.Add(deliveryId);
            if (!duplicate && eventName != "provision")
            {
                UniqueNonProvisionDeliveries++;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (!(eventName == "transaction" && OmitTransactionAcknowledgement))
            {
                response.Headers.TryAddWithoutValidation(
                    WebhookSyncProtocol.DeliveryStatusHeader,
                    duplicate
                        ? WebhookSyncProtocol.DuplicateStatus
                        : WebhookSyncProtocol.AppliedStatus);
            }

            if (eventName == "provision")
            {
                response.Headers.TryAddWithoutValidation(
                    WebhookSyncProtocol.TransformFingerprintHeader,
                    _transformFingerprint);
            }

            return response;
        }

        private static string Header(HttpRequestMessage request, string name) =>
            request.Headers.GetValues(name).Single();

        private static bool VerifySignature(
            ReadOnlySpan<byte> key,
            string timestamp,
            ReadOnlySpan<byte> body,
            string signature)
        {
            using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hash.AppendData(Encoding.ASCII.GetBytes(timestamp));
            hash.AppendData("."u8);
            hash.AppendData(body);
            var expected = "v1=" + Convert.ToHexStringLower(hash.GetHashAndReset());
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(signature));
        }
    }

    private sealed record CapturedRequest(
        string Event,
        string DeliveryId,
        byte[] Body,
        bool SignatureValid);
}
