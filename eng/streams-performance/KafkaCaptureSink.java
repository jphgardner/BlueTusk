import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ObjectNode;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
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
import java.util.concurrent.ExecutionException;
import java.util.concurrent.TimeUnit;
import org.apache.kafka.clients.admin.Admin;
import org.apache.kafka.clients.admin.NewTopic;
import org.apache.kafka.clients.consumer.KafkaConsumer;
import org.apache.kafka.clients.producer.KafkaProducer;
import org.apache.kafka.clients.producer.ProducerRecord;
import org.apache.kafka.common.KafkaException;
import org.apache.kafka.common.TopicPartition;
import org.apache.kafka.common.serialization.ByteArrayDeserializer;
import org.apache.kafka.common.serialization.ByteArraySerializer;
import org.apache.kafka.common.serialization.StringDeserializer;
import org.apache.kafka.common.serialization.StringSerializer;

/** Uses a native Kafka transaction to publish each complete Debezium source transaction and its checkpoint. */
final class KafkaCaptureSink implements SyncCaptureReference {
    private static final String EVENTS = "sync.capture.events";
    private static final String STATE = "sync.capture.state";
    private static final String STAGE = "sync.capture.stage";
    private static final String PIPELINE = "sync_capture";
    private static final String TRANSFORM = "sync-capture";
    private static final String COLLECTION = "streams_capture";
    private static final List<String> SOURCE = List.of("pinned-postgres-fixture", "bluetusk_benchmark", COLLECTION, COLLECTION);
    private static final String SERVER = "kafka:9092";
    private static final TopicPartition STAGE_PARTITION = new TopicPartition(STAGE, 0);
    private static final ObjectMapper JSON = new ObjectMapper();
    private static final String SOURCE_FINGERPRINT = digest(bytes(String.join("\n", SOURCE)));
    private static final String TRANSFORM_FINGERPRINT = digest(identity(TRANSFORM, "1", ""));

    private final KafkaProducer<String, byte[]> producer;
    private final KafkaConsumer<String, byte[]> fragments;
    private final ArrayList<Long> pending = new ArrayList<>();
    private final ArrayList<Long> staged = new ArrayList<>();
    private long currentTransaction;
    private int durableFragments;

    KafkaCaptureSink() throws IOException, InterruptedException {
        try (var admin = Admin.create(Map.of("bootstrap.servers", SERVER))) {
            admin.createTopics(List.of(new NewTopic(EVENTS, 1, (short)1),
                new NewTopic(STATE, 1, (short)1).configs(Map.of("cleanup.policy", "compact")),
                new NewTopic(STAGE, 1, (short)1))).all().get(45, TimeUnit.SECONDS);
        } catch (ExecutionException | java.util.concurrent.TimeoutException failure) {
            throw new IOException("Could not provision the isolated Kafka capture topics.", failure);
        }
        producer = new KafkaProducer<>(Map.of("bootstrap.servers", SERVER, "transactional.id", PIPELINE,
            "max.block.ms", 30_000, "transaction.timeout.ms", 30_000, "linger.ms", 0),
            new StringSerializer(), new ByteArraySerializer());
        producer.initTransactions();
        fragments = new KafkaConsumer<>(Map.of("bootstrap.servers", SERVER, "isolation.level", "read_committed",
            "enable.auto.commit", false, "fetch.max.wait.ms", 25), new StringDeserializer(), new ByteArrayDeserializer());
        fragments.assign(List.of(STAGE_PARTITION));
        var configuration = JSON.createObjectNode().put("formatVersion", 1).put("pipelineId", PIPELINE)
            .put("sourceFingerprint", SOURCE_FINGERPRINT).put("transformFingerprint", TRANSFORM_FINGERPRINT);
        publish(new ProducerRecord<>(STATE, "configuration", JSON.writeValueAsBytes(configuration)));
    }

    public void deliver(long key, long transactionId, long lsn, long commitMicros, boolean last) throws IOException {
        if (pending.isEmpty() && staged.isEmpty()) currentTransaction = transactionId;
        if (currentTransaction != transactionId) throw new IOException("Kafka received rows from an incomplete source transaction.");
        pending.add(key);
        if (!last) return;

        var keys = new ArrayList<Long>();
        for (long offset : staged) readFragment(offset, keys);
        keys.addAll(pending);
        var position = String.format(Locale.ROOT, "%016x", lsn);
        var xid = String.format(Locale.ROOT, "%08x", transactionId);
        var id = "bt1-" + digest(identity("transaction", PIPELINE, SOURCE_FINGERPRINT, position, xid));
        var data = new ProducerRecord<>(EVENTS, id, envelope(keys, id, transactionId, position, xid, commitMicros));
        data.headers().add("bluetusk.event", bytes("transaction"))
            .add("bluetusk.delivery-id", bytes(id))
            .add("bluetusk.format-version", bytes("1"))
            .add("bluetusk.transform-fingerprint", bytes(TRANSFORM_FINGERPRINT));
        publish(data, new ProducerRecord<>(STATE, "position", bytes(position)));
        staged.clear();
        pending.clear();
    }

