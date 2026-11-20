import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.sun.management.OperatingSystemMXBean;
import com.sun.management.ThreadMXBean;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;
import java.io.IOException;
import java.lang.management.ManagementFactory;
import java.net.InetSocketAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.sql.SQLException;
import java.time.Instant;
import java.util.Arrays;
import java.util.HexFormat;
import java.util.Map;

/** Counts actual HTTP deliveries inside the pinned Debezium JVM. */
public final class StreamsHttpProbe {
    private static final String VERSION = "3.6.1.Final";
    private static final Path OUTPUT = Path.of("/tmp/streams-reference.json");
    private static final ObjectMapper JSON = new ObjectMapper();
    private static final long TICKS_PER_SECOND = 10_000_000;
    private static final int MAX_HTTP_BATCH_BYTES = 2 * 1024 * 1024;
    private static final int SPILL_BYTES = 4 * 1024 * 1024;
    private static final int MAX_SPILL_HTTP_BATCH_BYTES = 8 * 1024 * 1024;

    private final int changes;
    private final int warmupRows;
    private final int measuredRows;
    private final int trial;
    private final String commit;
    private final String image;
    private final String hostOs;
    private final String scenario;
    private final boolean liveDelivery;
    private final boolean snapshot;
    private final boolean spill;
    private final String sink;
    private final SyncCaptureReference destination;
    private final long[] ticks;
    private final long[] commitMicros;
    private final long[] transactionIds;
    private final String[] spillPayloadSha256;
    private final MessageDigest spillHash;
    private final ThreadMXBean threads = (ThreadMXBean) ManagementFactory.getThreadMXBean();
    private final OperatingSystemMXBean process = (OperatingSystemMXBean) ManagementFactory.getOperatingSystemMXBean();
    private int delivered;
    private int spillPayloadBytes;
    private long syncTransactionId;
    private long started;
    private long allocationBefore;
    private long cpuBefore;
    private long gcBefore;
    private boolean complete;
    private boolean failed;

    public static void premain(String arguments) throws IOException, SQLException, InterruptedException {
        var probe = new StreamsHttpProbe(arguments);
        var server = HttpServer.create(new InetSocketAddress("127.0.0.1", 8081), 32);
        server.createContext("/events", probe::receive);
        server.createContext("/progress", probe::progress);
        server.createContext("/measure", probe::begin);
        if (!probe.liveDelivery) {
            probe.startMeasurement();
        }
        // One HTTP handler thread keeps the delivery order and measurement boundaries consistent.
        server.start();
    }

    private StreamsHttpProbe(String arguments) throws IOException, SQLException, InterruptedException {
        var fields = arguments.split(",", -1);
        if (fields.length != 9 || !fields[4].matches("[0-9a-f]{40}") ||
            !fields[5].matches("postgres:[^@\\s]+@sha256:[0-9a-f]{64}") ||
            !fields[6].matches("windows|linux") || !fields[7].matches("commit-to-delivery|catch-up|snapshot|spill") ||
            !fields[8].matches("count|postgresql|redis|nats|opensearch|kafka") || !System.getProperty("os.arch").equals("amd64")) {
            throw new IllegalArgumentException("An exact x64 benchmark identity is required.");
        }
        changes = Integer.parseInt(fields[0]);
        warmupRows = Math.multiplyExact(changes, Integer.parseInt(fields[1]));
        var transactions = Integer.parseInt(fields[2]);
        measuredRows = Math.multiplyExact(changes, transactions);
        trial = Integer.parseInt(fields[3]);
        scenario = fields[7];
        sink = fields[8];
        boolean sync = !sink.equals("count");
        spill = scenario.equals("spill");
        liveDelivery = scenario.equals("commit-to-delivery") || spill;
        snapshot = scenario.equals("snapshot");
        if ((changes != 1 && changes != 1_000 && !(sync && changes == 100)) || (sync && !scenario.equals("commit-to-delivery")) ||
            (liveDelivery ? warmupRows < 1 : warmupRows != 0) || warmupRows > 10_000_000 ||
            transactions < 1 || transactions > 16_000 || measuredRows > 1_048_576 || trial < 0 || trial >= 50 ||
            (spill && warmupRows / changes + transactions > 32)) {
            throw new IllegalArgumentException("The Streams capture exceeds its workload bounds.");
        }
        commit = fields[4];
        image = fields[5];
        hostOs = fields[6];
        ticks = new long[measuredRows];
        commitMicros = new long[snapshot ? 0 : transactions];
        transactionIds = new long[snapshot ? 0 : transactions];
        spillPayloadSha256 = new String[spill ? transactions : 0];
        spillHash = spill ? sha256() : null;
        destination = switch (sink) {
            case "postgresql" -> new PostgreSqlCaptureSink();
            case "redis" -> new RedisCaptureSink();
            case "nats" -> new NatsCaptureSink();
            case "opensearch" -> new OpenSearchCaptureSink();
            case "kafka" -> new KafkaCaptureSink();
            default -> null;
        };
        if (!threads.isThreadAllocatedMemorySupported()) {
            throw new IllegalStateException("The reference JVM must expose its process allocation counter.");
        }
        threads.setThreadAllocatedMemoryEnabled(true);
    }

