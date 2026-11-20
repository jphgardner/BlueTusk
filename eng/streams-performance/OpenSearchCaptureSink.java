import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
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
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.Map;

/** Native REST baseline with external versions and a checkpoint after validated bulk writes. */
final class OpenSearchCaptureSink implements SyncCaptureReference {
    private static final String CONTENT_TYPE = "application/json";
    private static final String BULK = "_bulk?wait_for_active_shards=all&refresh=false";
    private static final String COLLECTION = "streams_capture";
    private static final String CONTROL = "bluetusk-sync-control";
    private static final String STAGE = "sync-benchmark-stage";
    private static final ObjectMapper JSON = new ObjectMapper();

    private final HttpClient http = HttpClient.newBuilder().version(HttpClient.Version.HTTP_1_1).connectTimeout(Duration.ofSeconds(10)).build();
    private final MessageDigest sha = sha256();
    private final String pipeline = fingerprint("sync_capture").substring(0, 24);
    private final String transform = transformFingerprint();
    private final String generation = transform.substring(0, 16);
    private final String documents = "bluetusk-sync-p" + pipeline + "-c" + fingerprint(COLLECTION).substring(0, 24) + "-g" + generation;
    private final String reconcile = documents + "-reconcile";
    private final String checkpoint = CONTROL + "/_doc/checkpoint-" + pipeline + "-" + generation;
    private final String sourceFingerprint = fingerprint("pinned-postgres-fixture\nbluetusk_benchmark\nstreams_capture\nstreams_capture");
    private final ArrayList<Long> pending = new ArrayList<>();
    private final ArrayList<String> staged = new ArrayList<>();
    private long currentTransaction;
    private int durableFragments;

    OpenSearchCaptureSink() throws IOException {
        create(CONTROL, Map.of("properties", Map.of("recordType", Map.of("type", "keyword"),
            "position", Map.of("type", "unsigned_long"), "transactionId", Map.of("type", "long"))));
        create(documents, Map.of("dynamic", true));
        create(reconcile, Map.of("dynamic", "strict", "properties", Map.of(
            "recordType", Map.of("type", "keyword"), "formatVersion", Map.of("type", "integer"),
            "pipelineHash", Map.of("type", "keyword"), "generation", Map.of("type", "keyword"),
            "collection", Map.of("type", "keyword"), "key", Map.of("type", "keyword", "ignore_above", 8192),
            "keyHash", Map.of("type", "unsigned_long"), "contentHash", Map.of("type", "keyword"),
            "contentType", Map.of("type", "keyword"), "partitionKey", Map.of("type", "keyword"))));
        create(STAGE, Map.of("properties", Map.of("transactionId", Map.of("type", "long"), "keys", Map.of("type", "long"))));
    }

    public void deliver(long key, long transactionId, long lsn, long commitMicros, boolean last) throws IOException {
        currentTransaction = transactionId;
        pending.add(key);
        if (!last) return;

        var existingCheckpoint = request("GET", checkpoint, null, CONTENT_TYPE, true);
        if (existingCheckpoint != null && existingCheckpoint.required("_source").required("position").longValue() >= lsn) {
            throw new IOException("The OpenSearch reference did not receive a new source position.");
        }

        var keys = new ArrayList<Long>();
        for (var id : staged) {
            var fragment = request("GET", STAGE + "/_doc/" + id, null, CONTENT_TYPE, false).required("_source");
            if (fragment.required("transactionId").longValue() != transactionId) {
                throw new IOException("The OpenSearch fragment belongs to another source transaction.");
            }
            for (var row : fragment.required("keys")) keys.add(row.longValue());
        }
        keys.addAll(pending);

        var routingReads = JSON.createArrayNode();
        var requestBody = new StringBuilder();
        for (long numeric : keys) {
            var text = Long.toString(numeric);
            var id = fingerprint(COLLECTION + "\n" + text);
            routingReads.addObject().put("_index", reconcile).put("_id", id).putArray("_source").add("partitionKey");
            var payload = "{\"id\":" + text + "}";
            index(requestBody, documents, id, lsn, payload);
            var sidecar = JSON.createObjectNode().put("recordType", "document").put("formatVersion", 2)
                .put("pipelineHash", pipeline).put("generation", generation).put("collection", COLLECTION).put("key", text)
                .put("keyHash", Integer.toUnsignedLong(ByteBuffer.wrap(sha.digest(bytes(text))).getInt()))
                .put("contentHash", fingerprint(payload)).put("contentType", CONTENT_TYPE).putNull("partitionKey");
            index(requestBody, reconcile, id, lsn, sidecar.toString());
        }
        var previous = request("POST", "_mget", JSON.createObjectNode().set("docs", routingReads).toString(), CONTENT_TYPE, false).required("docs");
        if (previous.size() != keys.size()) throw new IOException("The routing lookup returned incomplete results.");
        for (var result : previous) {
            if (result.has("error") || result.required("found").booleanValue()) throw new IOException("The fixture contains an unexpected document version.");
        }
        bulk(requestBody, 2 * keys.size(), "index", lsn);
        var state = JSON.createObjectNode().put("recordType", "checkpoint").put("formatVersion", 2).put("pipelineHash", pipeline)
            .put("sourceFingerprint", sourceFingerprint).put("transformFingerprint", transform).put("generation", generation)
            .put("position", lsn).put("transactionId", transactionId);
        var saved = request("PUT", checkpoint + "?version=" + lsn + "&version_type=external_gte&refresh=wait_for", state.toString(), CONTENT_TYPE, false);
        if (saved.required("_version").longValue() != lsn) throw new IOException("OpenSearch did not acknowledge the exact checkpoint version.");

        if (!staged.isEmpty()) {
            var cleanup = new StringBuilder();
            for (var id : staged) cleanup.append(JSON.writeValueAsString(Map.of("delete", Map.of("_index", STAGE, "_id", id)))).append('\n');
            bulk(cleanup, staged.size(), "delete", 0);
        }
        staged.clear();
        pending.clear();
    }

