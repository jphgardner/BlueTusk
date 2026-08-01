using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PostgreSqlFunctionTranslationTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Array_range_and_multirange_functions_translate()
    {
        using var context = CreateContext();

        var sql = context.Values
            .Select(value => new
            {
                Length = EF.Functions.ArrayLength(value.Numbers, 1),
                LowerBound = EF.Functions.ArrayLowerBound(value.Numbers, 1),
                UpperBound = EF.Functions.ArrayUpperBound(value.Numbers, 1),
                Cardinality = EF.Functions.ArrayCardinality(value.Numbers),
                RangeLower = EF.Functions.RangeLower(value.Range),
                RangeUpper = EF.Functions.RangeUpper(value.Range),
                RangeEmpty = EF.Functions.RangeIsEmpty(value.Range),
                RangeLowerInclusive = EF.Functions.RangeIsLowerInclusive(value.Range),
                RangeUpperInclusive = EF.Functions.RangeIsUpperInclusive(value.Range),
                RangeLowerInfinite = EF.Functions.RangeIsLowerInfinite(value.Range),
                RangeUpperInfinite = EF.Functions.RangeIsUpperInfinite(value.Range),
                MultirangeLower = EF.Functions.MultirangeLower(value.Multirange),
                MultirangeUpper = EF.Functions.MultirangeUpper(value.Multirange),
                MultirangeEmpty = EF.Functions.MultirangeIsEmpty(value.Multirange),
                MultirangeLowerInclusive = EF.Functions.MultirangeIsLowerInclusive(value.Multirange),
                MultirangeUpperInclusive = EF.Functions.MultirangeIsUpperInclusive(value.Multirange),
                MultirangeLowerInfinite = EF.Functions.MultirangeIsLowerInfinite(value.Multirange),
                MultirangeUpperInfinite = EF.Functions.MultirangeIsUpperInfinite(value.Multirange),
            })
            .ToQueryString();

        Assert.Contains("array_length(", sql, StringComparison.Ordinal);
        Assert.Contains("array_lower(", sql, StringComparison.Ordinal);
        Assert.Contains("array_upper(", sql, StringComparison.Ordinal);
        Assert.Contains("cardinality(", sql, StringComparison.Ordinal);
        Assert.Contains("lower(", sql, StringComparison.Ordinal);
        Assert.Contains("upper(", sql, StringComparison.Ordinal);
        Assert.Contains("isempty(", sql, StringComparison.Ordinal);
        Assert.Contains("lower_inc(", sql, StringComparison.Ordinal);
        Assert.Contains("upper_inc(", sql, StringComparison.Ordinal);
        Assert.Contains("lower_inf(", sql, StringComparison.Ordinal);
        Assert.Contains("upper_inf(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_regex_and_network_functions_translate_with_store_specific_results()
    {
        using var context = CreateContext();
        var path = new BlueTuskJsonPath("$.version");

        var sql = context.Values
            .Select(value => new
            {
                JsonType = EF.Functions.JsonTypeOf(value.Json),
                JsonLength = EF.Functions.JsonArrayLength(value.JsonArray),
                JsonValue = EF.Functions.JsonPathQueryFirst(value.Json, path),
                Replaced = EF.Functions.RegexReplace(value.Text, "provider", "driver"),
                Matches = EF.Functions.RegexCount(value.Text, "[A-Z]"),
                Host = EF.Functions.NetworkHost(value.Network),
                Family = EF.Functions.NetworkAddressFamily(value.Network),
                MaskLength = EF.Functions.NetworkMaskLength(value.Network),
                Network = EF.Functions.NetworkPart(value.Network),
                Broadcast = EF.Functions.NetworkBroadcast(value.Network),
            })
            .ToQueryString();

        Assert.Contains("jsonb_typeof(", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_array_length(", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_path_query_first(", sql, StringComparison.Ordinal);
        Assert.Contains("regexp_replace(", sql, StringComparison.Ordinal);
        Assert.Contains("regexp_count(", sql, StringComparison.Ordinal);
        Assert.Contains("host(", sql, StringComparison.Ordinal);
        Assert.Contains("family(", sql, StringComparison.Ordinal);
        Assert.Contains("masklen(", sql, StringComparison.Ordinal);
        Assert.Contains("network(", sql, StringComparison.Ordinal);
        Assert.Contains("broadcast(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_text_construction_and_ranking_functions_translate_composably()
    {
        using var context = CreateContext();
        var query = "PostgreSQL provider";

        var sql = context.Values
            .Where(value => EF.Functions.FullTextMatches(
                EF.Functions.ToTextSearchVector(value.Text),
                EF.Functions.PlainToTextSearchQuery(query)))
            .Select(value => new
            {
                VectorLength = EF.Functions.TextSearchVectorLength(
                    EF.Functions.ToTextSearchVector(value.Text)),
                RawNodes = EF.Functions.TextSearchQueryNodeCount(
                    EF.Functions.ToTextSearchQuery("PostgreSQL & provider")),
                PlainNodes = EF.Functions.TextSearchQueryNodeCount(
                    EF.Functions.PlainToTextSearchQuery(query)),
                PhraseNodes = EF.Functions.TextSearchQueryNodeCount(
                    EF.Functions.PhraseToTextSearchQuery(query)),
                WebNodes = EF.Functions.TextSearchQueryNodeCount(
                    EF.Functions.WebSearchToTextSearchQuery(query)),
                Rank = EF.Functions.TextSearchRank(
                    EF.Functions.ToTextSearchVector(value.Text),
                    EF.Functions.PlainToTextSearchQuery(query)),
            })
            .ToQueryString();

        Assert.Contains("to_tsvector(", sql, StringComparison.Ordinal);
        Assert.Contains("to_tsquery(", sql, StringComparison.Ordinal);
        Assert.Contains("plainto_tsquery(", sql, StringComparison.Ordinal);
        Assert.Contains("phraseto_tsquery(", sql, StringComparison.Ordinal);
        Assert.Contains("websearch_to_tsquery(", sql, StringComparison.Ordinal);
        Assert.Contains("length(", sql, StringComparison.Ordinal);
        Assert.Contains("numnode(", sql, StringComparison.Ordinal);
        Assert.Contains("ts_rank(", sql, StringComparison.Ordinal);
        Assert.Contains(" @@ ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Date_time_functions_translate_with_typed_timestamp_and_interval_results()
    {
        using var context = CreateContext();
        var timestamp = new DateTime(2026, 8, 1, 12, 34, 56, DateTimeKind.Unspecified);
        var timestampWithTimeZone = new DateTimeOffset(2026, 8, 1, 12, 34, 56, TimeSpan.Zero);
        var interval = new BlueTuskInterval(14, 3, TimeSpan.FromMinutes(47).Ticks / 10);

        var sql = context.Values
            .Select(_ => new
            {
                Hour = EF.Functions.DatePart("hour", timestamp),
                OffsetHour = EF.Functions.DatePart("hour", timestampWithTimeZone),
                IntervalMonth = EF.Functions.DatePart("month", interval),
                Truncated = EF.Functions.DateTrunc("day", timestamp),
                OffsetTruncated = EF.Functions.DateTrunc(
                    "day",
                    timestampWithTimeZone,
                    "Europe/London"),
                IntervalTruncated = EF.Functions.DateTrunc("hour", interval),
                Binned = EF.Functions.DateBin(
                    TimeSpan.FromMinutes(15),
                    timestamp,
                    new DateTime(2026, 8, 1)),
                Age = EF.Functions.DateAge(timestamp, new DateTime(2026, 1, 1)),
                Date = EF.Functions.MakeDate(2026, 8, 1),
                Time = EF.Functions.MakeTime(12, 34, 56.5),
                Timestamp = EF.Functions.MakeTimestamp(2026, 8, 1, 12, 34, 56.5),
                TimestampWithTimeZone = EF.Functions.MakeTimestampWithTimeZone(
                    2026,
                    8,
                    1,
                    12,
                    34,
                    56.5,
                    "UTC"),
                Interval = EF.Functions.MakeInterval(1, 2, 1, 3, 4, 5, 6.5),
                JustifiedDays = EF.Functions.JustifyDays(interval),
                JustifiedHours = EF.Functions.JustifyHours(interval),
                Justified = EF.Functions.JustifyInterval(interval),
            })
            .ToQueryString();

        Assert.Contains("date_part(", sql, StringComparison.Ordinal);
        Assert.Contains("date_trunc(", sql, StringComparison.Ordinal);
        Assert.Contains("date_bin(", sql, StringComparison.Ordinal);
        Assert.Contains("age(", sql, StringComparison.Ordinal);
        Assert.Contains("make_date(", sql, StringComparison.Ordinal);
        Assert.Contains("make_time(", sql, StringComparison.Ordinal);
        Assert.Contains("make_timestamp(", sql, StringComparison.Ordinal);
        Assert.Contains("make_timestamptz(", sql, StringComparison.Ordinal);
        Assert.Contains("make_interval(", sql, StringComparison.Ordinal);
        Assert.Contains("justify_days(", sql, StringComparison.Ordinal);
        Assert.Contains("justify_hours(", sql, StringComparison.Ordinal);
        Assert.Contains("justify_interval(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Geometric_functions_translate_for_every_supported_result_family()
    {
        using var context = CreateContext();
        var box = new BlueTuskBox(new BlueTuskPoint(0, 0), new BlueTuskPoint(4, 3));
        var circle = new BlueTuskCircle(new BlueTuskPoint(1, 2), 2);
        var segment = new BlueTuskLineSegment(new BlueTuskPoint(0, 0), new BlueTuskPoint(3, 4));
        var closedPath = new BlueTuskPath(
            [new BlueTuskPoint(0, 0), new BlueTuskPoint(3, 0), new BlueTuskPoint(0, 4)],
            isClosed: true);
        var polygon = new BlueTuskPolygon(
            [new BlueTuskPoint(0, 0), new BlueTuskPoint(3, 0), new BlueTuskPoint(0, 4)]);

        var sql = context.Values
            .Select(_ => new
            {
                BoxArea = EF.Functions.GeometryArea(box),
                PathArea = EF.Functions.GeometryArea(closedPath),
                CircleArea = EF.Functions.GeometryArea(circle),
                BoxCenter = EF.Functions.GeometryCenter(box),
                CircleCenter = EF.Functions.GeometryCenter(circle),
                Diagonal = EF.Functions.BoxDiagonal(box),
                Diameter = EF.Functions.CircleDiameter(circle),
                Height = EF.Functions.BoxHeight(box),
                Closed = EF.Functions.PathIsClosed(closedPath),
                Open = EF.Functions.PathIsOpen(closedPath),
                SegmentLength = EF.Functions.GeometryLength(segment),
                PathLength = EF.Functions.GeometryLength(closedPath),
                PathPoints = EF.Functions.GeometryPointCount(closedPath),
                PolygonPoints = EF.Functions.GeometryPointCount(polygon),
                ClosedPath = EF.Functions.PathClose(closedPath),
                OpenPath = EF.Functions.PathOpen(closedPath),
                Radius = EF.Functions.CircleRadius(circle),
                Slope = EF.Functions.PointSlope(
                    new BlueTuskPoint(0, 0),
                    new BlueTuskPoint(2, 1)),
                Width = EF.Functions.BoxWidth(box),
            })
            .ToQueryString();

        foreach (var function in new[]
        {
            "area(",
            "center(",
            "diagonal(",
            "diameter(",
            "height(",
            "isclosed(",
            "isopen(",
            "length(",
            "npoints(",
            "pclose(",
            "popen(",
            "radius(",
            "slope(",
            "width(",
        })
        {
            Assert.Contains(function, sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Array_string_binary_numeric_and_formatting_functions_translate()
    {
        using var context = CreateContext();
        var bytes = new byte[] { 0x01, 0x02, 0x03 };
        var bits = new BlueTuskBitString("1010");
        var timestamp = new DateTime(2026, 8, 1, 12, 34, 56, DateTimeKind.Unspecified);
        var interval = BlueTuskInterval.Parse("2 hours");
        var thresholds = new[] { 0, 10, 20 };

        var sql = context.Values
            .Select(value => new
            {
                Dimensions = EF.Functions.ArrayDimensions(value.Numbers),
                DimensionCount = EF.Functions.ArrayDimensionCount(value.Numbers),
                Position = EF.Functions.ArrayPosition(value.Numbers, 2),
                PositionFrom = EF.Functions.ArrayPosition(value.Numbers, 2, 2),
                Positions = EF.Functions.ArrayPositions(value.Numbers, 2),
                Removed = EF.Functions.ArrayRemove(value.Numbers, 2),
                Replaced = EF.Functions.ArrayReplace(value.Numbers, 2, 9),
                Reversed = EF.Functions.ArrayReverse(value.Numbers),
                Shuffled = EF.Functions.ArrayShuffle(value.Numbers),
                Sampled = EF.Functions.ArraySample(value.Numbers, 2),
                Trimmed = EF.Functions.ArrayTrim(value.Numbers, 1),
                Joined = EF.Functions.ArrayToString(value.Numbers, ","),
                JoinedWithNull = EF.Functions.ArrayToString(value.Numbers, ",", "NULL"),
                Split = EF.Functions.StringToArray(value.Text, " "),
                SplitWithNull = EF.Functions.StringToArray(value.Text, " ", "NULL"),
                Ascii = EF.Functions.StringAscii(value.Text),
                Character = EF.Functions.StringCharacter(65),
                TextBits = EF.Functions.BitLength(value.Text),
                BinaryBits = EF.Functions.BitLength(bytes),
                ValueBits = EF.Functions.BitLength(bits),
                TextBytes = EF.Functions.ByteLength(value.Text),
                BinaryBytes = EF.Functions.ByteLength(bytes),
                InitialCapital = EF.Functions.StringInitialCapital(value.Text),
                Left = EF.Functions.StringLeft(value.Text, 4),
                Right = EF.Functions.StringRight(value.Text, 8),
                LeftPad = EF.Functions.StringPadLeft(value.Text, 32, "."),
                RightPad = EF.Functions.StringPadRight(value.Text, 32),
                LeftTrim = EF.Functions.StringTrimLeft(value.Text, "B"),
                RightTrim = EF.Functions.StringTrimRight(value.Text),
                Trim = EF.Functions.StringTrim(value.Text),
                TextMd5 = EF.Functions.Md5(value.Text),
                BinaryMd5 = EF.Functions.Md5(bytes),
                Identifier = EF.Functions.ParseIdentifier("public.table"),
                QuotedIdentifier = EF.Functions.QuoteIdentifier("Mixed Name"),
                QuotedLiteral = EF.Functions.QuoteLiteral(value.Text),
                QuotedNullable = EF.Functions.QuoteNullableLiteral(value.Text),
                Repeated = EF.Functions.StringRepeat(value.Text, 2),
                ReversedText = EF.Functions.StringReverse(value.Text),
                Part = EF.Functions.StringSplitPart(value.Text, " ", 2),
                StartsWith = EF.Functions.StringStartsWith(value.Text, "Blue"),
                Translated = EF.Functions.StringTranslate(value.Text, "BT", "bt"),
                Encoded = EF.Functions.BinaryEncode(bytes, "hex"),
                Decoded = EF.Functions.BinaryDecode("010203", "hex"),
                Byte = EF.Functions.BinaryGetByte(bytes, 1),
                SetByte = EF.Functions.BinarySetByte(bytes, 1, 9),
                Bit = EF.Functions.BinaryGetBit(bytes, 0),
                SetBit = EF.Functions.BinarySetBit(bytes, 0, 1),
                TrimmedBytes = EF.Functions.BinaryTrim(bytes, new byte[] { 0x01 }),
                ReversedBytes = EF.Functions.BinaryReverse(bytes),
                CubeRoot = EF.Functions.CubeRoot(27),
                Degrees = EF.Functions.Degrees(Math.PI),
                Radians = EF.Functions.Radians(180),
                Division = EF.Functions.NumericDivide(7, 2),
                Factorial = EF.Functions.Factorial(5),
                Gcd = EF.Functions.GreatestCommonDivisor(12, 18),
                Lcm = EF.Functions.LeastCommonMultiple(12, 18),
                MinimumScale = EF.Functions.NumericMinimumScale(1.2300m),
                Scale = EF.Functions.NumericScale(1.2300m),
                TrimScale = EF.Functions.NumericTrimScale(1.2300m),
                Bucket = EF.Functions.WidthBucket(5d, 0d, 10d, 5),
                ThresholdBucket = EF.Functions.WidthBucket(5, thresholds),
                NumberText = EF.Functions.FormatValue(1234.5m, "FM9999.0"),
                DateText = EF.Functions.FormatValue(timestamp, "YYYY-MM-DD"),
                IntervalText = EF.Functions.FormatValue(interval, "HH24:MI"),
                Date = EF.Functions.ParseDate("2026-08-01", "YYYY-MM-DD"),
                Number = EF.Functions.ParseNumber("1,234.5", "9G999D9"),
                Timestamp = EF.Functions.ParseTimestamp("2026-08-01 12:34", "YYYY-MM-DD HH24:MI"),
                Unix = EF.Functions.UnixTimestamp(0),
            })
            .ToQueryString();

        foreach (var function in new[]
                 {
                     "array_dims(", "array_ndims(", "array_position(", "array_positions(",
                     "array_remove(", "array_replace(", "array_reverse(", "array_shuffle(",
                     "array_sample(", "trim_array(", "array_to_string(", "string_to_array(",
                     "ascii(", "chr(", "bit_length(", "octet_length(", "initcap(", "left(",
                     "right(", "lpad(", "rpad(", "ltrim(", "rtrim(", "btrim(", "md5(",
                     "parse_ident(", "quote_ident(", "quote_literal(", "quote_nullable(",
                     "repeat(", "reverse(", "split_part(", "starts_with(", "translate(",
                     "encode(", "decode(", "get_byte(", "set_byte(", "get_bit(", "set_bit(",
                     "cbrt(", "degrees(", "radians(", "div(", "factorial(", "gcd(", "lcm(",
                     "min_scale(", "scale(", "trim_scale(", "width_bucket(", "to_char(",
                     "to_date(", "to_number(", "to_timestamp(",
                 })
        {
            Assert.Contains(function, sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PostgreSQL_scalar_functions_execute_with_typed_results_and_parameters()
    {
        var connectionString = GetConnectionString();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
        var serverVersion = Convert.ToInt32(
            await ExecuteScalarAsync(dataSource, "SHOW server_version_num"),
            System.Globalization.CultureInfo.InvariantCulture);
        await ExecuteNonQueryAsync(
            dataSource,
            """
            DROP TABLE IF EXISTS "ef_function_values";
            CREATE TABLE "ef_function_values" (
                "Id" integer PRIMARY KEY,
                "Text" text NOT NULL,
                "Numbers" integer[] NOT NULL,
                "Range" int4range NOT NULL,
                "Multirange" int4multirange NOT NULL,
                "Json" jsonb NOT NULL,
                "JsonArray" jsonb NOT NULL,
                "Network" inet NOT NULL);
            INSERT INTO "ef_function_values"
                ("Id", "Text", "Numbers", "Range", "Multirange", "Json", "JsonArray", "Network")
            VALUES (
                1,
                'BlueTusk PostgreSQL provider',
                ARRAY[1, 2, 3],
                '[1,10)'::int4range,
                '{[1,4),[7,10)}'::int4multirange,
                '{"kind":"provider","version":3}'::jsonb,
                '[1,2,3]'::jsonb,
                '10.0.0.42/24'::inet)
            """);

        try
        {
            await using var context = CreateContext(dataSource);
            var collection = await context.Values
                .Select(value => new
                {
                    Length = EF.Functions.ArrayLength(value.Numbers, 1),
                    LowerBound = EF.Functions.ArrayLowerBound(value.Numbers, 1),
                    UpperBound = EF.Functions.ArrayUpperBound(value.Numbers, 1),
                    Cardinality = EF.Functions.ArrayCardinality(value.Numbers),
                    RangeLower = EF.Functions.RangeLower(value.Range),
                    RangeUpper = EF.Functions.RangeUpper(value.Range),
                    RangeEmpty = EF.Functions.RangeIsEmpty(value.Range),
                    RangeLowerInclusive = EF.Functions.RangeIsLowerInclusive(value.Range),
                    RangeUpperInclusive = EF.Functions.RangeIsUpperInclusive(value.Range),
                    RangeLowerInfinite = EF.Functions.RangeIsLowerInfinite(value.Range),
                    RangeUpperInfinite = EF.Functions.RangeIsUpperInfinite(value.Range),
                    MultirangeLower = EF.Functions.MultirangeLower(value.Multirange),
                    MultirangeUpper = EF.Functions.MultirangeUpper(value.Multirange),
                    MultirangeEmpty = EF.Functions.MultirangeIsEmpty(value.Multirange),
                    MultirangeLowerInclusive = EF.Functions.MultirangeIsLowerInclusive(value.Multirange),
                    MultirangeUpperInclusive = EF.Functions.MultirangeIsUpperInclusive(value.Multirange),
                    MultirangeLowerInfinite = EF.Functions.MultirangeIsLowerInfinite(value.Multirange),
                    MultirangeUpperInfinite = EF.Functions.MultirangeIsUpperInfinite(value.Multirange),
                })
                .SingleAsync();

            Assert.Equal(3, collection.Length);
            Assert.Equal(1, collection.LowerBound);
            Assert.Equal(3, collection.UpperBound);
            Assert.Equal(3, collection.Cardinality);
            Assert.Equal(1, collection.RangeLower);
            Assert.Equal(10, collection.RangeUpper);
            Assert.False(collection.RangeEmpty);
            Assert.True(collection.RangeLowerInclusive);
            Assert.False(collection.RangeUpperInclusive);
            Assert.False(collection.RangeLowerInfinite);
            Assert.False(collection.RangeUpperInfinite);
            Assert.Equal(1, collection.MultirangeLower);
            Assert.Equal(10, collection.MultirangeUpper);
            Assert.False(collection.MultirangeEmpty);
            Assert.True(collection.MultirangeLowerInclusive);
            Assert.False(collection.MultirangeUpperInclusive);
            Assert.False(collection.MultirangeLowerInfinite);
            Assert.False(collection.MultirangeUpperInfinite);

            var bytes = new byte[] { 0x01, 0x02, 0x03 };
            var bits = new BlueTuskBitString("1010");
            var thresholds = new[] { 0, 10, 20 };
            var trimValue = new byte[] { 0x01, 0x01, 0x02, 0x01 };
            var leftTrimValue = new byte[] { 0x01, 0x01, 0x02 };
            var rightTrimValue = new byte[] { 0x02, 0x01, 0x01 };
            var trimBytes = new byte[] { 0x01 };
            var common = await context.Values
                .Select(value => new
                {
                    Dimensions = EF.Functions.ArrayDimensions(value.Numbers),
                    DimensionCount = EF.Functions.ArrayDimensionCount(value.Numbers),
                    Position = EF.Functions.ArrayPosition(value.Numbers, 2),
                    PositionFrom = EF.Functions.ArrayPosition(value.Numbers, 2, 2),
                    Positions = EF.Functions.ArrayPositions(value.Numbers, 2),
                    Removed = EF.Functions.ArrayRemove(value.Numbers, 2),
                    Replaced = EF.Functions.ArrayReplace(value.Numbers, 2, 9),
                    Trimmed = EF.Functions.ArrayTrim(value.Numbers, 1),
                    Joined = EF.Functions.ArrayToString(value.Numbers, ","),
                    Split = EF.Functions.StringToArray(value.Text, " "),
                    Ascii = EF.Functions.StringAscii(value.Text),
                    Character = EF.Functions.StringCharacter(65),
                    TextBits = EF.Functions.BitLength(value.Text),
                    BinaryBits = EF.Functions.BitLength(bytes),
                    ValueBits = EF.Functions.BitLength(bits),
                    TextBytes = EF.Functions.ByteLength(value.Text),
                    BinaryBytes = EF.Functions.ByteLength(bytes),
                    ValueBytes = EF.Functions.ByteLength(bits),
                    InitialCapital = EF.Functions.StringInitialCapital(value.Text),
                    Left = EF.Functions.StringLeft(value.Text, 4),
                    Right = EF.Functions.StringRight(value.Text, 8),
                    LeftPad = EF.Functions.StringPadLeft("42", 4, "0"),
                    RightPad = EF.Functions.StringPadRight("42", 4, "0"),
                    LeftTrim = EF.Functions.StringTrimLeft("xxvalue", "x"),
                    RightTrim = EF.Functions.StringTrimRight("valuexx", "x"),
                    Trim = EF.Functions.StringTrim("xxvaluexx", "x"),
                    TextMd5 = EF.Functions.Md5("abc"),
                    BinaryMd5 = EF.Functions.Md5(bytes),
                    Identifier = EF.Functions.ParseIdentifier("public.table"),
                    QuotedIdentifier = EF.Functions.QuoteIdentifier("Mixed Name"),
                    QuotedLiteral = EF.Functions.QuoteLiteral("Blue'Tusk"),
                    QuotedNullable = EF.Functions.QuoteNullableLiteral<string>(null),
                    Repeated = EF.Functions.StringRepeat("ab", 3),
                    ReversedText = EF.Functions.StringReverse("abc"),
                    Part = EF.Functions.StringSplitPart(value.Text, " ", 2),
                    StartsWith = EF.Functions.StringStartsWith(value.Text, "Blue"),
                    Translated = EF.Functions.StringTranslate("12345", "143", "ax"),
                    Encoded = EF.Functions.BinaryEncode(bytes, "hex"),
                    Decoded = EF.Functions.BinaryDecode("010203", "hex"),
                    Byte = EF.Functions.BinaryGetByte(bytes, 1),
                    SetByte = EF.Functions.BinarySetByte(bytes, 1, 9),
                    Bit = EF.Functions.BinaryGetBit(bytes, 7),
                    SetBit = EF.Functions.BinarySetBit(bytes, 7, 1),
                    TrimmedBytes = EF.Functions.BinaryTrim(trimValue, trimBytes),
                    LeftTrimmedBytes = EF.Functions.BinaryTrimLeft(leftTrimValue, trimBytes),
                    RightTrimmedBytes = EF.Functions.BinaryTrimRight(rightTrimValue, trimBytes),
                    CubeRoot = EF.Functions.CubeRoot(27),
                    Degrees = EF.Functions.Degrees(Math.PI),
                    Radians = EF.Functions.Radians(180),
                    Division = EF.Functions.NumericDivide(7, 2),
                    Factorial = EF.Functions.Factorial(5),
                    Gcd = EF.Functions.GreatestCommonDivisor(12, 18),
                    Lcm = EF.Functions.LeastCommonMultiple(12, 18),
                    MinimumScale = EF.Functions.NumericMinimumScale(1.2300m),
                    Scale = EF.Functions.NumericScale(1.2300m),
                    TrimScale = EF.Functions.NumericTrimScale(1.2300m),
                    Bucket = EF.Functions.WidthBucket(5d, 0d, 10d, 5),
                    ThresholdBucket = EF.Functions.WidthBucket(15, thresholds),
                    NumberText = EF.Functions.FormatValue(1234.5m, "FM9999.0"),
                    DateText = EF.Functions.FormatValue(
                        new DateTime(2026, 8, 1, 12, 34, 56, DateTimeKind.Unspecified),
                        "YYYY-MM-DD"),
                    IntervalText = EF.Functions.FormatValue(
                        BlueTuskInterval.Parse("2 hours"),
                        "HH24:MI"),
                    Date = EF.Functions.ParseDate("2026-08-01", "YYYY-MM-DD"),
                    Number = EF.Functions.ParseNumber("1,234.5", "9G999D9"),
                    Timestamp = EF.Functions.ParseTimestamp(
                        "2026-08-01 12:34 +00",
                        "YYYY-MM-DD HH24:MI TZH"),
                    Unix = EF.Functions.UnixTimestamp(0),
                })
                .SingleAsync();

            Assert.Equal("[1:3]", common.Dimensions);
            Assert.Equal(1, common.DimensionCount);
            Assert.Equal(2, common.Position);
            Assert.Equal(2, common.PositionFrom);
            Assert.Equal([2], common.Positions!);
            Assert.Equal([1, 3], common.Removed);
            Assert.Equal([1, 9, 3], common.Replaced);
            Assert.Equal([1, 2], common.Trimmed);
            Assert.Equal("1,2,3", common.Joined);
            Assert.Equal(["BlueTusk", "PostgreSQL", "provider"], common.Split!);
            Assert.Equal(66, common.Ascii);
            Assert.Equal("A", common.Character);
            Assert.Equal(224, common.TextBits);
            Assert.Equal(24, common.BinaryBits);
            Assert.Equal(4, common.ValueBits);
            Assert.Equal(28, common.TextBytes);
            Assert.Equal(3, common.BinaryBytes);
            Assert.Equal(1, common.ValueBytes);
            Assert.Equal("Bluetusk Postgresql Provider", common.InitialCapital);
            Assert.Equal("Blue", common.Left);
            Assert.Equal("provider", common.Right);
            Assert.Equal("0042", common.LeftPad);
            Assert.Equal("4200", common.RightPad);
            Assert.Equal("value", common.LeftTrim);
            Assert.Equal("value", common.RightTrim);
            Assert.Equal("value", common.Trim);
            Assert.Equal("900150983cd24fb0d6963f7d28e17f72", common.TextMd5);
            Assert.Equal("5289df737df57326fcdd22597afb1fac", common.BinaryMd5);
            Assert.Equal(["public", "table"], common.Identifier!);
            Assert.Equal("\"Mixed Name\"", common.QuotedIdentifier);
            Assert.Equal("'Blue''Tusk'", common.QuotedLiteral);
            Assert.Equal("NULL", common.QuotedNullable);
            Assert.Equal("ababab", common.Repeated);
            Assert.Equal("cba", common.ReversedText);
            Assert.Equal("PostgreSQL", common.Part);
            Assert.True(common.StartsWith);
            Assert.Equal("a2x5", common.Translated);
            Assert.Equal("010203", common.Encoded);
            Assert.Equal(bytes, common.Decoded);
            Assert.Equal(2, common.Byte);
            Assert.Equal([0x01, 0x09, 0x03], common.SetByte);
            Assert.Equal(0, common.Bit);
            Assert.Equal([0x81, 0x02, 0x03], common.SetBit);
            Assert.Equal([0x02], common.TrimmedBytes);
            Assert.Equal([0x02], common.LeftTrimmedBytes);
            Assert.Equal([0x02], common.RightTrimmedBytes);
            Assert.Equal(3, common.CubeRoot);
            Assert.Equal(180, common.Degrees, precision: 10);
            Assert.Equal(Math.PI, common.Radians, precision: 10);
            Assert.Equal(3, common.Division);
            Assert.Equal(120m, common.Factorial);
            Assert.Equal(6, common.Gcd);
            Assert.Equal(36, common.Lcm);
            Assert.Equal(2, common.MinimumScale);
            Assert.Equal(2, common.Scale);
            Assert.Equal(1.23m, common.TrimScale);
            Assert.Equal(3, common.Bucket);
            Assert.Equal(2, common.ThresholdBucket);
            Assert.Equal("1234.5", common.NumberText);
            Assert.Equal("2026-08-01", common.DateText);
            Assert.Equal("02:00", common.IntervalText);
            Assert.Equal(new DateOnly(2026, 8, 1), common.Date);
            Assert.Equal(1234.5m, common.Number);
            Assert.Equal(
                new DateTimeOffset(2026, 8, 1, 12, 34, 0, TimeSpan.Zero),
                common.Timestamp.ToUniversalTime());
            Assert.Equal(DateTimeOffset.UnixEpoch, common.Unix.ToUniversalTime());

            if (serverVersion >= 160000)
            {
                var randomized = await context.Values
                    .Select(value => new
                    {
                        Shuffled = EF.Functions.ArrayShuffle(value.Numbers),
                        Sampled = EF.Functions.ArraySample(value.Numbers, 2),
                    })
                    .SingleAsync();
                Assert.Equal([1, 2, 3], randomized.Shuffled.Order());
                Assert.Equal(2, randomized.Sampled.Length);
                Assert.All(randomized.Sampled, item => Assert.InRange(item, 1, 3));
            }

            if (serverVersion >= 180000)
            {
                var reversed = await context.Values
                    .Select(value => new
                    {
                        Array = EF.Functions.ArrayReverse(value.Numbers),
                        Bytes = EF.Functions.BinaryReverse(bytes),
                    })
                    .SingleAsync();
                Assert.Equal([3, 2, 1], reversed.Array);
                Assert.Equal([0x03, 0x02, 0x01], reversed.Bytes);
            }

            var jsonPath = new BlueTuskJsonPath("$.version");
            var scalar = await context.Values
                .Select(value => new
                {
                    JsonType = EF.Functions.JsonTypeOf(value.Json),
                    JsonLength = EF.Functions.JsonArrayLength(value.JsonArray),
                    JsonValue = EF.Functions.JsonPathQueryFirst(value.Json, jsonPath),
                    Replaced = EF.Functions.RegexReplace(value.Text, "PostgreSQL", "database"),
                    Capitals = EF.Functions.RegexCount(value.Text, "[A-Z]"),
                    Host = EF.Functions.NetworkHost(value.Network),
                    Family = EF.Functions.NetworkAddressFamily(value.Network),
                    MaskLength = EF.Functions.NetworkMaskLength(value.Network),
                    Network = EF.Functions.NetworkPart(value.Network),
                    Broadcast = EF.Functions.NetworkBroadcast(value.Network),
                })
                .SingleAsync();

            Assert.Equal("object", scalar.JsonType);
            Assert.Equal(3, scalar.JsonLength);
            Assert.Equal("3", scalar.JsonValue);
            Assert.Equal("BlueTusk database provider", scalar.Replaced);
            Assert.Equal(6, scalar.Capitals);
            Assert.Equal("10.0.0.42", scalar.Host);
            Assert.Equal(4, scalar.Family);
            Assert.Equal(24, scalar.MaskLength);
            Assert.Equal("10.0.0.0/24", scalar.Network.ToString());
            Assert.True(scalar.Network.IsCidr);
            Assert.Equal("10.0.0.255/24", scalar.Broadcast.ToString());

            var search = "PostgreSQL provider";
            var textSearch = await context.Values
                .Select(value => new
                {
                    Matches = EF.Functions.FullTextMatches(
                        EF.Functions.ToTextSearchVector(value.Text),
                        EF.Functions.PlainToTextSearchQuery(search)),
                    VectorLength = EF.Functions.TextSearchVectorLength(
                        EF.Functions.ToTextSearchVector(value.Text)),
                    RawNodes = EF.Functions.TextSearchQueryNodeCount(
                        EF.Functions.ToTextSearchQuery("PostgreSQL & provider")),
                    PlainNodes = EF.Functions.TextSearchQueryNodeCount(
                        EF.Functions.PlainToTextSearchQuery(search)),
                    PhraseNodes = EF.Functions.TextSearchQueryNodeCount(
                        EF.Functions.PhraseToTextSearchQuery(search)),
                    WebNodes = EF.Functions.TextSearchQueryNodeCount(
                        EF.Functions.WebSearchToTextSearchQuery(search)),
                    Rank = EF.Functions.TextSearchRank(
                        EF.Functions.ToTextSearchVector(value.Text),
                        EF.Functions.PlainToTextSearchQuery(search)),
                })
                .SingleAsync();

            Assert.True(textSearch.Matches);
            Assert.True(textSearch.VectorLength > 0);
            Assert.True(textSearch.RawNodes > 0);
            Assert.True(textSearch.PlainNodes > 0);
            Assert.True(textSearch.PhraseNodes > 0);
            Assert.True(textSearch.WebNodes > 0);
            Assert.True(textSearch.Rank > 0);

            var timestamp = new DateTime(
                2026,
                8,
                1,
                12,
                34,
                56,
                DateTimeKind.Unspecified);
            var timestampWithTimeZone = new DateTimeOffset(
                2026,
                8,
                1,
                12,
                34,
                56,
                TimeSpan.Zero);
            var nativeInterval = new BlueTuskInterval(
                months: 27,
                days: 3,
                microseconds: (TimeSpan.FromHours(2) + TimeSpan.FromMinutes(47)).Ticks / 10);
            var temporal = await context.Values
                .Select(_ => new
                {
                    Hour = EF.Functions.DatePart("hour", timestamp),
                    OffsetHour = EF.Functions.DatePart("hour", timestampWithTimeZone),
                    IntervalMonth = EF.Functions.DatePart("month", nativeInterval),
                    Truncated = EF.Functions.DateTrunc("day", timestamp),
                    OffsetTruncated = EF.Functions.DateTrunc(
                        "day",
                        timestampWithTimeZone,
                        "Europe/London"),
                    IntervalTruncated = EF.Functions.DateTrunc("hour", nativeInterval),
                    Binned = EF.Functions.DateBin(
                        TimeSpan.FromMinutes(15),
                        timestamp,
                        new DateTime(2026, 8, 1)),
                    OffsetBinned = EF.Functions.DateBin(
                        TimeSpan.FromMinutes(15),
                        timestampWithTimeZone,
                        new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
                    Age = EF.Functions.DateAge(
                        new DateTime(2026, 3, 15),
                        new DateTime(2026, 1, 10)),
                    OffsetAge = EF.Functions.DateAge(
                        new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero)),
                    Date = EF.Functions.MakeDate(2026, 8, 1),
                    Time = EF.Functions.MakeTime(12, 34, 56.5),
                    Timestamp = EF.Functions.MakeTimestamp(2026, 8, 1, 12, 34, 56.5),
                    TimestampWithTimeZone = EF.Functions.MakeTimestampWithTimeZone(
                        2026,
                        8,
                        1,
                        12,
                        34,
                        56.5,
                        "UTC"),
                    Interval = EF.Functions.MakeInterval(1, 2, 1, 3, 4, 5, 6.5),
                    JustifiedDays = EF.Functions.JustifyDays(
                        new BlueTuskInterval(0, 65, 0)),
                    JustifiedHours = EF.Functions.JustifyHours(
                        new BlueTuskInterval(
                            0,
                            0,
                            (TimeSpan.FromHours(50) + TimeSpan.FromMinutes(10)).Ticks / 10)),
                    Justified = EF.Functions.JustifyInterval(
                        new BlueTuskInterval(1, 0, -TimeSpan.FromHours(1).Ticks / 10)),
                })
                .SingleAsync();

            Assert.Equal(12, temporal.Hour);
            Assert.Equal(12, temporal.OffsetHour);
            Assert.Equal(3, temporal.IntervalMonth);
            Assert.Equal(new DateTime(2026, 8, 1), temporal.Truncated);
            Assert.Equal(
                new DateTimeOffset(2026, 7, 31, 23, 0, 0, TimeSpan.Zero),
                temporal.OffsetTruncated.ToUniversalTime());
            Assert.Equal(
                new BlueTuskInterval(27, 3, TimeSpan.FromHours(2).Ticks / 10),
                temporal.IntervalTruncated);
            Assert.Equal(new DateTime(2026, 8, 1, 12, 30, 0), temporal.Binned);
            Assert.Equal(
                new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.Zero),
                temporal.OffsetBinned.ToUniversalTime());
            Assert.Equal(new BlueTuskInterval(2, 5, 0), temporal.Age);
            Assert.Equal(new BlueTuskInterval(2, 5, 0), temporal.OffsetAge);
            Assert.Equal(new DateOnly(2026, 8, 1), temporal.Date);
            Assert.Equal(new TimeOnly(12, 34, 56, 500), temporal.Time);
            Assert.Equal(new DateTime(2026, 8, 1, 12, 34, 56, 500), temporal.Timestamp);
            Assert.Equal(
                new DateTimeOffset(2026, 8, 1, 12, 34, 56, 500, TimeSpan.Zero),
                temporal.TimestampWithTimeZone.ToUniversalTime());
            Assert.Equal(
                new BlueTuskInterval(
                    14,
                    10,
                    (TimeSpan.FromHours(4)
                        + TimeSpan.FromMinutes(5)
                        + TimeSpan.FromSeconds(6.5)).Ticks / 10),
                temporal.Interval);
            Assert.Equal(new BlueTuskInterval(2, 5, 0), temporal.JustifiedDays);
            Assert.Equal(
                new BlueTuskInterval(
                    0,
                    2,
                    (TimeSpan.FromHours(2) + TimeSpan.FromMinutes(10)).Ticks / 10),
                temporal.JustifiedHours);
            Assert.Equal(
                new BlueTuskInterval(0, 29, TimeSpan.FromHours(23).Ticks / 10),
                temporal.Justified);

            var box = new BlueTuskBox(
                new BlueTuskPoint(0, 0),
                new BlueTuskPoint(4, 3));
            var circle = new BlueTuskCircle(new BlueTuskPoint(1, 2), 2);
            var segment = new BlueTuskLineSegment(
                new BlueTuskPoint(0, 0),
                new BlueTuskPoint(3, 4));
            var points = new[]
            {
                new BlueTuskPoint(0, 0),
                new BlueTuskPoint(3, 0),
                new BlueTuskPoint(0, 4),
            };
            var closedPath = new BlueTuskPath(points, isClosed: true);
            var openPath = new BlueTuskPath(points, isClosed: false);
            var polygon = new BlueTuskPolygon(points);
            var geometry = await context.Values
                .Select(_ => new
                {
                    BoxArea = EF.Functions.GeometryArea(box),
                    PathArea = EF.Functions.GeometryArea(closedPath),
                    OpenPathArea = EF.Functions.GeometryArea(openPath),
                    CircleArea = EF.Functions.GeometryArea(circle),
                    BoxCenter = EF.Functions.GeometryCenter(box),
                    CircleCenter = EF.Functions.GeometryCenter(circle),
                    Diagonal = EF.Functions.BoxDiagonal(box),
                    Diameter = EF.Functions.CircleDiameter(circle),
                    Height = EF.Functions.BoxHeight(box),
                    Closed = EF.Functions.PathIsClosed(closedPath),
                    Open = EF.Functions.PathIsOpen(openPath),
                    SegmentLength = EF.Functions.GeometryLength(segment),
                    ClosedPathLength = EF.Functions.GeometryLength(closedPath),
                    OpenPathLength = EF.Functions.GeometryLength(openPath),
                    PathPoints = EF.Functions.GeometryPointCount(closedPath),
                    PolygonPoints = EF.Functions.GeometryPointCount(polygon),
                    ClosedPath = EF.Functions.PathClose(openPath),
                    OpenPath = EF.Functions.PathOpen(closedPath),
                    Radius = EF.Functions.CircleRadius(circle),
                    Slope = EF.Functions.PointSlope(
                        new BlueTuskPoint(0, 0),
                        new BlueTuskPoint(2, 1)),
                    Width = EF.Functions.BoxWidth(box),
                })
                .SingleAsync();

            Assert.Equal(12, geometry.BoxArea);
            Assert.Equal(6, geometry.PathArea);
            Assert.Null(geometry.OpenPathArea);
            Assert.Equal(Math.PI * 4, geometry.CircleArea, precision: 10);
            Assert.Equal(new BlueTuskPoint(2, 1.5), geometry.BoxCenter);
            Assert.Equal(circle.Center, geometry.CircleCenter);
            Assert.Equal(new BlueTuskLineSegment(box.High, box.Low), geometry.Diagonal);
            Assert.Equal(4, geometry.Diameter);
            Assert.Equal(3, geometry.Height);
            Assert.True(geometry.Closed);
            Assert.True(geometry.Open);
            Assert.Equal(5, geometry.SegmentLength);
            Assert.Equal(12, geometry.ClosedPathLength);
            Assert.Equal(8, geometry.OpenPathLength);
            Assert.Equal(3, geometry.PathPoints);
            Assert.Equal(3, geometry.PolygonPoints);
            Assert.True(geometry.ClosedPath.IsClosed);
            Assert.False(geometry.OpenPath.IsClosed);
            Assert.Equal(2, geometry.Radius);
            Assert.Equal(0.5, geometry.Slope);
            Assert.Equal(4, geometry.Width);
        }
        finally
        {
            await ExecuteNonQueryAsync(dataSource, "DROP TABLE IF EXISTS \"ef_function_values\"");
        }
    }

    private static FunctionContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FunctionContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new FunctionContext(options);
    }

    private static FunctionContext CreateContext(BlueTuskDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<FunctionContext>()
            .UseBlueTusk(dataSource)
            .Options;
        return new FunctionContext(options);
    }

    private static async Task ExecuteNonQueryAsync(BlueTuskDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ExecuteScalarAsync(BlueTuskDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        return new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        }.ConnectionString;
    }

    private sealed class FunctionContext(DbContextOptions<FunctionContext> options) : DbContext(options)
    {
        public DbSet<FunctionValue> Values => Set<FunctionValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var value = modelBuilder.Entity<FunctionValue>();
            value.ToTable("ef_function_values");
            value.Property(item => item.Json).HasColumnType("jsonb");
            value.Property(item => item.JsonArray).HasColumnType("jsonb");
        }
    }

    private sealed class FunctionValue
    {
        public int Id { get; set; }

        public string Text { get; set; } = string.Empty;

        public int[] Numbers { get; set; } = [];

        public BlueTuskRange<int> Range { get; set; }

        public BlueTuskMultirange<int> Multirange { get; set; } = new([]);

        public string Json { get; set; } = "{}";

        public string JsonArray { get; set; } = "[]";

        public BlueTuskNetworkAddress Network { get; set; }
    }
}