    private void progress(HttpExchange exchange) throws IOException {
        respond(exchange, failed ? 409 : 200, complete ? "complete" : Integer.toString(delivered));
    }

    private void begin(HttpExchange exchange) throws IOException {
        if (!exchange.getRequestMethod().equals("POST") || failed || started != 0 || delivered != warmupRows) {
            respond(exchange, 409, "Warmup is incomplete or measurement has already started.");
            return;
        }
        startMeasurement();
        respond(exchange, 200, "measuring");
    }

    private void startMeasurement() {
        allocationBefore = threads.getTotalThreadAllocatedBytes();
        cpuBefore = process.getProcessCpuTime();
        gcBefore = collections();
        started = System.nanoTime();
    }

    private void receive(HttpExchange exchange) throws IOException {
        var received = Instant.now();
        long receivedAt = System.nanoTime();
        try {
            if (!exchange.getRequestMethod().equals("POST") || failed || complete) {
                throw new IllegalStateException("Unexpected delivery request.");
            }
            int maxBytes = spill ? MAX_SPILL_HTTP_BATCH_BYTES : MAX_HTTP_BATCH_BYTES;
            var bytes = exchange.getRequestBody().readNBytes(maxBytes + 1);
            if (bytes.length > maxBytes) {
                throw new IllegalStateException("The delivery exceeded the fixture byte bound.");
            }
            var body = JSON.readTree(bytes);
            if (body.isArray()) {
                if (body.isEmpty() || body.size() > 1_000) {
                    throw new IllegalStateException("The HTTP batch has an unexpected row count.");
                }
                for (var event : body) {
                    recordEvent(event, received, receivedAt);
                }
            } else {
                recordEvent(body, received, receivedAt);
            }
            if (destination != null) {
                destination.beforeAcknowledge();
            }
            if (delivered == warmupRows + measuredRows) {
                writeResult();
                complete = true;
            }
            respond(exchange, 200, "accepted");
        } catch (IOException | RuntimeException | SQLException exception) {
            failed = true;
            System.err.println("Streams benchmark probe: " + exception.getMessage());
            respond(exchange, 409, "The Streams benchmark rejected the delivery.");
        }
    }

    private void recordEvent(JsonNode event, Instant received, long receivedAt) throws IOException, SQLException {
        var source = event.required("source");
        var after = event.required("after");
        var row = after.required("id");
        if (delivered >= warmupRows + measuredRows || !row.isIntegralNumber() ||
            !event.required("op").asText().equals(snapshot ? "r" : "c") || !source.required("version").asText().equals(VERSION) ||
            !source.required("schema").asText().equals("public") || !source.required("table").asText().equals("streams_capture")) {
            throw new IllegalStateException("Debezium delivered an unexpected row, operation, version or table.");
        }
        if (snapshot) {
            recordSnapshotRow(row.longValue(), receivedAt);
        } else {
            if (row.longValue() != (long) delivered + 1) {
                throw new IllegalStateException("Debezium delivered a missing, repeated or out-of-order row.");
            }
            if (delivered >= warmupRows) {
                if (started == 0) {
                    throw new IllegalStateException("A measured row arrived before the measurement marker.");
                }
                recordDelivery(source, received, receivedAt);
            }
            if (spill) {
                recordSpillPayload(after.required("payload"));
            }
            if (destination != null) {
                recordSyncDelivery(row.longValue(), source);
            }
        }
        delivered++;
    }

