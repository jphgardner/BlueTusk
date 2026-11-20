import com.fasterxml.jackson.databind.JsonNode;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashSet;
import java.util.Locale;
import java.util.concurrent.Executors;

/** Isolated receiver that returns applied only after the signed request is stored durably. */
final class WebhookReceiptFixture {
    private static final Path ROOT = Path.of("/tmp/sync-webhook-data");
    private static final Path PROVISION = ROOT.resolve("provision.json");
    private static final Path TRANSACTIONS = ROOT.resolve("transactions");
    private static final Path STAGED = ROOT.resolve("staged");
    private static final int MAX_BODY = 8 * 1024 * 1024;
    private final byte[] signingKey = WebhookCaptureProtocol.signingKey();

    public static void main(String[] args) throws IOException {
        var fixture = new WebhookReceiptFixture();
        if (args.length == 0) {
            fixture.serve();
        } else if (args.length == 3 && args[0].equals("verify")) {
            fixture.verify(Integer.parseInt(args[1]), args[2]);
        } else {
            throw new IllegalArgumentException("Start the receiver or provide the source transaction sequence for verification.");
        }
    }

    private void serve() throws IOException {
        Files.createDirectories(TRANSACTIONS);
        Files.createDirectories(STAGED);
        syncDirectory(ROOT);
        var server = HttpServer.create(new InetSocketAddress(8099), 12);
        server.setExecutor(Executors.newSingleThreadExecutor());
        server.createContext("/health", exchange -> {
            try (exchange) { reply(exchange, 200, null); }
        });
        server.createContext("/delivery", this::receive);
        server.start();
    }

    private void receive(HttpExchange exchange) throws IOException {
        try (exchange) {
            try {
                require(exchange.getRequestMethod().equals("POST") && exchange.getRequestURI().getPath().equals("/delivery"),
                    "Unexpected webhook route.");
                var headers = exchange.getRequestHeaders();
                byte[] payload = exchange.getRequestBody().readNBytes(MAX_BODY + 1);
                String timestamp = headers.getFirst(WebhookCaptureProtocol.TIMESTAMP);
                String signature = headers.getFirst(WebhookCaptureProtocol.SIGNATURE);
                require(payload.length > 0 && payload.length <= MAX_BODY &&
                    WebhookCaptureProtocol.KEY_ID.equals(headers.getFirst(WebhookCaptureProtocol.KEY_HEADER)) &&
                    WebhookCaptureProtocol.signed(signingKey, timestamp, signature, payload), "Invalid signed webhook request.");
                long now = Instant.now().getEpochSecond();
                long sentAt = Long.parseLong(timestamp);
                require(sentAt >= now - 300 && sentAt <= now + 300, "Invalid webhook timestamp.");
                var body = WebhookCaptureProtocol.JSON.readTree(payload);
                String event = text(body, "event");
                String id = text(body, "deliveryId");
                require(body.required("formatVersion").asInt() == 1 && id.matches("btw-[a-f0-9]{64}") &&
                    id.equals(headers.getFirst(WebhookCaptureProtocol.DELIVERY_ID)) &&
                    event.equals(headers.getFirst(WebhookCaptureProtocol.EVENT)) &&
                    text(body, "pipelineId").equals(WebhookCaptureProtocol.PIPELINE), "Invalid webhook identity.");

                var receipt = new Receipt(timestamp, signature, payload, body);
                byte[] responseBody = null;
                boolean duplicate = false;
                if (event.equals("provision")) {
                    duplicate = persist(PROVISION, receipt);
                    exchange.getResponseHeaders().set(WebhookCaptureProtocol.TRANSFORM_HEADER,
                        text(body.required("transform"), "fingerprint"));
                } else {
                    require(Files.isRegularFile(PROVISION), "The webhook pipeline must be provisioned.");
                    switch (event) {
                        case "transaction" -> duplicate = persist(TRANSACTIONS.resolve(id + ".json"), receipt);
                        case "stage" -> duplicate = persist(STAGED.resolve(id + ".json"), receipt);
                        case "stage.read", "stage.retire" -> {
                            var path = STAGED.resolve(id + ".json");
                            var staged = read(path);
                            require(staged.body().required("transactionId").longValue() == body.required("transactionId").longValue(),
                                "The staged fragment belongs to another source transaction.");
                            if (event.equals("stage.read")) {
                                responseBody = staged.payload();
                            } else {
                                Files.delete(path);
                                syncDirectory(STAGED);
                            }
                        }
                        default -> throw new IOException("Unexpected webhook event.");
                    }
                }
                exchange.getResponseHeaders().set(WebhookCaptureProtocol.STATUS, duplicate ? "duplicate" : "applied");
                reply(exchange, 200, responseBody);
            } catch (IOException | RuntimeException failure) {
                System.err.println("The isolated webhook receiver rejected a request (" + failure.getClass().getSimpleName() + ").");
                reply(exchange, 409, null);
            }
        }
    }

