namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskTextSearchCodecTests
{
    private const string VectorText = "'a':1A,2B 'b':3";
    private const string QueryText = "'fat':AB & ( 'rat' | !'cat':* )";

    [Fact]
    public void Text_search_vector_matches_postgresql_binary_layout()
    {
        var value = BlueTuskTextSearchVector.Parse(VectorText);
        var codec = new BlueTuskTextSearchVectorCodec();
        var bytes = Write(codec, BlueTuskBuiltInTypes.TextSearchVector, value, BlueTuskDataFormat.Binary);

        Assert.Equal(VectorText, value.ToString());
        Assert.Equal(
            "0000000261000002C0018002620000010003",
            Convert.ToHexString(bytes));
        AssertRoundTrip(codec, BlueTuskBuiltInTypes.TextSearchVector, value);
    }

    [Fact]
    public void Text_search_query_matches_postgresql_prefix_tree_layout()
    {
        var value = BlueTuskTextSearchQuery.Parse(QueryText);
        var codec = new BlueTuskTextSearchQueryCodec();
        var bytes = Write(codec, BlueTuskBuiltInTypes.TextSearchQuery, value, BlueTuskDataFormat.Binary);

        Assert.Equal(
            "000000060202020302010100016361740001000072617400010C0066617400",
            Convert.ToHexString(bytes));
        AssertRoundTrip(codec, BlueTuskBuiltInTypes.TextSearchQuery, value);
    }

    [Fact]
    public void Phrase_distance_and_empty_values_round_trip()
    {
        var phrase = BlueTuskTextSearchQuery.Parse("a <3> b");
        var emptyVector = new BlueTuskTextSearchVector([]);
        var queryCodec = new BlueTuskTextSearchQueryCodec();
        var vectorCodec = new BlueTuskTextSearchVectorCodec();

        Assert.Equal(
            "000000030204000301000062000100006100",
            Convert.ToHexString(Write(
                queryCodec,
                BlueTuskBuiltInTypes.TextSearchQuery,
                phrase,
                BlueTuskDataFormat.Binary)));
        AssertRoundTrip(queryCodec, BlueTuskBuiltInTypes.TextSearchQuery, phrase);
        AssertRoundTrip(queryCodec, BlueTuskBuiltInTypes.TextSearchQuery, BlueTuskTextSearchQuery.Empty);
        AssertRoundTrip(vectorCodec, BlueTuskBuiltInTypes.TextSearchVector, emptyVector);
    }

    [Fact]
    public void Vector_normalizes_entry_order_duplicate_lexemes_and_positions()
    {
        var vector = new BlueTuskTextSearchVector(
        [
            new BlueTuskTextSearchVectorEntry(
                "z",
                [new BlueTuskTextSearchPosition(2), new BlueTuskTextSearchPosition(2, BlueTuskTextSearchWeight.A)]),
            new BlueTuskTextSearchVectorEntry("a", [new BlueTuskTextSearchPosition(1)]),
            new BlueTuskTextSearchVectorEntry("z", [new BlueTuskTextSearchPosition(3, BlueTuskTextSearchWeight.B)]),
        ]);

        Assert.Equal("'a':1 'z':2A,3B", vector.ToString());
        Assert.Equal(2, vector.Count);
    }

    [Fact]
    public void Quoted_lexemes_preserve_quotes_and_backslashes()
    {
        var vector = BlueTuskTextSearchVector.Parse("'Joe''s':1A 'a\\\\b':2");
        var query = BlueTuskTextSearchQuery.Parse("'Joe''s':*A & 'a\\\\b'");

        Assert.Equal("Joe's", vector[0].Lexeme);
        Assert.Equal("a\\b", vector[1].Lexeme);
        AssertRoundTrip(
            new BlueTuskTextSearchVectorCodec(),
            BlueTuskBuiltInTypes.TextSearchVector,
            vector);
        AssertRoundTrip(
            new BlueTuskTextSearchQueryCodec(),
            BlueTuskBuiltInTypes.TextSearchQuery,
            query);
    }

    [Fact]
    public void Invalid_values_and_wire_trees_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlueTuskTextSearchPosition(0));
        Assert.Throws<ArgumentException>(() => new BlueTuskTextSearchVectorEntry(string.Empty));
        Assert.Throws<FormatException>(() => BlueTuskTextSearchQuery.Parse("a &"));

        Assert.Throws<InvalidOperationException>(() => ReadQuery(Convert.FromHexString("000000010202")));
        Assert.Throws<InvalidOperationException>(() => ReadVector(Convert.FromHexString("00000001610000010000")));
    }

    private static void AssertRoundTrip<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value)
    {
        foreach (var format in new[] { BlueTuskDataFormat.Text, BlueTuskDataFormat.Binary })
        {
            var bytes = Write(codec, type, value, format);
            var reader = new BlueTuskReader(bytes);
            Assert.Equal(value, codec.ReadTyped(ref reader, format, type));
            Assert.Equal(0, reader.Remaining);
        }
    }

    private static byte[] Write<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        BlueTuskDataFormat format)
    {
        var destination = new byte[4096];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, format, type);
        return destination.AsSpan(0, writer.WrittenCount).ToArray();
    }

    private static BlueTuskTextSearchQuery ReadQuery(byte[] bytes)
    {
        var reader = new BlueTuskReader(bytes);
        return new BlueTuskTextSearchQueryCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.TextSearchQuery);
    }

    private static BlueTuskTextSearchVector ReadVector(byte[] bytes)
    {
        var reader = new BlueTuskReader(bytes);
        return new BlueTuskTextSearchVectorCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            BlueTuskBuiltInTypes.TextSearchVector);
    }
}