    public void beforeAcknowledge() throws IOException {
        if (pending.isEmpty()) return;
        var fragment = ByteBuffer.allocate((pending.size() + 1) * Long.BYTES).order(ByteOrder.LITTLE_ENDIAN);
        fragment.putLong(currentTransaction);
        pending.forEach(fragment::putLong);
        staged.add(publish(new ProducerRecord<>(STAGE, currentTransaction + "-" + pending.getFirst(), fragment.array())));
        pending.clear();
        durableFragments++;
    }

    public int durableFragments() { return durableFragments; }
    public String implementation() { return "native Apache Kafka Java producer"; }

    @SafeVarargs
    private final long publish(ProducerRecord<String, byte[]> first, ProducerRecord<String, byte[]>... rest) throws IOException {
        producer.beginTransaction();
        try {
            var metadata = producer.send(first);
            for (var record : rest) producer.send(record);
            producer.commitTransaction();
            return metadata.get().offset();
        } catch (KafkaException | ExecutionException failure) {
            throw new IOException("Kafka did not confirm the complete native transaction.", failure);
        } catch (InterruptedException failure) {
            Thread.currentThread().interrupt();
            throw new IOException("Reading the committed Kafka offset was interrupted.", failure);
        }
    }

    private void readFragment(long offset, List<Long> keys) throws IOException {
        fragments.seek(STAGE_PARTITION, offset);
        var deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(15);
        while (System.nanoTime() < deadline) {
            for (var record : fragments.poll(Duration.ofMillis(100))) {
                if (record.offset() != offset) throw new IOException("Kafka did not return the exact staged fragment.");
                var buffer = ByteBuffer.wrap(record.value()).order(ByteOrder.LITTLE_ENDIAN);
                if (buffer.remaining() <= Long.BYTES || buffer.remaining() % Long.BYTES != 0 || buffer.getLong() != currentTransaction) {
                    throw new IOException("The committed Kafka fragment belongs to another source transaction.");
                }
                while (buffer.hasRemaining()) keys.add(buffer.getLong());
                return;
            }
        }
        throw new IOException("The committed Kafka fragment did not become readable.");
    }

    private byte[] envelope(List<Long> keys, String id, long transactionId, String position, String xid, long commitMicros) throws IOException {
        var root = JSON.createObjectNode().put("formatVersion", 1).put("event", "transaction").put("deliveryId", id).put("pipelineId", PIPELINE);
        root.putObject("source").put("systemIdentifier", SOURCE.get(0)).put("databaseName", SOURCE.get(1))
            .put("slotName", SOURCE.get(2)).put("publicationFingerprint", SOURCE.get(3)).put("fingerprint", SOURCE_FINGERPRINT);
        root.putObject("transform").put("name", TRANSFORM).put("fingerprint", TRANSFORM_FINGERPRINT);
        var commit = Instant.ofEpochSecond(Math.floorDiv(commitMicros, 1_000_000), Math.floorMod(commitMicros, 1_000_000) * 1000);
        root.putObject("transaction").put("transactionId", transactionId).put("commitEndPosition", position)
            .put("commitTimestamp", commit.toString()).put("outcome", "Committed");
        var mutations = root.putArray("mutations");
        for (int ordinal = 0; ordinal < keys.size(); ordinal++) {
            var key = keys.get(ordinal);
            ObjectNode mutation = mutations.addObject().put("stableId", SOURCE_FINGERPRINT + ":" + position + ":" + xid + ":" + ordinal)
                .put("kind", "Upsert").put("collection", COLLECTION).put("key", Long.toString(key)).put("contentType", "application/json");
            mutation.put("contentBase64", bytes("{\"id\":" + key + "}"));
        }
        return JSON.writeValueAsBytes(root);
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

    private static byte[] bytes(String value) { return value.getBytes(StandardCharsets.UTF_8); }
    private static String digest(byte[] value) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(value));
        } catch (NoSuchAlgorithmException failure) {
            throw new IllegalStateException("The reference JVM must support SHA-256.", failure);
        }
    }
}