    private static void reply(HttpExchange exchange, int status, byte[] body) throws IOException {
        if (body == null) {
            exchange.sendResponseHeaders(status, -1);
        } else {
            exchange.getResponseHeaders().set("Content-Type", "application/json");
            exchange.sendResponseHeaders(status, body.length);
            exchange.getResponseBody().write(body);
        }
    }

    private boolean persist(Path path, Receipt receipt) throws IOException {
        if (Files.exists(path)) {
            require(Arrays.equals(read(path).payload(), receipt.payload()), "A webhook delivery ID already names different bytes.");
            return true;
        }
        byte[] record = WebhookCaptureProtocol.JSON.writeValueAsBytes(WebhookCaptureProtocol.JSON.createObjectNode()
            .put("timestamp", receipt.timestamp()).put("signature", receipt.signature()).put("payload", receipt.payload()));
        Path temporary = Files.createTempFile(path.getParent(), "receipt-", ".tmp");
        try {
            try (var file = FileChannel.open(temporary, StandardOpenOption.WRITE)) {
                var remaining = ByteBuffer.wrap(record);
                while (remaining.hasRemaining()) file.write(remaining);
                file.force(true);
            }
            Files.move(temporary, path, StandardCopyOption.ATOMIC_MOVE);
            syncDirectory(path.getParent());
        } finally {
            Files.deleteIfExists(temporary);
        }
        return false;
    }

    private Receipt read(Path path) throws IOException {
        var record = WebhookCaptureProtocol.JSON.readTree(Files.readAllBytes(path));
        var payload = record.required("payload").binaryValue();
        String timestamp = text(record, "timestamp");
        String signature = text(record, "signature");
        require(WebhookCaptureProtocol.signed(signingKey, timestamp, signature, payload), "The recovered webhook signature differs.");
        return new Receipt(timestamp, signature, payload, WebhookCaptureProtocol.JSON.readTree(payload));
    }

