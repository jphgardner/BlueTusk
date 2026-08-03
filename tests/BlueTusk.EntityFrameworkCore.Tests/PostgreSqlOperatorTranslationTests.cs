using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PostgreSqlOperatorTranslationTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void Pattern_matching_extensions_translate_to_PostgreSQL_operators()
    {
        using var context = CreateContext();
        var like = "blue%";
        var regex = "^blue";

        var sql = context.Documents
            .Where(document =>
                EF.Functions.ILike(document.Name, like)
                && EF.Functions.RegexIsMatch(document.Name, regex)
                && EF.Functions.RegexIsMatchInsensitive(document.Name, regex)
                && EF.Functions.RegexIsNotMatch(document.Name, "^other")
                && EF.Functions.RegexIsNotMatchInsensitive(document.Name, "^other"))
            .ToQueryString();

        Assert.Contains(" ILIKE ", sql, StringComparison.Ordinal);
        Assert.Contains(" ~ ", sql, StringComparison.Ordinal);
        Assert.Contains(" ~* ", sql, StringComparison.Ordinal);
        Assert.Contains(" !~ ", sql, StringComparison.Ordinal);
        Assert.Contains(" !~* ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Array_range_and_multirange_extensions_translate_to_PostgreSQL_operators()
    {
        using var context = CreateContext();
        var numbers = new[] { 2, 3 };
        var range = new BlueTuskRange<int>(2, 8);
        var multirange = new BlueTuskMultirange<int>([range]);

        var arraySql = context.Documents
            .Where(document =>
                EF.Functions.ArrayContains(document.Numbers, numbers)
                && EF.Functions.ArrayContainedBy(document.Numbers, numbers)
                && EF.Functions.ArrayOverlaps(document.Numbers, numbers))
            .ToQueryString();
        var rangeSql = context.Documents
            .Where(document =>
                EF.Functions.RangeContains(document.Range, range)
                && EF.Functions.RangeContains(document.Range, multirange)
                && EF.Functions.RangeContains(document.Range, 5)
                && EF.Functions.RangeContainedBy(document.Range, range)
                && EF.Functions.RangeContainedBy(document.Range, multirange)
                && EF.Functions.RangeOverlaps(document.Range, range)
                && EF.Functions.RangeOverlaps(document.Range, multirange)
                && EF.Functions.RangeIsStrictlyLeftOf(document.Range, range)
                && EF.Functions.RangeIsStrictlyLeftOf(document.Range, multirange)
                && EF.Functions.RangeIsStrictlyRightOf(document.Range, range)
                && EF.Functions.RangeIsStrictlyRightOf(document.Range, multirange)
                && EF.Functions.RangeIsAdjacentTo(document.Range, range)
                && EF.Functions.RangeIsAdjacentTo(document.Range, multirange)
                && EF.Functions.RangeDoesNotExtendRightOf(document.Range, range)
                && EF.Functions.RangeDoesNotExtendRightOf(document.Range, multirange)
                && EF.Functions.RangeDoesNotExtendLeftOf(document.Range, range)
                && EF.Functions.RangeDoesNotExtendLeftOf(document.Range, multirange))
            .ToQueryString();
        var multirangeSql = context.Documents
            .Where(document =>
                EF.Functions.MultirangeContains(document.Multirange, multirange)
                && EF.Functions.MultirangeContains(document.Multirange, range)
                && EF.Functions.MultirangeContains(document.Multirange, 5)
                && EF.Functions.MultirangeContainedBy(document.Multirange, multirange)
                && EF.Functions.MultirangeContainedBy(document.Multirange, range)
                && EF.Functions.MultirangeOverlaps(document.Multirange, multirange)
                && EF.Functions.MultirangeOverlaps(document.Multirange, range)
                && EF.Functions.MultirangeIsStrictlyLeftOf(document.Multirange, multirange)
                && EF.Functions.MultirangeIsStrictlyLeftOf(document.Multirange, range)
                && EF.Functions.MultirangeIsStrictlyRightOf(document.Multirange, multirange)
                && EF.Functions.MultirangeIsStrictlyRightOf(document.Multirange, range)
                && EF.Functions.MultirangeDoesNotExtendRightOf(document.Multirange, multirange)
                && EF.Functions.MultirangeDoesNotExtendRightOf(document.Multirange, range)
                && EF.Functions.MultirangeDoesNotExtendLeftOf(document.Multirange, multirange)
                && EF.Functions.MultirangeDoesNotExtendLeftOf(document.Multirange, range)
                && EF.Functions.MultirangeIsAdjacentTo(document.Multirange, multirange)
                && EF.Functions.MultirangeIsAdjacentTo(document.Multirange, range))
            .ToQueryString();

        Assert.Contains(" @> ", arraySql, StringComparison.Ordinal);
        Assert.Contains(" <@ ", arraySql, StringComparison.Ordinal);
        Assert.Contains(" && ", arraySql, StringComparison.Ordinal);
        Assert.Contains(" << ", rangeSql, StringComparison.Ordinal);
        Assert.Contains(" >> ", rangeSql, StringComparison.Ordinal);
        Assert.Contains(" -|- ", rangeSql, StringComparison.Ordinal);
        Assert.Contains(" &< ", rangeSql, StringComparison.Ordinal);
        Assert.Contains(" &> ", rangeSql, StringComparison.Ordinal);
        Assert.Contains(" @> ", multirangeSql, StringComparison.Ordinal);
        Assert.Contains(" <@ ", multirangeSql, StringComparison.Ordinal);
        Assert.Contains(" && ", multirangeSql, StringComparison.Ordinal);
        Assert.Contains(" << ", multirangeSql, StringComparison.Ordinal);
        Assert.Contains(" >> ", multirangeSql, StringComparison.Ordinal);
        Assert.Contains(" &< ", multirangeSql, StringComparison.Ordinal);
        Assert.Contains(" &> ", multirangeSql, StringComparison.Ordinal);
        Assert.Contains(" -|- ", multirangeSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Quantified_and_row_value_comparisons_translate_to_PostgreSQL_syntax()
    {
        using var context = CreateContext();
        var values = new[] { 1, 2 };
        var patterns = new[] { "Blue%", "%Provider" };
        var boundaryId = 2;
        var boundaryName = "Boundary";

        var quantifiedSql = context.Documents
            .Where(document =>
                EF.Functions.EqualAny(document.Id, values)
                && EF.Functions.NotEqualAny(document.Id, values)
                && EF.Functions.LessThanAny(document.Id, values)
                && EF.Functions.LessThanOrEqualAny(document.Id, values)
                && EF.Functions.GreaterThanAny(document.Id, values)
                && EF.Functions.GreaterThanOrEqualAny(document.Id, values)
                && EF.Functions.EqualAll(document.Id, values)
                && EF.Functions.NotEqualAll(document.Id, values)
                && EF.Functions.LessThanAll(document.Id, values)
                && EF.Functions.LessThanOrEqualAll(document.Id, values)
                && EF.Functions.GreaterThanAll(document.Id, values)
                && EF.Functions.GreaterThanOrEqualAll(document.Id, values)
                && EF.Functions.LikeAny(document.Name, patterns)
                && EF.Functions.ILikeAny(document.Name, patterns)
                && EF.Functions.LikeAll(document.Name, patterns)
                && EF.Functions.ILikeAll(document.Name, patterns))
            .ToQueryString();
        var rowSql = context.Documents
            .Where(document =>
                EF.Functions.RowEqual(
                    ValueTuple.Create(document.Id, document.Name),
                    ValueTuple.Create(boundaryId, boundaryName))
                || EF.Functions.RowNotEqual(
                    ValueTuple.Create(document.Id, document.Name),
                    ValueTuple.Create(boundaryId, boundaryName))
                || EF.Functions.RowLessThan(
                    ValueTuple.Create(document.Id, document.Name),
                    ValueTuple.Create(boundaryId, boundaryName))
                || EF.Functions.RowLessThanOrEqual(
                    ValueTuple.Create(document.Id, document.Name),
                    ValueTuple.Create(boundaryId, boundaryName))
                || EF.Functions.RowGreaterThan(
                    ValueTuple.Create(document.Id, document.Name),
                    ValueTuple.Create(boundaryId, boundaryName))
                || EF.Functions.RowGreaterThanOrEqual(
                    ValueTuple.Create(document.Id, document.Name),
                    ValueTuple.Create(boundaryId, boundaryName)))
            .ToQueryString();

        Assert.Contains(" = ANY(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" <> ANY(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" < ANY(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" <= ANY(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" > ANY(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" >= ANY(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" = ALL(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" <> ALL(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" < ALL(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" <= ALL(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" > ALL(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" >= ALL(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" LIKE ANY(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" ILIKE ANY(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" LIKE ALL(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains(" ILIKE ALL(", quantifiedSql, StringComparison.Ordinal);
        Assert.Contains("(\"e\".\"Id\", \"e\".\"Name\")", rowSql, StringComparison.Ordinal);
        Assert.Contains(" = (", rowSql, StringComparison.Ordinal);
        Assert.Contains(" <> (", rowSql, StringComparison.Ordinal);
        Assert.Contains(" < (", rowSql, StringComparison.Ordinal);
        Assert.Contains(" <= (", rowSql, StringComparison.Ordinal);
        Assert.Contains(" > (", rowSql, StringComparison.Ordinal);
        Assert.Contains(" >= (", rowSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Row_value_comparisons_reject_mismatched_tuple_lengths()
    {
        using var context = CreateContext();

        var error = Assert.Throws<InvalidOperationException>(() => context.Documents
            .Where(document => EF.Functions.RowGreaterThan(
                ValueTuple.Create(document.Id, document.Name),
                ValueTuple.Create(1, "Boundary", 3)))
            .ToQueryString());

        Assert.Contains("same number of elements", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_extensions_preserve_jsonb_and_path_operator_semantics()
    {
        using var context = CreateContext();
        var fragment = """{"kind":"provider"}""";
        var keys = new[] { "kind", "version" };
        var path = new BlueTuskJsonPath("$.kind");
        var predicate = new BlueTuskJsonPath("$.version > 1");

        var sql = context.Documents
            .Where(document =>
                EF.Functions.JsonContains(document.Json, fragment)
                && EF.Functions.JsonContainedBy(document.Json, fragment)
                && EF.Functions.JsonExists(document.Json, "kind")
                && EF.Functions.JsonExistsAny(document.Json, keys)
                && EF.Functions.JsonExistsAll(document.Json, keys)
                && EF.Functions.JsonPathExists(document.Json, path)
                && EF.Functions.JsonPathMatches(document.Json, predicate))
            .ToQueryString();

        Assert.Contains(" @> ", sql, StringComparison.Ordinal);
        Assert.Contains(" <@ ", sql, StringComparison.Ordinal);
        Assert.Contains(" ? ", sql, StringComparison.Ordinal);
        Assert.Contains(" ?| ", sql, StringComparison.Ordinal);
        Assert.Contains(" ?& ", sql, StringComparison.Ordinal);
        Assert.Contains(" @? ", sql, StringComparison.Ordinal);
        Assert.Contains(" @@ ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Network_and_full_text_extensions_translate_to_typed_PostgreSQL_operators()
    {
        using var context = CreateContext();
        var network = BlueTuskNetworkAddress.Parse("10.0.0.0/24");
        var query = BlueTuskTextSearchQuery.Parse("'provider'");

        var sql = context.Documents
            .Where(document =>
                EF.Functions.NetworkContains(document.Network, network)
                && EF.Functions.NetworkContainedBy(document.Network, network)
                && EF.Functions.NetworkOverlaps(document.Network, network)
                && EF.Functions.NetworkStrictlyContains(document.Network, network)
                && EF.Functions.NetworkStrictlyContainedBy(document.Network, network)
                && EF.Functions.FullTextMatches(document.SearchVector, query)
                && EF.Functions.FullTextQueryContains(query, query)
                && EF.Functions.FullTextQueryContainedBy(query, query))
            .ToQueryString();

        Assert.Contains(" >>= ", sql, StringComparison.Ordinal);
        Assert.Contains(" <<= ", sql, StringComparison.Ordinal);
        Assert.Contains(" && ", sql, StringComparison.Ordinal);
        Assert.Contains(" >> ", sql, StringComparison.Ordinal);
        Assert.Contains(" << ", sql, StringComparison.Ordinal);
        Assert.Contains(" @@ ", sql, StringComparison.Ordinal);
        Assert.Contains(" @> ", sql, StringComparison.Ordinal);
        Assert.Contains(" <@ ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Scalar_producing_operators_translate_with_typed_results()
    {
        using var context = CreateContext();
        var numbers = new[] { 1, 2 };
        var otherNumbers = new[] { 3, 4 };
        var range = new BlueTuskRange<int>(1, 5);
        var otherRange = new BlueTuskRange<int>(4, 8);
        var multirange = new BlueTuskMultirange<int>([range]);
        var otherMultirange = new BlueTuskMultirange<int>([otherRange]);
        var query = BlueTuskTextSearchQuery.Parse("'blue'");
        var otherQuery = BlueTuskTextSearchQuery.Parse("'tusk'");
        var vector = BlueTuskTextSearchVector.Parse("'blue':1");
        var otherVector = BlueTuskTextSearchVector.Parse("'tusk':1");
        var network = BlueTuskNetworkAddress.Parse("10.0.0.4/24");
        var otherNetwork = BlueTuskNetworkAddress.Parse("255.255.255.0");
        var jsonPath = new[] { "a", "b" };
        var bits = new BlueTuskBitString("101");
        var otherBits = new BlueTuskBitString("011");

        var sql = context.Documents
            .Select(_ => new
            {
                ArrayConcat = EF.Functions.ArrayConcatenate(numbers, otherNumbers),
                ArrayAppend = EF.Functions.ArrayAppend(numbers, 3),
                ArrayPrepend = EF.Functions.ArrayPrepend(0, numbers),
                RangeUnion = EF.Functions.RangeUnion(range, otherRange),
                RangeIntersect = EF.Functions.RangeIntersect(range, otherRange),
                RangeExcept = EF.Functions.RangeExcept(range, otherRange),
                MultirangeUnion = EF.Functions.MultirangeUnion(multirange, otherMultirange),
                MultirangeIntersect = EF.Functions.MultirangeIntersect(multirange, otherMultirange),
                MultirangeExcept = EF.Functions.MultirangeExcept(multirange, otherMultirange),
                JsonConcat = EF.Functions.JsonConcatenate("{\"left\":1}", "{\"right\":2}"),
                JsonDeleteKey = EF.Functions.JsonDelete("{\"drop\":1}", "drop"),
                JsonDeleteIndex = EF.Functions.JsonDelete("[1,2]", 0),
                JsonDeletePath = EF.Functions.JsonDeletePath("{\"a\":{\"b\":1}}", jsonPath),
                JsonGet = EF.Functions.JsonGet("{\"a\":1}", "a"),
                JsonGetIndex = EF.Functions.JsonGet("[1,2]", 0),
                JsonGetText = EF.Functions.JsonGetText("{\"a\":1}", "a"),
                JsonGetIndexText = EF.Functions.JsonGetText("[1,2]", 0),
                JsonGetPath = EF.Functions.JsonGetPath("{\"a\":{\"b\":1}}", jsonPath),
                JsonGetPathText = EF.Functions.JsonGetPathText("{\"a\":{\"b\":1}}", jsonPath),
                VectorConcat = EF.Functions.FullTextVectorConcatenate(vector, otherVector),
                QueryAnd = EF.Functions.FullTextQueryAnd(query, otherQuery),
                QueryOr = EF.Functions.FullTextQueryOr(query, otherQuery),
                QueryPhrase = EF.Functions.FullTextQueryPhrase(query, otherQuery),
                QueryNot = EF.Functions.FullTextQueryNot(query),
                NetworkNot = EF.Functions.NetworkBitwiseNot(network),
                NetworkAnd = EF.Functions.NetworkBitwiseAnd(network, otherNetwork),
                NetworkOr = EF.Functions.NetworkBitwiseOr(network, otherNetwork),
                NetworkAdd = EF.Functions.NetworkAdd(network, 2),
                ReverseNetworkAdd = EF.Functions.NetworkAdd(2, network),
                NetworkSubtract = EF.Functions.NetworkSubtract(network, 2),
                NetworkDistance = EF.Functions.NetworkDistance(network, network),
                BitConcat = EF.Functions.BitStringConcatenate(bits, otherBits),
                BitAnd = EF.Functions.BitStringAnd(bits, otherBits),
                BitOr = EF.Functions.BitStringOr(bits, otherBits),
                BitXor = EF.Functions.BitStringXor(bits, otherBits),
                BitNot = EF.Functions.BitStringNot(bits),
                BitShiftLeft = EF.Functions.BitStringShiftLeft(bits, 1),
                BitShiftRight = EF.Functions.BitStringShiftRight(bits, 1),
            })
            .ToQueryString();

        Assert.Contains(" || ", sql, StringComparison.Ordinal);
        Assert.Contains(" + ", sql, StringComparison.Ordinal);
        Assert.Contains(" * ", sql, StringComparison.Ordinal);
        Assert.Contains(" - ", sql, StringComparison.Ordinal);
        Assert.Contains(" && ", sql, StringComparison.Ordinal);
        Assert.Contains(" <-> ", sql, StringComparison.Ordinal);
        Assert.Contains("(!! ", sql, StringComparison.Ordinal);
        Assert.Contains("(~ ", sql, StringComparison.Ordinal);
        Assert.Contains(" & ", sql, StringComparison.Ordinal);
        Assert.Contains(" | ", sql, StringComparison.Ordinal);
        Assert.Contains(" -> ", sql, StringComparison.Ordinal);
        Assert.Contains(" ->> ", sql, StringComparison.Ordinal);
        Assert.Contains(" #> ", sql, StringComparison.Ordinal);
        Assert.Contains(" #>> ", sql, StringComparison.Ordinal);
        Assert.Contains(" #- ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Geometric_predicate_and_value_operators_translate_with_exact_tokens()
    {
        using var context = CreateContext();
        var point = new BlueTuskPoint(0, 0);
        var otherPoint = new BlueTuskPoint(3, 4);
        var segment = new BlueTuskLineSegment(point, otherPoint);
        var otherSegment = new BlueTuskLineSegment(new BlueTuskPoint(0, 4), new BlueTuskPoint(3, 0));
        var line = new BlueTuskLine(1, 0, 0);
        var otherLine = new BlueTuskLine(0, 1, 0);
        var box = new BlueTuskBox(point, otherPoint);
        var otherBox = new BlueTuskBox(new BlueTuskPoint(2, 2), new BlueTuskPoint(5, 5));
        var path = new BlueTuskPath([point, otherPoint], isClosed: false);
        var otherPath = new BlueTuskPath([new BlueTuskPoint(4, 0), point], isClosed: false);
        var polygon = new BlueTuskPolygon([point, new BlueTuskPoint(4, 0), new BlueTuskPoint(0, 4)]);
        var circle = new BlueTuskCircle(point, 5);

        var predicateSql = context.Documents
            .Where(_ =>
                EF.Functions.GeometryIsStrictlyLeftOf(point, otherPoint)
                || EF.Functions.GeometryIsStrictlyRightOf(otherBox, box)
                || EF.Functions.GeometryIsStrictlyBelow(point, otherPoint)
                || EF.Functions.GeometryIsStrictlyAbove(otherPoint, point)
                || EF.Functions.GeometryDoesNotExtendRightOf(box, otherBox)
                || EF.Functions.GeometryDoesNotExtendLeftOf(otherBox, box)
                || EF.Functions.GeometryDoesNotExtendAbove(box, otherBox)
                || EF.Functions.GeometryDoesNotExtendBelow(otherBox, box)
                || EF.Functions.GeometryOverlaps(box, otherBox)
                || EF.Functions.GeometrySameAs(point, point)
                || EF.Functions.GeometryEqual(box, box)
                || EF.Functions.GeometryNotEqual(point, otherPoint)
                || EF.Functions.GeometryLessThan(box, otherBox)
                || EF.Functions.GeometryLessThanOrEqual(box, otherBox)
                || EF.Functions.GeometryGreaterThan(otherBox, box)
                || EF.Functions.GeometryGreaterThanOrEqual(otherBox, box)
                || EF.Functions.GeometryContains(polygon, point)
                || EF.Functions.GeometryContainedBy(point, circle)
                || EF.Functions.GeometryIntersects(segment, otherSegment)
                || EF.Functions.GeometryIsPerpendicular(line, otherLine)
                || EF.Functions.GeometryIsParallel(line, line)
                || EF.Functions.GeometryIsHorizontal(segment)
                || EF.Functions.GeometryIsHorizontal(point, otherPoint)
                || EF.Functions.GeometryIsVertical(line))
            .ToQueryString();
        var valueSql = context.Documents
            .Select(_ => new
            {
                Distance = EF.Functions.GeometryDistance(point, path),
                Intersection = EF.Functions.GeometryIntersection(segment, otherSegment),
                Closest = EF.Functions.GeometryClosestPoint(point, segment),
                Add = EF.Functions.PointAdd(point, otherPoint),
                Subtract = EF.Functions.PointSubtract(point, otherPoint),
                Multiply = EF.Functions.PointMultiply(point, otherPoint),
                Divide = EF.Functions.PointDivide(otherPoint, new BlueTuskPoint(1, 1)),
                PathTranslate = EF.Functions.PathTranslate(path, otherPoint),
                PathTranslateNegative = EF.Functions.PathTranslateNegative(path, otherPoint),
                PathScale = EF.Functions.PathScale(path, otherPoint),
                PathScaleInverse = EF.Functions.PathScaleInverse(path, new BlueTuskPoint(1, 1)),
                PathConcat = EF.Functions.PathConcatenate(path, otherPath),
                BoxTranslate = EF.Functions.BoxTranslate(box, otherPoint),
                BoxScale = EF.Functions.BoxScale(box, otherPoint),
                CircleTranslate = EF.Functions.CircleTranslate(circle, otherPoint),
                CircleScale = EF.Functions.CircleScale(circle, otherPoint),
            })
            .ToQueryString();

        foreach (var token in new[]
                 {
                     " << ", " >> ", " <<| ", " |>> ", " &< ", " &> ", " &<| ", " |&> ",
                     " && ", " ~= ", " = ", " <> ", " < ", " <= ", " > ", " >= ", " @> ", " <@ ",
                     " ?# ", " ?-| ", " ?|| ", "(?- ", " ?- ", "(?| ",
                 })
        {
            Assert.Contains(token, predicateSql, StringComparison.Ordinal);
        }

        foreach (var token in new[] { " <-> ", " # ", " ## ", " + ", " - ", " * ", " / " })
        {
            Assert.Contains(token, valueSql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PostgreSQL_native_operators_execute_with_typed_parameters()
    {
        var connectionString = GetConnectionString();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
        await ExecuteNonQueryAsync(
            dataSource,
            """
            DROP TABLE IF EXISTS "ef_operator_documents";
            CREATE TABLE "ef_operator_documents" (
                "Id" integer PRIMARY KEY,
                "Name" text NOT NULL,
                "Numbers" integer[] NOT NULL,
                "Range" int4range NOT NULL,
                "Multirange" int4multirange NOT NULL,
                "Json" jsonb NOT NULL,
                "Network" inet NOT NULL,
                "SearchVector" tsvector NOT NULL);
            INSERT INTO "ef_operator_documents"
                ("Id", "Name", "Numbers", "Range", "Multirange", "Json", "Network", "SearchVector")
            VALUES (
                1,
                'BlueTusk Provider',
                ARRAY[1, 2, 3],
                '[1,10)'::int4range,
                '{[1,4),[7,10)}'::int4multirange,
                '{"kind":"provider","version":3}'::jsonb,
                '10.0.0.42/24'::inet,
                to_tsvector('simple', 'BlueTusk PostgreSQL provider'))
            """);

        try
        {
            await using var context = CreateContext(dataSource);
            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.ILike(document.Name, "bluetusk%")
                    && EF.Functions.RegexIsMatch(document.Name, "^Blue")
                    && EF.Functions.RegexIsMatchInsensitive(document.Name, "^blue")
                    && EF.Functions.RegexIsNotMatch(document.Name, "^Other")
                    && EF.Functions.RegexIsNotMatchInsensitive(document.Name, "^other")));
            var containedNumbers = new[] { 1, 3 };
            var containingNumbers = new[] { 1, 2, 3, 4 };
            var overlappingNumbers = new[] { 3, 9 };
            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.ArrayContains(document.Numbers, containedNumbers)
                    && EF.Functions.ArrayContainedBy(document.Numbers, containingNumbers)
                    && EF.Functions.ArrayOverlaps(document.Numbers, overlappingNumbers)));

            var ids = new[] { 0, 1 };
            var otherIds = new[] { 2, 3 };
            var lowerIds = new[] { -1, 0 };
            var upperIds = new[] { 2, 3 };
            var exactIds = new[] { 1, 1 };
            var namePatterns = new[] { "Blue%", "%Provider" };
            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.EqualAny(document.Id, ids)
                    && EF.Functions.NotEqualAny(document.Id, ids)
                    && EF.Functions.LessThanAny(document.Id, upperIds)
                    && EF.Functions.LessThanOrEqualAny(document.Id, ids)
                    && EF.Functions.GreaterThanAny(document.Id, lowerIds)
                    && EF.Functions.GreaterThanOrEqualAny(document.Id, ids)
                    && EF.Functions.EqualAll(document.Id, exactIds)
                    && EF.Functions.NotEqualAll(document.Id, otherIds)
                    && EF.Functions.LessThanAll(document.Id, upperIds)
                    && EF.Functions.LessThanOrEqualAll(document.Id, upperIds)
                    && EF.Functions.GreaterThanAll(document.Id, lowerIds)
                    && EF.Functions.GreaterThanOrEqualAll(document.Id, exactIds)
                    && EF.Functions.LikeAny(document.Name, namePatterns)
                    && EF.Functions.ILikeAny(document.Name, namePatterns)
                    && EF.Functions.LikeAll(document.Name, namePatterns)
                    && EF.Functions.ILikeAll(document.Name, namePatterns)
                    && EF.Functions.RowEqual(
                        ValueTuple.Create(document.Id, document.Name),
                        ValueTuple.Create(1, "BlueTusk Provider"))
                    && EF.Functions.RowNotEqual(
                        ValueTuple.Create(document.Id, document.Name),
                        ValueTuple.Create(2, "BlueTusk Provider"))
                    && EF.Functions.RowLessThan(
                        ValueTuple.Create(document.Id, document.Name),
                        ValueTuple.Create(2, string.Empty))
                    && EF.Functions.RowLessThanOrEqual(
                        ValueTuple.Create(document.Id, document.Name),
                        ValueTuple.Create(1, "BlueTusk Provider"))
                    && EF.Functions.RowGreaterThan(
                        ValueTuple.Create(document.Id, document.Name),
                        ValueTuple.Create(0, "zzz"))
                    && EF.Functions.RowGreaterThanOrEqual(
                        ValueTuple.Create(document.Id, document.Name),
                        ValueTuple.Create(1, "BlueTusk Provider"))));

            var compiled = EF.CompileQuery(
                (DocumentContext database, int[] candidateIds, int cursorId, string cursorName) =>
                    database.Documents.Count(document =>
                        EF.Functions.EqualAny(document.Id, candidateIds)
                        && EF.Functions.RowGreaterThanOrEqual(
                            ValueTuple.Create(document.Id, document.Name),
                            ValueTuple.Create(cursorId, cursorName))));
            Assert.Equal(1, compiled(context, ids, 1, "BlueTusk Provider"));

            var containedRange = new BlueTuskRange<int>(2, 3);
            var containingRange = new BlueTuskRange<int>(0, 20);
            var overlappingRange = new BlueTuskRange<int>(9, 12);
            var rightRange = new BlueTuskRange<int>(20, 30);
            var leftRange = new BlueTuskRange<int>(-10, 0);
            var adjacentRange = new BlueTuskRange<int>(10, 12);
            var containedMultirange = new BlueTuskMultirange<int>([containedRange]);
            var containingMultirange = new BlueTuskMultirange<int>([containingRange]);
            var overlappingMultirange = new BlueTuskMultirange<int>([overlappingRange]);
            var rightMultirange = new BlueTuskMultirange<int>([rightRange]);
            var leftMultirange = new BlueTuskMultirange<int>([leftRange]);
            var adjacentMultirange = new BlueTuskMultirange<int>([adjacentRange]);
            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.RangeContains(document.Range, containedRange)
                    && EF.Functions.RangeContains(document.Range, containedMultirange)
                    && EF.Functions.RangeContains(document.Range, 5)
                    && EF.Functions.RangeContainedBy(document.Range, containingRange)
                    && EF.Functions.RangeContainedBy(document.Range, containingMultirange)
                    && EF.Functions.RangeOverlaps(document.Range, overlappingRange)
                    && EF.Functions.RangeOverlaps(document.Range, overlappingMultirange)
                    && EF.Functions.RangeIsStrictlyLeftOf(document.Range, rightRange)
                    && EF.Functions.RangeIsStrictlyLeftOf(document.Range, rightMultirange)
                    && EF.Functions.RangeIsStrictlyRightOf(document.Range, leftRange)
                    && EF.Functions.RangeIsStrictlyRightOf(document.Range, leftMultirange)
                    && EF.Functions.RangeIsAdjacentTo(document.Range, adjacentRange)
                    && EF.Functions.RangeIsAdjacentTo(document.Range, adjacentMultirange)
                    && EF.Functions.RangeDoesNotExtendRightOf(document.Range, rightRange)
                    && EF.Functions.RangeDoesNotExtendRightOf(document.Range, rightMultirange)
                    && EF.Functions.RangeDoesNotExtendLeftOf(document.Range, leftRange)
                    && EF.Functions.RangeDoesNotExtendLeftOf(document.Range, leftMultirange)));

            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.MultirangeContains(document.Multirange, containedMultirange)
                    && EF.Functions.MultirangeContains(document.Multirange, containedRange)
                    && EF.Functions.MultirangeContains(document.Multirange, 8)
                    && EF.Functions.MultirangeContainedBy(document.Multirange, containingMultirange)
                    && EF.Functions.MultirangeContainedBy(document.Multirange, containingRange)
                    && EF.Functions.MultirangeOverlaps(document.Multirange, overlappingMultirange)
                    && EF.Functions.MultirangeOverlaps(document.Multirange, overlappingRange)
                    && EF.Functions.MultirangeIsStrictlyLeftOf(document.Multirange, rightMultirange)
                    && EF.Functions.MultirangeIsStrictlyLeftOf(document.Multirange, rightRange)
                    && EF.Functions.MultirangeIsStrictlyRightOf(document.Multirange, leftMultirange)
                    && EF.Functions.MultirangeIsStrictlyRightOf(document.Multirange, leftRange)
                    && EF.Functions.MultirangeDoesNotExtendRightOf(document.Multirange, rightMultirange)
                    && EF.Functions.MultirangeDoesNotExtendRightOf(document.Multirange, rightRange)
                    && EF.Functions.MultirangeDoesNotExtendLeftOf(document.Multirange, leftMultirange)
                    && EF.Functions.MultirangeDoesNotExtendLeftOf(document.Multirange, leftRange)
                    && EF.Functions.MultirangeIsAdjacentTo(document.Multirange, adjacentMultirange)
                    && EF.Functions.MultirangeIsAdjacentTo(document.Multirange, adjacentRange)));

            var jsonFragment = """{"kind":"provider"}""";
            var jsonContainer = """{"kind":"provider","version":3,"stable":false}""";
            var anyJsonKeys = new[] { "missing", "version" };
            var allJsonKeys = new[] { "kind", "version" };
            var jsonPath = new BlueTuskJsonPath("$.version ? (@ > 2)");
            var jsonPredicate = new BlueTuskJsonPath("$.version > 2");
            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.JsonContains(document.Json, jsonFragment)
                    && EF.Functions.JsonContainedBy(document.Json, jsonContainer)
                    && EF.Functions.JsonExists(document.Json, "kind")
                    && EF.Functions.JsonExistsAny(document.Json, anyJsonKeys)
                    && EF.Functions.JsonExistsAll(document.Json, allJsonKeys)
                    && EF.Functions.JsonPathExists(document.Json, jsonPath)
                    && EF.Functions.JsonPathMatches(document.Json, jsonPredicate)));

            var containedAddress = BlueTuskNetworkAddress.Parse("10.0.0.99");
            var containingNetwork = BlueTuskNetworkAddress.Parse("10.0.0.0/16");
            var overlappingNetwork = BlueTuskNetworkAddress.Parse("10.0.0.0/25");
            var textSearchQuery = BlueTuskTextSearchQuery.Parse("'provider'");
            Assert.Equal(
                1,
                await context.Documents.CountAsync(document =>
                    EF.Functions.NetworkContains(document.Network, containedAddress)
                    && EF.Functions.NetworkContainedBy(document.Network, containingNetwork)
                    && EF.Functions.NetworkOverlaps(document.Network, overlappingNetwork)
                    && EF.Functions.NetworkStrictlyContains(document.Network, containedAddress)
                    && EF.Functions.NetworkStrictlyContainedBy(document.Network, containingNetwork)
                    && EF.Functions.FullTextMatches(document.SearchVector, textSearchQuery)
                    && EF.Functions.FullTextQueryContains(textSearchQuery, textSearchQuery)
                    && EF.Functions.FullTextQueryContainedBy(textSearchQuery, textSearchQuery)));

            var leftNumbers = new[] { 1, 2 };
            var rightNumbers = new[] { 3, 4 };
            var scalarLeftRange = new BlueTuskRange<int>(1, 5);
            var scalarRightRange = new BlueTuskRange<int>(4, 8);
            var scalarLeftMultirange = new BlueTuskMultirange<int>([scalarLeftRange]);
            var scalarRightMultirange = new BlueTuskMultirange<int>([scalarRightRange]);
            var leftQuery = BlueTuskTextSearchQuery.Parse("'blue'");
            var rightQuery = BlueTuskTextSearchQuery.Parse("'tusk'");
            var leftVector = BlueTuskTextSearchVector.Parse("'blue':1");
            var rightVector = BlueTuskTextSearchVector.Parse("'tusk':1");
            var scalarNetwork = BlueTuskNetworkAddress.Parse("10.0.0.4/24");
            var netmask = BlueTuskNetworkAddress.Parse("255.255.255.0");
            var deletePath = new[] { "a", "b" };
            var bits = new BlueTuskBitString("101");
            var otherBits = new BlueTuskBitString("011");
            var scalarValues = await context.Documents
                .Select(_ => new
                {
                    ArrayConcat = EF.Functions.ArrayConcatenate(leftNumbers, rightNumbers),
                    ArrayAppend = EF.Functions.ArrayAppend(leftNumbers, 3),
                    ArrayPrepend = EF.Functions.ArrayPrepend(0, leftNumbers),
                    RangeUnion = EF.Functions.RangeUnion(scalarLeftRange, scalarRightRange),
                    RangeIntersect = EF.Functions.RangeIntersect(scalarLeftRange, scalarRightRange),
                    RangeExcept = EF.Functions.RangeExcept(scalarLeftRange, scalarRightRange),
                    MultirangeUnion = EF.Functions.MultirangeUnion(
                        scalarLeftMultirange,
                        scalarRightMultirange),
                    MultirangeIntersect = EF.Functions.MultirangeIntersect(
                        scalarLeftMultirange,
                        scalarRightMultirange),
                    MultirangeExcept = EF.Functions.MultirangeExcept(
                        scalarLeftMultirange,
                        scalarRightMultirange),
                    JsonConcat = EF.Functions.JsonConcatenate("{\"left\":1}", "{\"right\":2}"),
                    JsonDeleteKey = EF.Functions.JsonDelete("{\"keep\":1,\"drop\":2}", "drop"),
                    JsonDeleteIndex = EF.Functions.JsonDelete("[1,2]", 0),
                    JsonDeletePath = EF.Functions.JsonDeletePath(
                        "{\"a\":{\"b\":1,\"c\":2}}",
                        deletePath),
                    JsonGet = EF.Functions.JsonGet("{\"a\":1}", "a"),
                    JsonGetIndex = EF.Functions.JsonGet("[1,2]", 1),
                    JsonGetText = EF.Functions.JsonGetText("{\"a\":1}", "a"),
                    JsonGetIndexText = EF.Functions.JsonGetText("[1,2]", 1),
                    JsonGetPath = EF.Functions.JsonGetPath("{\"a\":{\"b\":1}}", deletePath),
                    JsonGetPathText = EF.Functions.JsonGetPathText(
                        "{\"a\":{\"b\":1}}",
                        deletePath),
                    VectorConcat = EF.Functions.FullTextVectorConcatenate(leftVector, rightVector),
                    QueryAnd = EF.Functions.FullTextQueryAnd(leftQuery, rightQuery),
                    QueryOr = EF.Functions.FullTextQueryOr(leftQuery, rightQuery),
                    QueryPhrase = EF.Functions.FullTextQueryPhrase(leftQuery, rightQuery),
                    QueryNot = EF.Functions.FullTextQueryNot(leftQuery),
                    NetworkNot = EF.Functions.NetworkBitwiseNot(scalarNetwork),
                    NetworkAnd = EF.Functions.NetworkBitwiseAnd(scalarNetwork, netmask),
                    NetworkOr = EF.Functions.NetworkBitwiseOr(scalarNetwork, netmask),
                    NetworkAdd = EF.Functions.NetworkAdd(scalarNetwork, 2),
                    ReverseNetworkAdd = EF.Functions.NetworkAdd(2, scalarNetwork),
                    NetworkSubtract = EF.Functions.NetworkSubtract(scalarNetwork, 2),
                    NetworkDistance = EF.Functions.NetworkDistance(scalarNetwork, scalarNetwork),
                    BitConcat = EF.Functions.BitStringConcatenate(bits, otherBits),
                    BitAnd = EF.Functions.BitStringAnd(bits, otherBits),
                    BitOr = EF.Functions.BitStringOr(bits, otherBits),
                    BitXor = EF.Functions.BitStringXor(bits, otherBits),
                    BitNot = EF.Functions.BitStringNot(bits),
                    BitShiftLeft = EF.Functions.BitStringShiftLeft(bits, 1),
                    BitShiftRight = EF.Functions.BitStringShiftRight(bits, 1),
                })
                .SingleAsync();

            Assert.Equal([1, 2, 3, 4], scalarValues.ArrayConcat);
            Assert.Equal([1, 2, 3], scalarValues.ArrayAppend);
            Assert.Equal([0, 1, 2], scalarValues.ArrayPrepend);
            Assert.Equal(new BlueTuskRange<int>(1, 8), scalarValues.RangeUnion);
            Assert.Equal(new BlueTuskRange<int>(4, 5), scalarValues.RangeIntersect);
            Assert.Equal(new BlueTuskRange<int>(1, 4), scalarValues.RangeExcept);
            Assert.Equal("{\"left\": 1, \"right\": 2}", scalarValues.JsonConcat);
            Assert.Equal("{\"keep\": 1}", scalarValues.JsonDeleteKey);
            Assert.Equal("[2]", scalarValues.JsonDeleteIndex);
            Assert.Equal("{\"a\": {\"c\": 2}}", scalarValues.JsonDeletePath);
            Assert.Equal("1", scalarValues.JsonGet);
            Assert.Equal("2", scalarValues.JsonGetIndex);
            Assert.Equal("1", scalarValues.JsonGetText);
            Assert.Equal("2", scalarValues.JsonGetIndexText);
            Assert.Equal("1", scalarValues.JsonGetPath);
            Assert.Equal("1", scalarValues.JsonGetPathText);
            Assert.Equal(0, scalarValues.NetworkDistance);
            Assert.Equal("10.0.0.6/24", scalarValues.NetworkAdd.ToString());
            Assert.Equal(scalarValues.NetworkAdd, scalarValues.ReverseNetworkAdd);
            Assert.Equal("10.0.0.2/24", scalarValues.NetworkSubtract.ToString());
            Assert.Equal("101011", scalarValues.BitConcat.ToString());
            Assert.Equal("001", scalarValues.BitAnd.ToString());
            Assert.Equal("111", scalarValues.BitOr.ToString());
            Assert.Equal("110", scalarValues.BitXor.ToString());
            Assert.Equal("010", scalarValues.BitNot.ToString());
            Assert.Equal("010", scalarValues.BitShiftLeft.ToString());
            Assert.Equal("010", scalarValues.BitShiftRight.ToString());

            var origin = new BlueTuskPoint(0, 0);
            var threeFour = new BlueTuskPoint(3, 4);
            var horizontal = new BlueTuskLineSegment(origin, new BlueTuskPoint(4, 0));
            var vertical = new BlueTuskLineSegment(new BlueTuskPoint(2, -2), new BlueTuskPoint(2, 2));
            var parallel = new BlueTuskLineSegment(new BlueTuskPoint(0, 1), new BlueTuskPoint(4, 1));
            var xAxis = new BlueTuskLine(0, 1, 0);
            var yAxis = new BlueTuskLine(1, 0, 0);
            var lowerLeftBox = new BlueTuskBox(new BlueTuskPoint(-4, -4), new BlueTuskPoint(-2, -2));
            var upperRightBox = new BlueTuskBox(new BlueTuskPoint(2, 2), new BlueTuskPoint(4, 4));
            var overlappingBox = new BlueTuskBox(new BlueTuskPoint(3, 3), new BlueTuskPoint(5, 5));
            var smallBox = new BlueTuskBox(origin, new BlueTuskPoint(1, 1));
            var largeBox = new BlueTuskBox(origin, new BlueTuskPoint(4, 4));
            var path = new BlueTuskPath([origin, threeFour], isClosed: false);
            var otherPath = new BlueTuskPath([threeFour, new BlueTuskPoint(5, 5)], isClosed: false);
            Assert.Equal(
                1,
                await context.Documents.CountAsync(_ =>
                    EF.Functions.GeometryIsStrictlyLeftOf(lowerLeftBox, upperRightBox)
                    && EF.Functions.GeometryIsStrictlyRightOf(upperRightBox, lowerLeftBox)
                    && EF.Functions.GeometryIsStrictlyBelow(lowerLeftBox, upperRightBox)
                    && EF.Functions.GeometryIsStrictlyAbove(upperRightBox, lowerLeftBox)
                    && EF.Functions.GeometryDoesNotExtendRightOf(lowerLeftBox, upperRightBox)
                    && EF.Functions.GeometryDoesNotExtendLeftOf(upperRightBox, lowerLeftBox)
                    && EF.Functions.GeometryDoesNotExtendAbove(lowerLeftBox, upperRightBox)
                    && EF.Functions.GeometryDoesNotExtendBelow(upperRightBox, lowerLeftBox)
                    && EF.Functions.GeometryOverlaps(upperRightBox, overlappingBox)
                    && EF.Functions.GeometrySameAs(origin, origin)
                    && EF.Functions.GeometryEqual(upperRightBox, upperRightBox)
                    && EF.Functions.GeometryNotEqual(origin, threeFour)
                    && EF.Functions.GeometryLessThan(smallBox, largeBox)
                    && EF.Functions.GeometryLessThanOrEqual(smallBox, largeBox)
                    && EF.Functions.GeometryGreaterThan(largeBox, smallBox)
                    && EF.Functions.GeometryGreaterThanOrEqual(largeBox, smallBox)
                    && EF.Functions.GeometryContains(upperRightBox, threeFour)
                    && EF.Functions.GeometryContainedBy(threeFour, upperRightBox)
                    && EF.Functions.GeometryIntersects(horizontal, vertical)
                    && EF.Functions.GeometryIsPerpendicular(horizontal, vertical)
                    && EF.Functions.GeometryIsParallel(horizontal, parallel)
                    && EF.Functions.GeometryIsHorizontal(horizontal)
                    && EF.Functions.GeometryIsHorizontal(origin, new BlueTuskPoint(4, 0))
                    && EF.Functions.GeometryIsVertical(vertical)));

            var geometricValues = await context.Documents
                .Select(_ => new
                {
                    Distance = EF.Functions.GeometryDistance(origin, threeFour),
                    SegmentIntersection = EF.Functions.GeometryIntersection(horizontal, vertical),
                    LineIntersection = EF.Functions.GeometryIntersection(xAxis, yAxis),
                    Closest = EF.Functions.GeometryClosestPoint(threeFour, horizontal),
                    Add = EF.Functions.PointAdd(origin, threeFour),
                    Subtract = EF.Functions.PointSubtract(threeFour, new BlueTuskPoint(1, 1)),
                    Multiply = EF.Functions.PointMultiply(threeFour, new BlueTuskPoint(2, 2)),
                    Divide = EF.Functions.PointDivide(threeFour, new BlueTuskPoint(3, 2)),
                    PathTranslate = EF.Functions.PathTranslate(path, new BlueTuskPoint(1, 1)),
                    PathTranslateNegative = EF.Functions.PathTranslateNegative(
                        path,
                        new BlueTuskPoint(1, 1)),
                    PathScale = EF.Functions.PathScale(path, new BlueTuskPoint(2, 2)),
                    PathScaleInverse = EF.Functions.PathScaleInverse(path, new BlueTuskPoint(2, 2)),
                    PathConcat = EF.Functions.PathConcatenate(path, otherPath),
                    BoxTranslate = EF.Functions.BoxTranslate(upperRightBox, new BlueTuskPoint(1, 1)),
                    BoxScale = EF.Functions.BoxScale(upperRightBox, new BlueTuskPoint(2, 2)),
                    CircleTranslate = EF.Functions.CircleTranslate(
                        new BlueTuskCircle(origin, 2),
                        new BlueTuskPoint(1, 1)),
                    CircleScale = EF.Functions.CircleScale(
                        new BlueTuskCircle(origin, 2),
                        new BlueTuskPoint(2, 2)),
                })
                .SingleAsync();

            Assert.Equal(5, geometricValues.Distance);
            Assert.Equal(new BlueTuskPoint(2, 0), geometricValues.SegmentIntersection);
            Assert.Equal(origin, geometricValues.LineIntersection);
            Assert.Equal(new BlueTuskPoint(3, 0), geometricValues.Closest);
            Assert.Equal(threeFour, geometricValues.Add);
            Assert.Equal(new BlueTuskPoint(2, 3), geometricValues.Subtract);
            Assert.Equal(new BlueTuskPoint(-2, 14), geometricValues.Multiply);
            Assert.Equal(17d / 13, geometricValues.Divide.X, precision: 10);
            Assert.Equal(6d / 13, geometricValues.Divide.Y, precision: 10);
        }
        finally
        {
            await ExecuteNonQueryAsync(dataSource, "DROP TABLE IF EXISTS \"ef_operator_documents\"");
        }
    }

    private static DocumentContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DocumentContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new DocumentContext(options);
    }

    private static DocumentContext CreateContext(BlueTuskDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<DocumentContext>()
            .UseBlueTusk(dataSource)
            .Options;
        return new DocumentContext(options);
    }

    private static async Task ExecuteNonQueryAsync(BlueTuskDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        _ = await command.ExecuteNonQueryAsync();
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

    private sealed class DocumentContext(DbContextOptions<DocumentContext> options) : DbContext(options)
    {
        public DbSet<Document> Documents => Set<Document>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var document = modelBuilder.Entity<Document>();
            document.ToTable("ef_operator_documents");
            document.Property(value => value.Json).HasColumnType("jsonb");
        }
    }

    private sealed class Document
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public int[] Numbers { get; set; } = [];

        public BlueTuskRange<int> Range { get; set; }

        public BlueTuskMultirange<int> Multirange { get; set; } = new([]);

        public string Json { get; set; } = "{}";

        public BlueTuskNetworkAddress Network { get; set; }

        public BlueTuskTextSearchVector SearchVector { get; set; } = new([]);
    }
}
