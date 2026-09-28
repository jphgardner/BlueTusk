namespace BlueTusk.Search;

public sealed record SearchTextChunk(int Ordinal, int StartCharacter, string Content);

/// <summary>Deterministic overlapping chunks preserving UTF-16 surrogate pairs. Version the options when replacing an index definition.</summary>
public static class SearchChunker
{
    public static IReadOnlyList<SearchTextChunk> Chunk(string content, int maxCharacters = 2048, int overlapCharacters = 128, int maxChunks = 2048)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapCharacters);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(overlapCharacters, maxCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChunks);
        var result = new List<SearchTextChunk>();
        var start = 0;
        do
        {
            if (result.Count >= maxChunks)
            {
                throw new ArgumentException("Content exceeds the configured maximum chunks.", nameof(content));
            }

            var end = Math.Min(content.Length, start + maxCharacters);
            if (end < content.Length && char.IsHighSurrogate(content[end - 1]) && char.IsLowSurrogate(content[end]))
            {
                end--;
            }

            result.Add(new SearchTextChunk(result.Count, start, content[start..end]));
            if (end == content.Length)
            {
                break;
            }

            var next = Math.Max(start + 1, end - overlapCharacters);
            if (char.IsLowSurrogate(content[next]) && char.IsHighSurrogate(content[next - 1]))
            {
                next++;
            }

            start = next;
        } while (start < content.Length);
        return result.AsReadOnly();
    }
}
