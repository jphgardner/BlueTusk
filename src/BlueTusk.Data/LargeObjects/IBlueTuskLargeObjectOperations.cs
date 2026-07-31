namespace BlueTusk.Data.LargeObjects;

internal interface IBlueTuskLargeObjectOperations
{
    byte[] Read(int count) =>
        throw new NotSupportedException("This large-object implementation does not provide synchronous I/O.");

    ValueTask<byte[]> ReadAsync(int count, CancellationToken cancellationToken);

    int Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException("This large-object implementation does not provide synchronous I/O.");

    ValueTask<int> WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("This large-object implementation does not provide synchronous I/O.");

    ValueTask<long> SeekAsync(long offset, SeekOrigin origin, CancellationToken cancellationToken);

    void SetLength(long value) =>
        throw new NotSupportedException("This large-object implementation does not provide synchronous I/O.");

    ValueTask SetLengthAsync(long value, CancellationToken cancellationToken);

    void Close(bool commit) =>
        throw new NotSupportedException("This large-object implementation does not provide synchronous I/O.");

    ValueTask CloseAsync(bool commit, CancellationToken cancellationToken);

    void Abandon();
}
