import com.fasterxml.jackson.databind.ObjectMapper;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;
import org.apache.parquet.conf.PlainParquetConfiguration;
import org.apache.parquet.example.data.simple.SimpleGroupFactory;
import org.apache.parquet.hadoop.example.ExampleParquetWriter;
import org.apache.parquet.hadoop.metadata.CompressionCodecName;
import org.apache.parquet.io.OutputFile;
import org.apache.parquet.io.PositionOutputStream;
import org.apache.parquet.io.api.Binary;
import org.apache.parquet.schema.MessageType;
import org.apache.parquet.schema.MessageTypeParser;
import software.amazon.awssdk.auth.credentials.AwsBasicCredentials;
import software.amazon.awssdk.auth.signer.AwsS3V4Signer;
import software.amazon.awssdk.auth.signer.params.AwsS3V4SignerParams;
import software.amazon.awssdk.http.SdkHttpFullRequest;
import software.amazon.awssdk.http.SdkHttpMethod;
import software.amazon.awssdk.regions.Region;

/** Writes Zstandard Parquet and an immutable commit manifest using the native S3 HTTP API. */
final class S3CaptureSink implements SyncCaptureReference {
    private static final String BUCKET = "/bluetusk-sync-capture/";
    private static final String PREFIX = "sync-capture";
    private static final String PIPELINE = "sync_capture";
    private static final String TRANSFORM = "sync-capture";
    private static final String COLLECTION = "streams_capture";
    private static final String CONTENT_HASH = "x-amz-meta-bluetusk-sha256";
    private static final String SOURCE_FINGERPRINT = digest(bytes(
        String.join("\n", "pinned-postgres-fixture", "bluetusk_benchmark", COLLECTION, COLLECTION)));
    private static final String TRANSFORM_FINGERPRINT = digest(identity(TRANSFORM, "1", ""));
    private static final MessageType SCHEMA = MessageTypeParser.parseMessageType("""
        message S3SyncParquetRow {
          required binary DeliveryId (STRING);
          required int32 Ordinal;
          required binary StableId (STRING);
          required binary Kind (STRING);
          required binary Collection (STRING);
          optional binary Key (STRING);
          optional binary ContentType (STRING);
          optional binary PartitionKey (STRING);
          required binary Content;
        }
        """);
    private static final ObjectMapper JSON = new ObjectMapper();