    private void recordSnapshotRow(long row, long receivedAt) {
        long latency = (receivedAt - started) / 100;
        if (row < 1 || row > ticks.length || ticks[(int) row - 1] != 0 || latency <= 0) {
            throw new IllegalStateException("Debezium delivered an unexpected or repeated snapshot row.");
        }
        ticks[(int) row - 1] = latency;
    }

    private void recordDelivery(JsonNode source, Instant received, long receivedAt) {
        var timestamp = source.required("ts_us");
        var transaction = source.required("txId");
        if (!timestamp.isIntegralNumber() || !transaction.isIntegralNumber() || timestamp.longValue() <= 0) {
            throw new IllegalStateException("The source must identify the PostgreSQL transaction and commit timestamp.");
        }
        int sample = delivered - warmupRows;
        int batch = sample / changes;
        if (sample % changes == 0) {
            commitMicros[batch] = timestamp.longValue();
            transactionIds[batch] = transaction.longValue();
        } else if (commitMicros[batch] != timestamp.longValue() || transactionIds[batch] != transaction.longValue()) {
            throw new IllegalStateException("Rows in a transaction reported different source positions.");
        }
        if (spill || destination != null) {
            return;
        }
        long receiveTicks = received.getEpochSecond() * TICKS_PER_SECOND + received.getNano() / 100;
        long latency = liveDelivery ? receiveTicks - timestamp.longValue() * 10 : (receivedAt - started) / 100;
        if (latency <= 0) {
            throw new IllegalStateException("The Streams delivery latency must be positive.");
        }
        ticks[sample] = latency;
    }

    private void recordSyncDelivery(long key, JsonNode source) throws SQLException, IOException {
        var transaction = source.required("txId");
        var lsn = source.required("lsn");
        var timestamp = source.required("ts_us");
        if (!transaction.isIntegralNumber() || !lsn.isIntegralNumber() || !timestamp.isIntegralNumber() ||
            transaction.longValue() <= 0 || lsn.longValue() <= 0 || timestamp.longValue() <= 0) {
            throw new IllegalStateException("The reference Sync sink must receive PostgreSQL positions, transaction IDs and timestamps.");
        }
        int rowInTransaction = delivered % changes;
        if (rowInTransaction == 0) {
            if (transaction.longValue() <= syncTransactionId) {
                throw new IllegalStateException("The reference Sync sink received a repeated or out-of-order source transaction.");
            }
            syncTransactionId = transaction.longValue();
        } else if (transaction.longValue() != syncTransactionId) {
            throw new IllegalStateException("The reference Sync sink received an incomplete transaction.");
        }
        boolean last = rowInTransaction == changes - 1;
        destination.deliver(key, syncTransactionId, lsn.longValue(), timestamp.longValue(), last);
        if (last && delivered >= warmupRows) {
            int batch = (delivered - warmupRows) / changes;
            var committed = Instant.now();
            long latency = committed.getEpochSecond() * TICKS_PER_SECOND + committed.getNano() / 100 - commitMicros[batch] * 10;
            if (latency <= 0) {
                throw new IllegalStateException("The Sync destination latency must be positive.");
            }
            Arrays.fill(ticks, batch * changes, (batch + 1) * changes, latency);
        }
    }

    private void recordSpillPayload(JsonNode payload) throws IOException {
        if (!payload.isTextual()) {
            throw new IllegalStateException("The Debezium sink did not deliver a binary payload.");
        }
        var bytes = payload.binaryValue();
        spillPayloadBytes += bytes.length;
        if (spillPayloadBytes > SPILL_BYTES) {
            throw new IllegalStateException("The Debezium sink delivered more than 4 MiB in a single transaction.");
        }
        spillHash.update(bytes);
        if ((delivered + 1) % changes != 0) {
            return;
        }
        if (spillPayloadBytes != SPILL_BYTES) {
            throw new IllegalStateException("The Debezium sink did not deliver the complete 4 MiB payload.");
        }
        spillPayloadBytes = 0;
        var digest = spillHash.digest();
        if (delivered >= warmupRows) {
            int batch = (delivered - warmupRows) / changes;
            spillPayloadSha256[batch] = HexFormat.of().formatHex(digest);
            var consumed = Instant.now();
            long latency = consumed.getEpochSecond() * TICKS_PER_SECOND + consumed.getNano() / 100 - commitMicros[batch] * 10;
            if (latency <= 0) {
                throw new IllegalStateException("The Streams spill latency must be positive.");
            }
            Arrays.fill(ticks, batch * changes, (batch + 1) * changes, latency);
        }
    }

