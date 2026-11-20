import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ObjectNode;
import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.time.Instant;
import java.util.Base64;
import java.util.HexFormat;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

final class WebhookCaptureProtocol {
    static final ObjectMapper JSON = new ObjectMapper();
    static final String KEY_ID = "sync-fixture-v1";
    static final String EVENT = "BlueTusk-Event";
    static final String DELIVERY_ID = "BlueTusk-Delivery-Id";
    static final String TIMESTAMP = "BlueTusk-Timestamp";
    static final String SIGNATURE = "BlueTusk-Signature";
    static final String KEY_HEADER = "BlueTusk-Key-Id";
    static final String STATUS = "BlueTusk-Delivery-Status";
    static final String TRANSFORM_HEADER = "BlueTusk-Transform-Fingerprint";
    static final String PIPELINE = "sync_capture";
    static final String TRANSFORM = "sync-capture";
    static final String COLLECTION = "streams_capture";
    static final String SYSTEM = "pinned-postgres-fixture";
    static final String DATABASE = "bluetusk_benchmark";
    static final String TRANSFORM_FINGERPRINT = digest(identity(TRANSFORM, "1", ""));
    static final String SOURCE_FINGERPRINT = digest(bytes(String.join("\n", SYSTEM, DATABASE, COLLECTION, COLLECTION)));

    static byte[] signingKey() {
        String configured = System.getenv("BLUETUSK_BENCHMARK_WEBHOOK_KEY");
        if (configured == null) throw new IllegalStateException("The isolated webhook fixture requires a signing key.");
        byte[] key = Base64.getDecoder().decode(configured);
        if (key.length != 32) throw new IllegalStateException("The isolated webhook signing key must have 32 bytes.");
        return key;
    }

    static String sign(byte[] key, String timestamp, byte[] payload) {
        try {
            var mac = Mac.getInstance("HmacSHA256");
            mac.init(new SecretKeySpec(key, "HmacSHA256"));
            mac.update(bytes(timestamp + "."));
            return "v1=" + HexFormat.of().formatHex(mac.doFinal(payload));
        } catch (GeneralSecurityException failure) {
            throw new IllegalStateException("The JVM must support HMAC-SHA256.", failure);
        }
    }

    static boolean signed(byte[] key, String timestamp, String signature, byte[] body) {
        return timestamp != null && signature != null &&
            MessageDigest.isEqual(bytes(signature), bytes(sign(key, timestamp, body)));
    }

    static String deliveryId(String event, String... components) {
        String[] fields = new String[components.length + 2];
        fields[0] = "bluetusk-sync-webhook-v1";
        fields[1] = event;
        System.arraycopy(components, 0, fields, 2, components.length);
        return "btw-" + digest(identity(fields));
    }

    static ObjectNode envelope(String event, String id) {
        var root = JSON.createObjectNode().put("formatVersion", 1).put("event", event).put("deliveryId", id).put("pipelineId", PIPELINE);
        root.putObject("source").put("systemIdentifier", SYSTEM).put("databaseName", DATABASE).put("slotName", COLLECTION)
            .put("publicationFingerprint", COLLECTION).put("fingerprint", SOURCE_FINGERPRINT);
        root.putObject("transform").put("name", TRANSFORM).put("fingerprint", TRANSFORM_FINGERPRINT);
        return root;
    }

    static Instant instant(long micros) {
        return Instant.ofEpochSecond(Math.floorDiv(micros, 1_000_000), Math.floorMod(micros, 1_000_000) * 1000);
    }

    static byte[] bytes(String value) { return value.getBytes(StandardCharsets.UTF_8); }

    private static byte[] identity(String... fields) {
        var output = new ByteArrayOutputStream();
        for (var field : fields) {
            var value = bytes(field);
            output.writeBytes(ByteBuffer.allocate(Integer.BYTES).order(ByteOrder.LITTLE_ENDIAN).putInt(value.length).array());
            output.writeBytes(value);
        }
        return output.toByteArray();
    }

    private static String digest(byte[] value) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(value));
        } catch (GeneralSecurityException failure) {
            throw new IllegalStateException("The JVM must support SHA-256.", failure);
        }
    }

    private WebhookCaptureProtocol() { }
}
