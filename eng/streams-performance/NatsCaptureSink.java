import io.nats.client.JetStream;
import io.nats.client.JetStreamApiException;
import io.nats.client.JetStreamManagement;
import io.nats.client.Nats;
import io.nats.client.PublishOptions;
import io.nats.client.api.StorageType;
import io.nats.client.api.StreamConfiguration;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.List;
import java.util.Locale;

/** Native JetStream reference that persists exactly one complete envelope per source transaction. */
final class NatsCaptureSink implements SyncCaptureReference {
    private static final String STREAM = "SYNC_CAPTURE";
    private static final String STAGE = "SYNC_CAPTURE_STAGE";
    private static final String SUBJECT = "sync.capture.transaction";
    private static final String PART = "sync.stage.part";
    private static final String PIPELINE = "sync_capture";
    private static final String TRANSFORM = "sync-capture";
    private static final String COLLECTION = "streams_capture";
    private static final List<String> SOURCE = List.of("pinned-postgres-fixture", "bluetusk_benchmark", COLLECTION, COLLECTION);
    private static final long UNIX_EPOCH_TICKS = 621_355_968_000_000_000L;

    private final MessageDigest sha = sha256();
    private final String fingerprint = hex(sha.digest(bytes(String.join("\n", SOURCE))));
    private final JetStream jetStream;
    private final JetStreamManagement management;
    private final ArrayList<Long> pending = new ArrayList<>();
    private final ArrayList<Long> staged = new ArrayList<>();
    private long currentTransaction;
    private int durableFragments;

    NatsCaptureSink() throws IOException, InterruptedException {
        var connection = Nats.connect("nats://nats:4222");
        jetStream = connection.jetStream();
        management = connection.jetStreamManagement();
        try {
            management.addStream(config(STREAM, "sync.capture.>"));
            management.addStream(config(STAGE, PART));
        } catch (JetStreamApiException failure) {
            throw new IOException("Could not provision the isolated capture streams.", failure);
        }
    }

    public void deliver(long key, long transactionId, long lsn, long commitMicros, boolean last) throws IOException {
        currentTransaction = transactionId;
        pending.add(key);
        if (!last) return;

        var keys = new ArrayList<Long>();
        try {
            for (long sequence : staged) {
                var fragment = ByteBuffer.wrap(management.getMessage(STAGE, sequence).getData()).order(ByteOrder.LITTLE_ENDIAN);
                if (fragment.remaining() <= Long.BYTES || fragment.remaining() % Long.BYTES != 0 || fragment.getLong() != transactionId) {
                    throw new IOException("The JetStream fragment does not match the pending source transaction.");
                }
                while (fragment.hasRemaining()) keys.add(fragment.getLong());
            }
            keys.addAll(pending);
            publish(STREAM, SUBJECT, "capture-" + transactionId + "-" + lsn, envelope(keys, transactionId, lsn, commitMicros));
            if (!staged.isEmpty() && !management.purgeStream(STAGE).isSuccess()) {
                throw new IOException("The acknowledged JetStream fragments could not be reclaimed.");
            }
        } catch (JetStreamApiException failure) {
            throw new IOException("The native JetStream destination could not publish the complete source transaction.", failure);
        }
        staged.clear();
        pending.clear();
    }

    public void beforeAcknowledge() throws IOException {
        if (pending.isEmpty()) return;
        var fragment = ByteBuffer.allocate((pending.size() + 1) * Long.BYTES).order(ByteOrder.LITTLE_ENDIAN);
        fragment.putLong(currentTransaction);
        for (long key : pending) fragment.putLong(key);
        try {
            staged.add(publish(STAGE, PART, "capture-part-" + currentTransaction + "-" + pending.getFirst(), fragment.array()));
        } catch (JetStreamApiException failure) {
            throw new IOException("The native JetStream sink could not persist an HTTP fragment.", failure);
        }
        pending.clear();
        durableFragments++;
    }

    public int durableFragments() { return durableFragments; }

    public String implementation() { return "native NATS JetStream jnats"; }

    private long publish(String stream, String subject, String id, byte[] payload) throws IOException, JetStreamApiException {
        var options = PublishOptions.builder().expectedStream(stream).messageId(id).build();
        var ack = jetStream.publish(subject, payload, options);
        if (ack.getSeqno() <= 0 || ack.isDuplicate() || !stream.equals(ack.getStream())) {
            throw new IOException("JetStream did not acknowledge a new durable message in the intended stream.");
        }
        return ack.getSeqno();
    }

    private byte[] envelope(List<Long> keys, long transactionId, long lsn, long commitMicros) {
        var content = new ByteArrayOutputStream();
        content.writeBytes(bytes("BTSN"));
        write(content, 1, Short.BYTES);
        content.write(1); // Transaction
        content.write(0);
        text(content, PIPELINE);
        text(content, TRANSFORM);
        text(content, transformFingerprint());
        for (var part : SOURCE) text(content, part);
        write(content, transactionId, Integer.BYTES);
        write(content, lsn, Long.BYTES);
        write(content, Math.addExact(UNIX_EPOCH_TICKS, Math.multiplyExact(commitMicros, 10)), Long.BYTES);
        content.write(0); // Committed
        text(content, null); // No global transaction ID
        write(content, keys.size(), Integer.BYTES);
        for (int ordinal = 0; ordinal < keys.size(); ordinal++) {
            var key = Long.toString(keys.get(ordinal));
            text(content, String.format(Locale.ROOT, "%s:%016x:%08x:%08x", fingerprint, lsn, transactionId, ordinal));
            content.write(0); // Upsert
            text(content, COLLECTION);
            text(content, key);
            text(content, "{\"id\":" + key + "}");
            text(content, "application/json");
            text(content, null); // No partition key
        }
        content.writeBytes(sha.digest(content.toByteArray()));
        return content.toByteArray();
    }

    private String transformFingerprint() {
        var identity = new ByteArrayOutputStream();
        text(identity, TRANSFORM);
        text(identity, "1");
        write(identity, 0, Integer.BYTES);
        return hex(sha.digest(identity.toByteArray()));
    }

    private static StreamConfiguration config(String stream, String subject) {
        return StreamConfiguration.builder().name(stream).subjects(subject).storageType(StorageType.File)
            .maxBytes(10L * 1024 * 1024 * 1024).maxMsgSize(1024 * 1024).replicas(1)
            .maxAge(Duration.ofDays(7)).duplicateWindow(Duration.ofHours(24)).build();
    }

    private static void text(ByteArrayOutputStream output, String value) {
        if (value == null) {
            write(output, -1, Integer.BYTES);
            return;
        }
        byte[] bytes = bytes(value);
        write(output, bytes.length, Integer.BYTES);
        output.writeBytes(bytes);
    }

    private static void write(ByteArrayOutputStream output, long value, int length) {
        for (int index = 0; index < length; index++) output.write((int)(value >>> (index * 8)));
    }

    private static byte[] bytes(String value) { return value.getBytes(StandardCharsets.UTF_8); }

    private static String hex(byte[] bytes) { return HexFormat.of().formatHex(bytes); }

    private static MessageDigest sha256() {
        try {
            return MessageDigest.getInstance("SHA-256");
        } catch (NoSuchAlgorithmException exception) {
            throw new IllegalStateException("The reference JVM must support SHA-256.", exception);
        }
    }
}
