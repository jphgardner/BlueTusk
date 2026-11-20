import java.io.IOException;
import java.math.BigDecimal;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.SQLException;
import java.util.ArrayList;
import java.util.Properties;

/** Native PostgreSQL baseline living in the same JVM as the Debezium Server HTTP client. */
final class PostgreSqlCaptureSink implements SyncCaptureReference {
    private final Connection connection;
    private final PreparedStatement write;
    private final PreparedStatement checkpoint;
    private final PreparedStatement stage;
    private final PreparedStatement publishStaged;
    private final PreparedStatement clearStaged;
    private final ArrayList<Row> pending = new ArrayList<>();
    private int stagedRows;
    private int durableFragments;

    private record Row(long key, long transactionId, long lsn) { }

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
        stage = connection.prepareStatement("INSERT INTO bluetusk_sync.staged_rows (source_transaction_id, document_id, lsn) VALUES (?, ?, ?)");
        publishStaged = connection.prepareStatement("""
            INSERT INTO bluetusk_sync.documents (pipeline_id, collection_name, document_key, content_type, content, source_change_id)
            SELECT 'sync_capture', 'streams_capture', document_id::text, 'application/json',
                   convert_to('{"id":' || document_id::text || '}', 'UTF8'), source_transaction_id::text || ':' || lsn::text || ':' || document_id::text
            FROM bluetusk_sync.staged_rows WHERE source_transaction_id = ? ORDER BY document_id
            ON CONFLICT (pipeline_id, collection_name, document_key) DO UPDATE
            SET content = EXCLUDED.content, source_change_id = EXCLUDED.source_change_id, updated_at = clock_timestamp()
            """);
        clearStaged = connection.prepareStatement("DELETE FROM bluetusk_sync.staged_rows WHERE source_transaction_id = ?");
    }

    public void deliver(long key, long transactionId, long lsn, long commitMicros, boolean last) throws SQLException {
        pending.add(new Row(key, transactionId, lsn));
        if (!last) {
            return;
        }
        try {
            if (stagedRows > 0) {
                publishStaged.setLong(1, transactionId);
                clearStaged.setLong(1, transactionId);
                if (publishStaged.executeUpdate() != stagedRows || clearStaged.executeUpdate() != stagedRows) {
                    throw new SQLException("The native PostgreSQL sink lost part of the staged transaction.");
                }
            }
            for (var row : pending) {
                write.setString(1, Long.toString(row.key()));
                write.setBytes(2, ("{\"id\":" + row.key() + "}").getBytes(StandardCharsets.UTF_8));
                write.setString(3, row.transactionId() + ":" + row.lsn() + ":" + row.key());
                write.addBatch();
            }
            write.executeBatch();
            var position = BigDecimal.valueOf(lsn);
            checkpoint.setBigDecimal(1, position);
            checkpoint.setLong(2, transactionId);
            checkpoint.setBigDecimal(3, position);
            if (checkpoint.executeUpdate() != 1) {
                throw new SQLException("The native PostgreSQL sink did not advance the checkpoint.");
            }
            connection.commit();
            pending.clear();
            stagedRows = 0;
        } catch (SQLException failure) {
            rollback(failure);
            throw failure;
        }
    }

    public void beforeAcknowledge() throws SQLException {
        if (pending.isEmpty()) {
            return;
        }
        // An HTTP batch can stop mid-transaction. Retain those rows durably before
        // the HTTP response can advance Debezium's offsets. Publish them at commit.
        try {
            for (var row : pending) {
                stage.setLong(1, row.transactionId());
                stage.setLong(2, row.key());
                stage.setLong(3, row.lsn());
                stage.addBatch();
            }
            stage.executeBatch();
            connection.commit();
            stagedRows += pending.size();
            pending.clear();
            durableFragments++;
        } catch (SQLException failure) {
            rollback(failure);
            throw failure;
        }
    }

    public int durableFragments() {
        return durableFragments;
    }

    public String implementation() {
        return "native PostgreSQL JDBC";
    }

    private void rollback(SQLException failure) {
        try {
            connection.rollback();
        } catch (SQLException rollback) {
            failure.addSuppressed(rollback);
        }
    }
}
