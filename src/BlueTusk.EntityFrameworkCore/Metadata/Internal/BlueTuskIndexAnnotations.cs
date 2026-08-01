using System.Text.Json;

namespace BlueTusk.EntityFrameworkCore.Metadata.Internal;

internal static class BlueTuskIndexAnnotations
{
    public const string Prefix = "BlueTusk:Index";
    public const string Method = Prefix + "Method";
    public const string OperatorClasses = Prefix + "OperatorClasses";
    public const string Collations = Prefix + "Collations";
    public const string NullSortOrders = Prefix + "NullSortOrders";
    public const string IncludeProperties = Prefix + "IncludeProperties";
    public const string StorageParameters = Prefix + "StorageParameters";
    public const string IsConcurrent = Prefix + "Concurrent";
    public const string NullsDistinct = Prefix + "NullsDistinct";
    public const string Expressions = Prefix + "Expressions";

    public static string SerializeStorageParameters(IReadOnlyDictionary<string, string> parameters) =>
        JsonSerializer.Serialize(
            parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    public static IReadOnlyDictionary<string, string> DeserializeStorageParameters(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(value)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
}
