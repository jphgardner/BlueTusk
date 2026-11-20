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
import java.time.Instant;
import java.util.Map;

/** Counts actual HTTP deliveries inside the pinned Debezium JVM. */
public final class StreamsHttpProbe {
    private static final String VERSION = "3.6.1.Final";
    private static final Path OUTPUT = Path.of("/tmp/streams-reference.json");
    private static final ObjectMapper JSON = new ObjectMapper();
    private static final long TICKS_PER_SECOND = 10_000_000;
    private static final int MAX_HTTP_BATCH_BYTES = 2 * 1024 * 1024;

    private final int changes;
    private final int warmupRows;
    private final int measuredRows;
    private final int trial;
    private final String commit;
    private final String image;
    private final String hostOs;
    private final long[] ticks;
    private final long[] commitMicros;
    private final long[] transactionIds;
    private final ThreadMXBean threads = (ThreadMXBean) ManagementFactory.getThreadMXBean();
    private final OperatingSystemMXBean process = (OperatingSystemMXBean) ManagementFactory.getOperatingSystemMXBean();
    private int delivered;
    private long started;
    private long allocationBefore;
    private long cpuBefore;
    private long gcBefore;
    private boolean complete;
    private boolean failed;

    public static void premain(String arguments) throws IOException {
        var probe = new StreamsHttpProbe(arguments);
        var server = HttpServer.create(new InetSocketAddress("127.0.0.1", 8081), 32);
        server.createContext("/events", probe::receive);
        server.createContext("/progress", probe::progress);
        server.createContext("/measure", probe::begin);
        // One HTTP handler thread keeps the delivery order and measurement boundaries consistent.
        server.start();
    }

    private StreamsHttpProbe(String arguments) {
        var fields = arguments.split(",", -1);
        if (fields.length != 7 || !fields[4].matches("[0-9a-f]{40}") ||
            !fields[5].matches("postgres:[^@\\s]+@sha256:[0-9a-f]{64}") ||
            !fields[6].matches("windows|linux") || !System.getProperty("os.arch").equals("amd64")) {
            throw new IllegalArgumentException("An exact x64 benchmark identity is required.");
        }
        changes = Integer.parseInt(fields[0]);
        warmupRows = Math.multiplyExact(changes, Integer.parseInt(fields[1]));
        var transactions = Integer.parseInt(fields[2]);
        measuredRows = Math.multiplyExact(changes, transactions);
        trial = Integer.parseInt(fields[3]);
        if ((changes != 1 && changes != 1_000) || warmupRows < 1 || warmupRows > 10_000_000 ||
            transactions < 1 || transactions > 16_000 || measuredRows > 1_048_576 || trial < 0 || trial >= 50) {
            throw new IllegalArgumentException("The Streams capture exceeds its workload bounds.");
        }
        commit = fields[4];
        image = fields[5];
        hostOs = fields[6];
        ticks = new long[measuredRows];
        commitMicros = new long[transactions];
        transactionIds = new long[transactions];
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
        allocationBefore = threads.getTotalThreadAllocatedBytes();
        cpuBefore = process.getProcessCpuTime();
        gcBefore = collections();
        started = System.nanoTime();
        respond(exchange, 200, "measuring");
    }

    private void receive(HttpExchange exchange) throws IOException {
        var received = Instant.now();
        try {
            if (!exchange.getRequestMethod().equals("POST") || failed || complete) {
                throw new IllegalStateException("Unexpected delivery request.");
            }
            var bytes = exchange.getRequestBody().readNBytes(MAX_HTTP_BATCH_BYTES + 1);
            if (bytes.length > MAX_HTTP_BATCH_BYTES) {
                throw new IllegalStateException("The delivery exceeded the fixture byte bound.");
            }
            var body = JSON.readTree(bytes);
            if (body.isArray()) {
                if (body.isEmpty() || body.size() > 1_000) {
                    throw new IllegalStateException("The HTTP batch has an unexpected row count.");
                }
                for (var event : body) {
                    recordEvent(event, received);
                }
            } else {
                recordEvent(body, received);
            }
            if (delivered == warmupRows + measuredRows) {
                writeResult();
                complete = true;
            }
            respond(exchange, 200, "accepted");
        } catch (IOException | RuntimeException exception) {
            failed = true;
            System.err.println("Streams benchmark probe: " + exception.getMessage());
            respond(exchange, 409, "The Streams benchmark rejected the delivery.");
        }
    }

    private void recordEvent(JsonNode event, Instant received) {
        var source = event.required("source");
        var row = event.required("after").required("id");
        if (delivered >= warmupRows + measuredRows || !row.isIntegralNumber() || row.longValue() != (long) delivered + 1 ||
            !event.required("op").asText().equals("c") || !source.required("version").asText().equals(VERSION) ||
            !source.required("schema").asText().equals("public") || !source.required("table").asText().equals("streams_capture")) {
            throw new IllegalStateException("Debezium delivered an unexpected row, operation, version or table.");
        }
        if (delivered >= warmupRows) {
            if (started == 0) {
                throw new IllegalStateException("A measured row arrived before the measurement marker.");
            }
            recordDelivery(source, received);
        }
        delivered++;
    }

    private void recordDelivery(JsonNode source, Instant received) {
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
        long receiveTicks = received.getEpochSecond() * TICKS_PER_SECOND + received.getNano() / 100;
        long latency = receiveTicks - timestamp.longValue() * 10;
        if (latency <= 0) {
            throw new IllegalStateException("The reference and PostgreSQL clocks must agree for commit-to-delivery capture.");
        }
        ticks[sample] = latency;
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
        capture.put("schemaVersion", 1).put("evidenceKind", "bluetusk-performance-trial").put("family", "Streams")
            .put("workloadKey", hostOs + "|Streams|changes=" + changes + "|scenario=commit-to-delivery")
            .put("role", "reference").put("trial", trial).put("sourceCommit", commit).put("diagnostic", true)
            .put("synthetic", false).put("implementation", "Debezium Server " + VERSION).put("frequency", TICKS_PER_SECOND);
        capture.set("environment", JSON.valueToTree(environment));
        capture.set("measurement", JSON.valueToTree(measurement));
        var temporary = OUTPUT.resolveSibling(OUTPUT.getFileName() + ".tmp");
        JSON.writeValue(temporary.toFile(), capture);
        Files.move(temporary, OUTPUT, StandardCopyOption.ATOMIC_MOVE);
    }

    private static long collections() {
        return ManagementFactory.getGarbageCollectorMXBeans().stream()
            .mapToLong(collector -> collector.getCollectionCount()).filter(count -> count >= 0).sum();
    }

    private static void respond(HttpExchange exchange, int code, String body) throws IOException {
        var bytes = body.getBytes(StandardCharsets.UTF_8);
        try (exchange) {
            exchange.sendResponseHeaders(code, bytes.length);
            exchange.getResponseBody().write(bytes);
        }
    }
}
