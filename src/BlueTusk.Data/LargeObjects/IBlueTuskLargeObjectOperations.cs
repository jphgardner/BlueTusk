namespace BlueTusk.Data.LargeObjects;

internal interface IBlueTuskLargeObjectOperations
{
    ValueTask<byte[]> ReadAsync(int count, CancellationToken cancellationToken);

    ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    ValueTask<long> SeekAsync(long offset, SeekOrigin origin, CancellationToken cancellationToken);

    ValueTask SetLengthAsync(long value, CancellationToken cancellationToken);

    ValueTask CloseAsync(bool commit, CancellationToken cancellationToken);

    void Abandon();
}
