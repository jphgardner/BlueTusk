namespace BlueTusk.Protocol;

internal interface IBlueTuskProtocolOperation
{
    BlueTuskConnectionState State { get; }

    ValueTask<BlueTuskOperationResult> HandleAsync(
        BlueTuskBackendMessage message,
        CancellationToken cancellationToken);
}

internal readonly record struct BlueTuskOperationResult(bool IsComplete);

