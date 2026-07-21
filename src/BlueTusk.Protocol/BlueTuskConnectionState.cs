namespace BlueTusk.Protocol;

public enum BlueTuskConnectionState
{
    Disconnected,
    TransportConnected,
    EncryptionNegotiation,
    Startup,
    Authentication,
    Initialising,
    Ready,
    Executing,
    CopyIn,
    CopyOut,
    CopyBoth,
    Replication,
    FailedTransaction,
    Cancelling,
    Resetting,
    Closing,
}

