import com.fasterxml.jackson.databind.JsonNode;
import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Locale;

/** Posts one signed webhook with a durable acknowledgement per PostgreSQL transaction. */
final class WebhookCaptureSink implements SyncCaptureReference {
    private static final URI ENDPOINT = URI.create("http://webhooks:8099/delivery");
    private final HttpClient client = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(5)).build();
    private final byte[] signingKey = WebhookCaptureProtocol.signingKey();
    private final ArrayList<Long> pending = new ArrayList<>();
    private final ArrayList<String> staged = new ArrayList<>();
    private long currentTransaction;
    private int durableFragments;

    WebhookCaptureSink() throws IOException {
        var id = WebhookCaptureProtocol.deliveryId("provision", WebhookCaptureProtocol.PIPELINE,
            WebhookCaptureProtocol.SOURCE_FINGERPRINT, WebhookCaptureProtocol.TRANSFORM_FINGERPRINT);
        var response = send(WebhookCaptureProtocol.envelope("provision", id));
        if (!response.headers().firstValue(WebhookCaptureProtocol.TRANSFORM_HEADER).orElse("")
                .equals(WebhookCaptureProtocol.TRANSFORM_FINGERPRINT)) {
            throw new IOException("The receiver acknowledged an unexpected transform.");
        }
    }

    @Override
    public void deliver(long key, long transactionId, long lsn, long commitMicros, boolean last) throws IOException {
        if (pending.isEmpty() && staged.isEmpty()) currentTransaction = transactionId;
        if (currentTransaction != transactionId) throw new IOException("The webhook received an incomplete source transaction.");
        pending.add(key);
        if (!last) return;

        var keys = new ArrayList<Long>();
        for (String fragmentId : staged) {
            var fragment = WebhookCaptureProtocol.JSON.readTree(send(stageRequest("stage.read", fragmentId)).body());
            if (fragment.required("transactionId").longValue() != transactionId) {
                throw new IOException("The staged HTTP fragment belongs to a different transaction.");
            }
            for (var value : fragment.required("keys")) keys.add(value.longValue());
        }
        keys.addAll(pending);
        var position = String.format(Locale.ROOT, "%016x", lsn);
        var xid = String.format(Locale.ROOT, "%08x", transactionId);
        var id = WebhookCaptureProtocol.deliveryId("transaction", WebhookCaptureProtocol.PIPELINE,
            WebhookCaptureProtocol.SOURCE_FINGERPRINT, position, xid);
        var body = WebhookCaptureProtocol.envelope("transaction", id);
        body.putObject("transaction").put("transactionId", transactionId).put("commitEndPosition", position)
            .put("commitTimestamp", WebhookCaptureProtocol.instant(commitMicros).toString()).put("outcome", "Committed");
        var mutations = body.putArray("mutations");
        for (int ordinal = 0; ordinal < keys.size(); ordinal++) {
            var text = Long.toString(keys.get(ordinal));
            mutations.addObject().put("stableId", WebhookCaptureProtocol.SOURCE_FINGERPRINT + ":" + position + ":" + xid + ":" + ordinal)
                .put("kind", "Upsert").put("collection", WebhookCaptureProtocol.COLLECTION).put("key", text)
                .put("contentType", "application/json").put("contentBase64", WebhookCaptureProtocol.bytes("{\"id\":" + text + "}"));
        }
        send(body);
        for (String fragmentId : staged) send(stageRequest("stage.retire", fragmentId));
        staged.clear();
        pending.clear();
    }

    @Override
    public void beforeAcknowledge() throws IOException {
        if (pending.isEmpty()) return;
        var id = WebhookCaptureProtocol.deliveryId("stage", Long.toString(currentTransaction), Long.toString(pending.getFirst()));
        var body = stageRequest("stage", id);
        var keys = body.putArray("keys");
        pending.forEach(keys::add);
        send(body);
        staged.add(id);
        pending.clear();
        durableFragments++;
    }

    @Override
    public int durableFragments() { return durableFragments; }

    @Override
    public String implementation() { return "native signed JDK HTTP webhook"; }

    private com.fasterxml.jackson.databind.node.ObjectNode stageRequest(String event, String id) {
        return WebhookCaptureProtocol.envelope(event, id).put("transactionId", currentTransaction);
    }

    private HttpResponse<byte[]> send(JsonNode body) throws IOException {
        var payload = WebhookCaptureProtocol.JSON.writeValueAsBytes(body);
        var timestamp = Long.toString(Instant.now().getEpochSecond());
        var request = HttpRequest.newBuilder(ENDPOINT).timeout(Duration.ofSeconds(20))
            .header("Content-Type", "application/json")
            .header(WebhookCaptureProtocol.EVENT, body.required("event").asText())
            .header(WebhookCaptureProtocol.DELIVERY_ID, body.required("deliveryId").asText())
            .header(WebhookCaptureProtocol.KEY_HEADER, WebhookCaptureProtocol.KEY_ID)
            .header(WebhookCaptureProtocol.TIMESTAMP, timestamp)
            .header(WebhookCaptureProtocol.SIGNATURE, WebhookCaptureProtocol.sign(signingKey, timestamp, payload))
            .POST(HttpRequest.BodyPublishers.ofByteArray(payload)).build();
        try {
            var response = client.send(request, HttpResponse.BodyHandlers.ofByteArray());
            if (response.statusCode() != 200 || !response.headers().firstValue(WebhookCaptureProtocol.STATUS).orElse("").equals("applied")) {
                throw new IOException("The destination did not durably acknowledge the webhook.");
            }
            return response;
        } catch (InterruptedException failure) {
            Thread.currentThread().interrupt();
            throw new IOException("The webhook delivery was interrupted.", failure);
        }
    }
}
