using System.Buffers;
using System.Text;
using System.Text.Json;

namespace BlueTusk.Schema;

internal static class SchemaJsonAdmission
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static int DecodedByteLength(ref Utf8JsonReader reader, long maximum)
    {
        if (maximum < 0) { throw new SchemaCaptureLimitException(); }
        if (!reader.ValueIsEscaped)
        {
            if (reader.ValueSpan.Length > maximum) { throw new SchemaCaptureLimitException(); }
            try { _ = StrictUtf8.GetCharCount(reader.ValueSpan); }
            catch (DecoderFallbackException error) { throw new JsonException("Catalogue strings must contain valid UTF-8.", error); }
            return reader.ValueSpan.Length;
        }
        var capacity = checked((int)Math.Min(reader.ValueSpan.Length, maximum));
        if (capacity == 0) { throw new SchemaCaptureLimitException(); }
        var rented = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            try { return reader.CopyString(rented.AsSpan(0, capacity)); }
            catch (ArgumentException) { throw new SchemaCaptureLimitException(); }
            catch (InvalidOperationException error) { throw new JsonException("Catalogue strings must contain valid Unicode.", error); }
        }
        finally { ArrayPool<byte>.Shared.Return(rented, clearArray: true); }
    }
}