    private void writeResult() throws IOException {
        long elapsed = (System.nanoTime() - started) / 100;
        long allocated = threads.getTotalThreadAllocatedBytes() - allocationBefore;
        long cpu = process.getProcessCpuTime() - cpuBefore;
        long gc = collections() - gcBefore;
        if (allocationBefore < 0 || cpuBefore < 0 || elapsed <= 0 || allocated < 0 || cpu < 0 || gc < 0) {
            throw new IllegalStateException("The reference JVM returned invalid process counters.");
        }
        long rss = Files.readAllLines(Path.of("/proc/self/status")).stream()
            .filter(line -> line.startsWith("VmHWM:")).map(line -> line.split("\\s+")[1])
            .mapToLong(Long::parseLong).findFirst().orElseThrow() * 1_024;
        var measurement = Map.of("elapsedTicks", elapsed, "completedOperations", measuredRows,
            "allocatedBytes", allocated, "cpuMilliseconds", cpu / 1_000_000.0, "peakWorkingSetBytes", rss,
            "gcCollections", new long[]{gc, 0, 0}, "workers", new Object[]{Map.of("requestTicks", ticks, "count", measuredRows)});
        var environment = Map.of("os", hostOs, "architecture", "x64", "clientOs", "linux", "runtime", System.getProperty("java.runtime.version"),
            "postgreSqlImage", image, "commitMicrosByTransaction", commitMicros, "transactionIds", transactionIds,
            "allocation", "JVM-wide allocated-byte counter delta", "gc", "Total collections across JVM collectors in the first counter",
            "peakRss", "JVM process VmHWM, including the loopback HTTP sink");
        var capture = JSON.createObjectNode();
        var workload = destination == null
            ? hostOs + "|Streams|changes=" + changes + (spill ? "|spoolBytes=" + SPILL_BYTES : "|scenario=" + scenario)
            : hostOs + "|Sync|mutations=" + changes + "|destination=" + sink;
        capture.put("schemaVersion", 1).put("evidenceKind", "bluetusk-performance-trial").put("family", destination == null ? "Streams" : "Sync")
            .put("workloadKey", workload)
            .put("role", "reference").put("trial", trial).put("sourceCommit", commit).put("diagnostic", true)
            .put("synthetic", false).put("implementation", "Debezium Server " + VERSION + (destination == null ? "" : " plus " + destination.implementation()))
            .put("frequency", TICKS_PER_SECOND);
        var environmentNode = JSON.createObjectNode();
        environment.forEach((key, value) -> environmentNode.set(key, JSON.valueToTree(value)));
        if (spill) {
            environmentNode.set("spillPayloadSha256", JSON.valueToTree(spillPayloadSha256));
            environmentNode.put("latency", "WAL commit to complete payload validation in the reference HTTP sink");
        }
        if (destination != null) {
            environmentNode.put("sink", sink);
            environmentNode.put("durableHttpFragments", destination.durableFragments());
            environmentNode.put("latency", "WAL commit to durable destination mutations and checkpoint");
        }
        capture.set("environment", environmentNode);
        capture.set("measurement", JSON.valueToTree(measurement));
        var temporary = OUTPUT.resolveSibling(OUTPUT.getFileName() + ".tmp");
        JSON.writeValue(temporary.toFile(), capture);
        Files.move(temporary, OUTPUT, StandardCopyOption.ATOMIC_MOVE);
    }

    private static long collections() {
        return ManagementFactory.getGarbageCollectorMXBeans().stream()
            .mapToLong(collector -> collector.getCollectionCount()).filter(count -> count >= 0).sum();
    }

    private static MessageDigest sha256() {
        try {
            return MessageDigest.getInstance("SHA-256");
        } catch (NoSuchAlgorithmException exception) {
            throw new IllegalStateException("The reference JVM must support SHA-256.", exception);
        }
    }

    private static void respond(HttpExchange exchange, int code, String body) throws IOException {
        var bytes = body.getBytes(StandardCharsets.UTF_8);
        try (exchange) {
            exchange.sendResponseHeaders(code, bytes.length);
            exchange.getResponseBody().write(bytes);
        }
    }
}
