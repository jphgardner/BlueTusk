import java.io.IOException;
import java.sql.SQLException;

interface SyncCaptureReference {
    void deliver(long key, long transactionId, long lsn, long commitMicros, boolean last) throws SQLException, IOException;
    void beforeAcknowledge() throws SQLException, IOException;
    int durableFragments();
    String implementation();
}
