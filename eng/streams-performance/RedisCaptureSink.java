import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.List;
import java.util.Locale;
import redis.clients.jedis.Jedis;

/** Writes the same document, reconciliation index and checkpoint through native Jedis. */
final class RedisCaptureSink implements SyncCaptureReference {
    private static final byte[] PUBLISH = bytes("""
        local checkpoint = redis.call('HGET', KEYS[1], 'checkpoint')
        if checkpoint and checkpoint >= ARGV[1] then return 0 end

        local function publish(key, staged)
            redis.call('HSET', KEYS[3], key, string.sub(staged, 11))
            redis.call('ZADD', KEYS[4], string.sub(staged, 1, 10), key)
        end
        local staged = redis.call('HGETALL', KEYS[5])
        for index = 1, #staged, 2 do publish(staged[index], staged[index + 1]) end
        for index = 3, #ARGV, 2 do publish(ARGV[index], ARGV[index + 1]) end
        redis.call('SADD', KEYS[2], KEYS[3], KEYS[4])
        redis.call('HSET', KEYS[1], 'checkpoint', ARGV[1], 'transaction_id', ARGV[2])
        redis.call('HINCRBY', KEYS[1], 'generation', 1)
        redis.call('DEL', KEYS[5])
        return 1
        """);
    private static final byte[] STAGE = bytes("return redis.call('HSET', KEYS[1], unpack(ARGV))");
    private final Jedis redis = new Jedis("redis", 6379);
    private final MessageDigest sha = sha256();
    private final List<byte[]> keys;
    private final List<byte[]> pending = new ArrayList<>();
    private final String source = HexFormat.of().formatHex(sha.digest(bytes("debezium-redis-source")));
    private int ordinal;
    private int durableFragments;

    RedisCaptureSink() {
        String root = "bluetusk:sync:{" + fingerprint("sync_capture") + "}";
        String collection = fingerprint("streams_capture");
        keys = List.of(bytes(root + ":state"), bytes(root + ":collections"), bytes(root + ":collection:" + collection),
            bytes(root + ":collection-index:" + collection), bytes(root + ":staged"));
        redis.hset(root + ":state", "pipeline_id", "sync_capture");
        redis.hset(root + ":state", "generation", "0");
    }

    public void deliver(long key, long transactionId, long lsn, boolean last) {
        String text = Long.toString(key);
        byte[] rawKey = bytes(text);
        String identity = String.format(Locale.ROOT, "%s:%016x:%08x:%08x", source, lsn, transactionId, ordinal++);
        long score = Integer.toUnsignedLong(ByteBuffer.wrap(sha.digest(rawKey)).getInt());
        pending.add(rawKey);
        pending.add(encodeDocument(identity, text, score));
        if (!last) {
            return;
        }
        var arguments = new ArrayList<byte[]>(pending.size() + 2);
        arguments.add(bytes(String.format(Locale.ROOT, "%016x", lsn)));
        arguments.add(bytes(Long.toString(transactionId)));
        arguments.addAll(pending);
        if (!Long.valueOf(1).equals(redis.eval(PUBLISH, keys, arguments))) {
            throw new IllegalStateException("The native Redis sink did not advance its checkpoint.");
        }
        pending.clear();
        ordinal = 0;
    }

    public void beforeAcknowledge() {
        if (pending.isEmpty()) {
            return;
        }
        if (!Long.valueOf(pending.size() / 2).equals(redis.eval(STAGE, List.of(keys.get(4)), pending))) {
            throw new IllegalStateException("The native Redis sink did not persist the complete HTTP fragment.");
        }
        pending.clear();
        durableFragments++;
    }

    public int durableFragments() { return durableFragments; }

    public String implementation() { return "native Redis Jedis"; }

    private byte[] encodeDocument(String identity, String key, long score) {
        byte[] identityBytes = bytes(identity);
        byte[] contentType = bytes("application/json");
        byte[] content = bytes("{\"id\":" + key + "}");
        int bodyLength = 5 + 4 * Integer.BYTES + identityBytes.length + contentType.length + content.length;
        var buffer = ByteBuffer.allocate(10 + bodyLength + 32).order(ByteOrder.LITTLE_ENDIAN);
        buffer.put(bytes(String.format(Locale.ROOT, "%010d", score)));
        buffer.put(bytes("BTSD")).put((byte)1);
        buffer.putInt(identityBytes.length).put(identityBytes);
        buffer.putInt(contentType.length).put(contentType);
        buffer.putInt(-1);
        buffer.putInt(content.length).put(content);
        sha.update(buffer.array(), 10, bodyLength);
        buffer.put(sha.digest());
        return buffer.array();
    }

    private String fingerprint(String value) { return HexFormat.of().formatHex(sha.digest(bytes(value))); }

    private static byte[] bytes(String value) { return value.getBytes(StandardCharsets.UTF_8); }

    private static MessageDigest sha256() {
        try {
            return MessageDigest.getInstance("SHA-256");
        } catch (NoSuchAlgorithmException exception) {
            throw new IllegalStateException("The reference JVM must support SHA-256.", exception);
        }
    }
}
