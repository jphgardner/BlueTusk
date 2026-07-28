namespace BlueTusk.Data.Copy;

public enum BlueTuskCopyDataFormat
{
    Text,
    Binary,
}

public sealed record BlueTuskRawCopyResult(
    BlueTuskCopyDataFormat Format,
    IReadOnlyList<BlueTuskCopyDataFormat> ColumnFormats,
    long RowsAffected,
    long BytesTransferred);
