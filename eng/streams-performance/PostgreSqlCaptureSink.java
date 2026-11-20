import java.io.IOException;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.SQLException;
import java.util.Properties;

/** Native PostgreSQL baseline living in the same JVM as the Debezium Server HTTP client. */
final class PostgreSqlCaptureSink {
    private final Connection connection;
    private final PreparedStatement write;
    private final PreparedStatement checkpoint;

    PostgreSqlCaptureSink() throws IOException, SQLException {
        var properties = new Properties();
        try (var source = Files.newInputStream(Path.of("/debezium/config/application.properties"))) {
            properties.load(source);
        }
        var settings = new Properties();
        settings.setProperty("user", properties.getProperty("debezium.source.database.user"));
        settings.setProperty("password", properties.getProperty("debezium.source.database.password"));
        settings.setProperty("sslmode", "disable");
        settings.setProperty("reWriteBatchedInserts", "true");
        connection = DriverManager.getConnection("jdbc:postgresql://postgres:5432/bluetusk_sync_sink", settings);
        try (var statement = connection.createStatement()) {
            statement.execute("SET synchronous_commit TO on");
        }
        connection.setAutoCommit(false);
        write = connection.prepareStatement("""
            INSERT INTO bluetusk_sync.documents (pipeline_id, collection_name, document_key, content_type, content, source_change_id)
            VALUES ('sync_capture', 'streams_capture', ?, 'application/json', ?, ?)
            ON CONFLICT (pipeline_id, collection_name, document_key) DO UPDATE
            SET content = EXCLUDED.content, source_change_id = EXCLUDED.source_change_id, updated_at = clock_timestamp()
            """);
        checkpoint = connection.prepareStatement("""
            UPDATE bluetusk_sync.pipelines SET checkpoint_position = ?, checkpoint_transaction_id = ?, updated_at = clock_timestamp()
            WHERE pipeline_id = 'sync_capture' AND (checkpoint_position IS NULL OR checkpoint_position < ?)
            """);
    }

    void deliver(long key, long transactionId, long lsn, boolean last) throws SQLException {
        try {
            write.setString(1, Long.toString(key));
            write.setBytes(2, ("{\"id\":" + key + "}").getBytes(StandardCharsets.UTF_8));
            write.setString(3, transactionId + ":" + lsn + ":" + key);
            write.addBatch();
            if (last) {
                write.executeBatch();
                var position = BigDecimal.valueOf(lsn);
                checkpoint.setBigDecimal(1, position);
                checkpoint.setLong(2, transactionId);
                checkpoint.setBigDecimal(3, position);
                if (checkpoint.executeUpdate() != 1) {
                    throw new SQLException("The native PostgreSQL sink did not advance the checkpoint.");
                }
                connection.commit();
            }
        } catch (SQLException failure) {
            try {
                connection.rollback();
            } catch (SQLException rollback) {
                failure.addSuppressed(rollback);
            }
            throw failure;
        }
    }
}