    private final HttpClient http = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(5)).build();
    private final AwsS3V4Signer signer = AwsS3V4Signer.create();
    private final AwsS3V4SignerParams signing = AwsS3V4SignerParams.builder()
        .awsCredentials(AwsBasicCredentials.create(secret("BLUETUSK_BENCHMARK_S3_ACCESS_KEY"), secret("BLUETUSK_BENCHMARK_S3_SECRET_KEY")))
        .signingName("s3").signingRegion(Region.US_EAST_1).enablePayloadSigning(true).enableChunkedEncoding(false).build();
    private final URI endpoint = URI.create("http://s3:9000");
    private final ArrayList<Long> pending = new ArrayList<>();
    private final ArrayList<String> staged = new ArrayList<>();
    private long currentTransaction;
    private int durableFragments;

    S3CaptureSink() throws IOException {
        var configuration = JSON.createObjectNode().put("PipelineId", PIPELINE)
            .put("SourceFingerprint", SOURCE_FINGERPRINT).put("TransformFingerprint", TRANSFORM_FINGERPRINT);
        put(PREFIX + "/_bluetusk/configuration.json", "application/json", JSON.writeValueAsBytes(configuration));
    }

    @Override
    public void deliver(long key, long transactionId, long lsn, long commitMicros, boolean last) throws IOException {
        if (pending.isEmpty() && staged.isEmpty()) currentTransaction = transactionId;
        if (currentTransaction != transactionId) throw new IOException("S3 received rows from an incomplete source transaction.");
        pending.add(key);
        if (!last) return;

        var keys = new ArrayList<Long>();
        for (String stage : staged) readFragment(stage, keys);
        keys.addAll(pending);
        var position = String.format(Locale.ROOT, "%016x", lsn);
        var xid = String.format(Locale.ROOT, "%08x", transactionId);
        var id = "bt1-" + digest(identity("transaction", PIPELINE, SOURCE_FINGERPRINT, position, xid));
        var relativeKey = "/transactions/" + position + "-" + xid;
        var manifestKey = PREFIX + "/commits" + relativeKey + ".json";
        expect(request(SdkHttpMethod.HEAD, manifestKey, null, null), 404, "lookup a new commit");

        var data = parquet(keys, position, xid, id);
        var dataKey = PREFIX + "/data" + relativeKey + ".parquet";
        put(dataKey, "application/vnd.apache.parquet", data);
        var manifest = JSON.createObjectNode().put("formatVersion", 1).put("event", "transaction")
            .put("deliveryId", id).put("pipelineId", PIPELINE).put("sourceFingerprint", SOURCE_FINGERPRINT)
            .put("transformName", TRANSFORM).put("transformFingerprint", TRANSFORM_FINGERPRINT)
            .put("dataKey", dataKey).put("dataSha256", digest(data)).put("rowCount", keys.size());
        var commit = Instant.ofEpochSecond(Math.floorDiv(commitMicros, 1_000_000), Math.floorMod(commitMicros, 1_000_000) * 1000);
        manifest.putObject("metadata").put("TransactionId", transactionId).put("commitEndPosition", position)
            .put("CommitTimestamp", commit.toString()).put("outcome", "Committed").putNull("GlobalTransactionId");
        put(manifestKey, "application/json", JSON.writeValueAsBytes(manifest));
        for (String stage : staged) expect(request(SdkHttpMethod.DELETE, stage, null, null), 204, "retire a committed fragment");
        staged.clear();
        pending.clear();
    }

    @Override
    public void beforeAcknowledge() throws IOException {
        if (pending.isEmpty()) return;
        var fragment = ByteBuffer.allocate((pending.size() + 1) * Long.BYTES).order(ByteOrder.LITTLE_ENDIAN);
        fragment.putLong(currentTransaction);
        pending.forEach(fragment::putLong);
        var stage = PREFIX + "/_bluetusk/stage/" + currentTransaction + "-" + pending.getFirst();
        put(stage, "application/octet-stream", fragment.array());
        staged.add(stage);
        pending.clear();
        durableFragments++;
    }

    @Override
    public int durableFragments() { return durableFragments; }

    @Override
    public String implementation() { return "native signed S3 REST and Apache Parquet 1.16.0"; }

    private void readFragment(String stage, List<Long> keys) throws IOException {
        var response = request(SdkHttpMethod.GET, stage, null, null);
        expect(response, 200, "reload a staged fragment");
        if (!response.headers().firstValue(CONTENT_HASH).orElse("").equals(digest(response.body()))) {
            throw new IOException("The recovered S3 fragment does not match its immutable hash.");
        }
        var fragment = ByteBuffer.wrap(response.body()).order(ByteOrder.LITTLE_ENDIAN);
        if (fragment.remaining() <= Long.BYTES || fragment.remaining() % Long.BYTES != 0 || fragment.getLong() != currentTransaction) {
            throw new IOException("The recovered S3 fragment belongs to another source transaction.");
        }
        while (fragment.hasRemaining()) keys.add(fragment.getLong());
    }

    private void put(String key, String contentType, byte[] payload) throws IOException {
        expect(request(SdkHttpMethod.PUT, key, contentType, payload), 200, "write an immutable object");
    }

    private HttpResponse<byte[]> request(SdkHttpMethod method, String key, String contentType, byte[] payload) throws IOException {
        URI uri = endpoint.resolve(BUCKET + key);
        var headers = SdkHttpFullRequest.builder().uri(uri).method(method);
        if (payload != null) {
            headers.contentStreamProvider(() -> new ByteArrayInputStream(payload)).putHeader("Content-Type", contentType)
                .putHeader("If-None-Match", "*").putHeader(CONTENT_HASH, digest(payload));
        }
        var signed = signer.sign(headers.build(), signing);
        var request = HttpRequest.newBuilder(uri).timeout(Duration.ofSeconds(20))
            .method(method.name(), payload == null ? HttpRequest.BodyPublishers.noBody() : HttpRequest.BodyPublishers.ofByteArray(payload));
        for (var header : signed.headers().entrySet()) {
            if (header.getKey().equalsIgnoreCase("host") || header.getKey().equalsIgnoreCase("content-length")) continue;
            for (String value : header.getValue()) request.header(header.getKey(), value);
        }
        try {
            return http.send(request.build(), HttpResponse.BodyHandlers.ofByteArray());
        } catch (InterruptedException failure) {
            Thread.currentThread().interrupt();
            throw new IOException("The native S3 request was interrupted.", failure);
        }
    }

    private static void expect(HttpResponse<byte[]> response, int status, String operation) throws IOException {
        if (response.statusCode() != status) {
            throw new IOException("The benchmark could not " + operation + " in S3 (HTTP " + response.statusCode() + ").");
        }
    }

    private static byte[] parquet(List<Long> keys, String position, String xid, String id) throws IOException {
        var output = new MemoryOutput();
        var metadata = Map.of("bluetusk.format-version", "1", "bluetusk.delivery-id", id, "bluetusk.event", "transaction",
            "bluetusk.pipeline", PIPELINE, "bluetusk.transform", TRANSFORM_FINGERPRINT);
        var groups = new SimpleGroupFactory(SCHEMA);
        try (var writer = ExampleParquetWriter.builder(output).withConf(new PlainParquetConfiguration()).withType(SCHEMA)
            .withCompressionCodec(CompressionCodecName.ZSTD).withExtraMetaData(metadata).build()) {
            for (int ordinal = 0; ordinal < keys.size(); ordinal++) {
                var key = Long.toString(keys.get(ordinal));
                writer.write(groups.newGroup().append("DeliveryId", id).append("Ordinal", ordinal)
                    .append("StableId", SOURCE_FINGERPRINT + ":" + position + ":" + xid + ":" + ordinal)
                    .append("Kind", "Upsert").append("Collection", COLLECTION).append("Key", key)
                    .append("ContentType", "application/json").append("Content", Binary.fromConstantByteArray(bytes("{\"id\":" + key + "}"))));
            }
        }
        return output.bytes.toByteArray();
    }

    private static final class MemoryOutput implements OutputFile {
        private final ByteArrayOutputStream bytes = new ByteArrayOutputStream();

        @Override
        public PositionOutputStream create(long blockSizeHint) {
            return new PositionOutputStream() {
                public long getPos() { return bytes.size(); }
                public void write(int value) { bytes.write(value); }
                public void write(byte[] source, int offset, int length) { bytes.write(source, offset, length); }
            };
        }

        @Override
        public PositionOutputStream createOrOverwrite(long blockSizeHint) {
            bytes.reset();
            return create(blockSizeHint);
        }

        @Override
        public boolean supportsBlockSize() { return false; }

        @Override
        public long defaultBlockSize() { return 0; }
    }

    private static byte[] identity(String... fields) {
        var output = new ByteArrayOutputStream();
        for (var field : fields) {
            var value = bytes(field);
            output.writeBytes(ByteBuffer.allocate(Integer.BYTES).order(ByteOrder.LITTLE_ENDIAN).putInt(value.length).array());
            output.writeBytes(value);
        }
        return output.toByteArray();
    }

    private static String secret(String variable) {
        return Objects.requireNonNull(System.getenv(variable), "The S3 fixture credentials must be provided.");
    }

    private static byte[] bytes(String value) { return value.getBytes(StandardCharsets.UTF_8); }

    private static String digest(byte[] value) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(value));
        } catch (NoSuchAlgorithmException failure) {
            throw new IllegalStateException("The reference JVM must support SHA-256.", failure);
        }
    }
}