    public void beforeAcknowledge() throws IOException {
        if (pending.isEmpty()) return;
        String id = currentTransaction + "-" + staged.size();
        var fragment = JSON.createObjectNode().put("transactionId", currentTransaction);
        fragment.set("keys", JSON.valueToTree(pending));
        var saved = request("PUT", STAGE + "/_doc/" + id + "?op_type=create&wait_for_active_shards=all", fragment.toString(), CONTENT_TYPE, false);
        if (!saved.required("result").asText().equals("created")) throw new IOException("OpenSearch did not persist the HTTP fragment.");
        staged.add(id);
        pending.clear();
        durableFragments++;
    }

    public int durableFragments() { return durableFragments; }

    public String implementation() { return "native OpenSearch REST"; }

    private void create(String index, Map<String, ?> mappings) throws IOException {
        var configuration = Map.of("settings", Map.of("number_of_shards", 1, "number_of_replicas", 0, "index.translog.durability", "request"),
            "mappings", mappings);
        var response = request("PUT", index, JSON.writeValueAsString(configuration), CONTENT_TYPE, false);
        if (!response.required("acknowledged").booleanValue()) throw new IOException("OpenSearch did not provision the benchmark index.");
    }

    private static void index(StringBuilder request, String index, String id, long version, String data) throws IOException {
        var action = Map.of("index", Map.of("_index", index, "_id", id, "version", version, "version_type", "external_gte"));
        request.append(JSON.writeValueAsString(action)).append('\n').append(data).append('\n');
    }

    private void bulk(StringBuilder body, int expected, String method, long version) throws IOException {
        var response = request("POST", BULK, body.toString(), "application/x-ndjson", false);
        var items = response.required("items");
        if (response.required("errors").booleanValue() || items.size() != expected) {
            throw new IOException("OpenSearch rejected part of the reference bulk request: " + response.toString().substring(0, Math.min(response.toString().length(), 400)));
        }
        for (var item : items) {
            var itemResult = item.required(method);
            int status = itemResult.required("status").intValue();
            if (status < 200 || status >= 300 || (version != 0 && itemResult.required("_version").longValue() != version)) {
                throw new IOException("OpenSearch did not acknowledge every individual reference bulk operation.");
            }
        }
    }

    private JsonNode request(String method, String path, String body, String type, boolean allowMissing) throws IOException {
        var payload = body == null ? HttpRequest.BodyPublishers.noBody() : HttpRequest.BodyPublishers.ofString(body);
        var request = HttpRequest.newBuilder(URI.create("http://opensearch:9200/" + path)).timeout(Duration.ofSeconds(25))
            .header("Content-Type", type).method(method, payload).build();
        try {
            var response = http.send(request, HttpResponse.BodyHandlers.ofByteArray());
            if (allowMissing && response.statusCode() == 404) return null;
            if (response.statusCode() < 200 || response.statusCode() >= 300) {
                var detail = new String(response.body(), StandardCharsets.UTF_8);
                throw new IOException(method + " " + path + " returned HTTP " + response.statusCode() + ": " + detail.substring(0, Math.min(400, detail.length())));
            }
            return JSON.readTree(response.body());
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            throw new IOException("The native OpenSearch request was interrupted.", exception);
        }
    }

    private String transformFingerprint() {
        var identity = new ByteArrayOutputStream();
        for (var part : new String[] { "sync-capture", "1", "" }) {
            var data = bytes(part);
            identity.writeBytes(ByteBuffer.allocate(Integer.BYTES).order(ByteOrder.LITTLE_ENDIAN).putInt(data.length).array());
            identity.writeBytes(data);
        }
        return HexFormat.of().formatHex(sha.digest(identity.toByteArray()));
    }

    private String fingerprint(String text) { return HexFormat.of().formatHex(sha.digest(bytes(text))); }

    private static byte[] bytes(String value) { return value.getBytes(StandardCharsets.UTF_8); }

    private static MessageDigest sha256() {
        try {
            return MessageDigest.getInstance("SHA-256");
        } catch (NoSuchAlgorithmException exception) {
            throw new IllegalStateException("The reference JVM must support SHA-256.", exception);
        }
    }
}