    private void verify(int changes, String sequence) throws IOException {
        require(changes == 1 || changes == 100 || changes == 1000, "Unexpected benchmark mutation count.");
        String[] source = sequence.split(",");
        var configuration = read(PROVISION).body();
        require(text(configuration, "event").equals("provision") &&
            text(configuration, "pipelineId").equals(WebhookCaptureProtocol.PIPELINE), "The recovered provisioning receipt is invalid.");
        var provenance = configuration.required("source");
        var transform = configuration.required("transform");
        var fingerprint = text(provenance, "fingerprint");
        require(text(transform, "fingerprint").equals(WebhookCaptureProtocol.TRANSFORM_FINGERPRINT),
            "The recovered webhook transform is invalid.");

        var transactions = new ArrayList<JsonNode>();
        try (var files = Files.list(TRANSACTIONS)) {
            for (Path path : files.toList()) {
                var transaction = read(path).body();
                require(path.getFileName().toString().equals(text(transaction, "deliveryId") + ".json"),
                    "A recovered transaction has the wrong durable identity.");
                transactions.add(transaction);
            }
        }
        transactions.sort((first, second) -> position(first).compareTo(position(second)));
        try (var files = Files.list(STAGED)) {
            require(files.findAny().isEmpty(), "The webhook receiver retained an incomplete HTTP fragment.");
        }
        require(transactions.size() == source.length, "The webhook receiver did not recover every source transaction.");

        var ids = new HashSet<Long>();
        String previousPosition = "";
        long finalId = 0;
        for (int batch = 0; batch < source.length; batch++) {
            String[] fields = source[batch].split(":");
            long xid = Long.parseLong(fields[0]);
            var timestamp = WebhookCaptureProtocol.instant(Long.parseLong(fields[1]));
            var envelope = transactions.get(batch);
            var commit = envelope.required("transaction");
            String position = position(envelope);
            var mutations = envelope.required("mutations");
            var xidHex = String.format(Locale.ROOT, "%08x", xid);
            require(text(envelope, "event").equals("transaction") &&
                    text(envelope, "pipelineId").equals(WebhookCaptureProtocol.PIPELINE) &&
                    envelope.required("source").equals(provenance) && envelope.required("transform").equals(transform) &&
                    text(envelope, "deliveryId").equals(WebhookCaptureProtocol.deliveryId("transaction", WebhookCaptureProtocol.PIPELINE,
                        fingerprint, position, xidHex)) &&
                    position.matches("[0-9a-f]{16}") && position.compareTo(previousPosition) > 0 &&
                    ids.add(xid) && commit.required("transactionId").longValue() == xid &&
                    Instant.parse(text(commit, "commitTimestamp")).equals(timestamp) && text(commit, "outcome").equals("Committed") &&
                    mutations.isArray() && mutations.size() == changes, "A recovered webhook differs from its source commit.");
            verifyRows(mutations, batch, changes, fingerprint + ":" + position + ":" + xidHex);
            previousPosition = position;
            finalId = xid;
        }
        var proof = WebhookCaptureProtocol.JSON.createObjectNode().put("transactions", source.length)
            .put("documents", Math.multiplyExact(source.length, changes)).put("finalSourceTransactionId", finalId)
            .put("committedPosition", previousPosition).put("recoveredFromWebhooks", true);
        System.out.println(WebhookCaptureProtocol.JSON.writeValueAsString(proof));
    }

    private static void verifyRows(JsonNode mutations, int batch, int changes, String identity) throws IOException {
        for (int ordinal = 0; ordinal < changes; ordinal++) {
            var row = mutations.get(ordinal);
            var key = Long.toString(batch * (long) changes + ordinal + 1);
            require(text(row, "stableId").equals(identity + ":" + ordinal) && text(row, "kind").equals("Upsert") &&
                text(row, "collection").equals(WebhookCaptureProtocol.COLLECTION) && text(row, "key").equals(key) &&
                text(row, "contentType").equals("application/json") && !row.has("partitionKey") &&
                Arrays.equals(row.required("contentBase64").binaryValue(), WebhookCaptureProtocol.bytes("{\"id\":" + key + "}")),
                "A recovered webhook mutation differs from the committed input.");
        }
    }

    private static String position(JsonNode body) { return text(body.required("transaction"), "commitEndPosition"); }

    private static String text(JsonNode body, String field) { return body.required(field).asText(); }

    private static void syncDirectory(Path directory) throws IOException {
        try (var directoryFile = FileChannel.open(directory, StandardOpenOption.READ)) { directoryFile.force(true); }
    }

    private static void require(boolean condition, String message) throws IOException {
        if (!condition) throw new IOException(message);
    }

    private record Receipt(String timestamp, String signature, byte[] payload, JsonNode body) { }
}
