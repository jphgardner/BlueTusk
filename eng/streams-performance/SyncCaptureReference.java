import java.sql.SQLException;

interface SyncCaptureReference {
    void deliver(long key, long transactionId, long lsn, boolean last) throws SQLException;
    void beforeAcknowledge() throws SQLException;
    int durableFragments();
    String implementation();
}
