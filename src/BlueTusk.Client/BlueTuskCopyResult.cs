using BlueTusk.Protocol;

namespace BlueTusk.Client;

public sealed record BlueTuskCopyResult(
    BlueTuskCopyResponse Response,
    string CommandTag,
    long BytesTransferred);
