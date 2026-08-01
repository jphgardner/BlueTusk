using System.Runtime.CompilerServices;
using BlueTusk.TypeSystem;

namespace Microsoft.EntityFrameworkCore;

/// <summary>PostgreSQL-native predicates translated by the BlueTusk EF Core provider.</summary>
public static class BlueTuskDbFunctionsExtensions
{
    public static bool ILike(this DbFunctions _, string matchExpression, string pattern)
        => ThrowTranslationOnly<bool>();

    public static bool RegexIsMatch(this DbFunctions _, string matchExpression, string pattern)
        => ThrowTranslationOnly<bool>();

    public static bool RegexIsMatchInsensitive(this DbFunctions _, string matchExpression, string pattern)
        => ThrowTranslationOnly<bool>();

    public static bool ArrayContains<T>(this DbFunctions _, T[] array, T[] contained)
        => ThrowTranslationOnly<bool>();

    public static bool ArrayContainedBy<T>(this DbFunctions _, T[] array, T[] container)
        => ThrowTranslationOnly<bool>();

    public static bool ArrayOverlaps<T>(this DbFunctions _, T[] left, T[] right)
        => ThrowTranslationOnly<bool>();

    public static bool EqualAny<T>(this DbFunctions _, T item, T[] values)
        => ThrowTranslationOnly<bool>();

    public static bool NotEqualAny<T>(this DbFunctions _, T item, T[] values)
        => ThrowTranslationOnly<bool>();

    public static bool LessThanAny<T>(this DbFunctions _, T item, T[] values)
        => ThrowTranslationOnly<bool>();

    public static bool LessThanOrEqualAny<T>(this DbFunctions _, T item, T[] values)
        => ThrowTranslationOnly<bool>();

    public static bool GreaterThanAny<T>(this DbFunctions _, T item, T[] values)
        => ThrowTranslationOnly<bool>();

    public static bool GreaterThanOrEqualAny<T>(this DbFunctions _, T item, T[] values)
        => ThrowTranslationOnly<bool>();

    public static bool EqualAll<T>(this DbFunctions _, T item, T[] values)
        => ThrowTranslationOnly<bool>();

    public static bool NotEqualAll<T>(this DbFunctions _, T item, T[] values)
        => ThrowTranslationOnly<bool>();

    public static bool LessThanAll<T>(this DbFunctions _, T item, T[] values)
        => ThrowTranslationOnly<bool>();

    public static bool LessThanOrEqualAll<T>(this DbFunctions _, T item, T[] values)
        => ThrowTranslationOnly<bool>();

    public static bool GreaterThanAll<T>(this DbFunctions _, T item, T[] values)
        => ThrowTranslationOnly<bool>();

    public static bool GreaterThanOrEqualAll<T>(this DbFunctions _, T item, T[] values)
        => ThrowTranslationOnly<bool>();

    public static bool LikeAny(this DbFunctions _, string item, string[] patterns)
        => ThrowTranslationOnly<bool>();

    public static bool ILikeAny(this DbFunctions _, string item, string[] patterns)
        => ThrowTranslationOnly<bool>();

    public static bool LikeAll(this DbFunctions _, string item, string[] patterns)
        => ThrowTranslationOnly<bool>();

    public static bool ILikeAll(this DbFunctions _, string item, string[] patterns)
        => ThrowTranslationOnly<bool>();

    public static bool RowEqual(this DbFunctions _, ITuple left, ITuple right)
        => ThrowTranslationOnly<bool>();

    public static bool RowNotEqual(this DbFunctions _, ITuple left, ITuple right)
        => ThrowTranslationOnly<bool>();

    public static bool RowLessThan(this DbFunctions _, ITuple left, ITuple right)
        => ThrowTranslationOnly<bool>();

    public static bool RowLessThanOrEqual(this DbFunctions _, ITuple left, ITuple right)
        => ThrowTranslationOnly<bool>();

    public static bool RowGreaterThan(this DbFunctions _, ITuple left, ITuple right)
        => ThrowTranslationOnly<bool>();

    public static bool RowGreaterThanOrEqual(this DbFunctions _, ITuple left, ITuple right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeContains<T>(
        this DbFunctions _,
        BlueTuskRange<T> range,
        BlueTuskRange<T> contained)
        => ThrowTranslationOnly<bool>();

    public static bool RangeContains<T>(this DbFunctions _, BlueTuskRange<T> range, T element)
        => ThrowTranslationOnly<bool>();

    public static bool RangeContainedBy<T>(
        this DbFunctions _,
        BlueTuskRange<T> range,
        BlueTuskRange<T> container)
        => ThrowTranslationOnly<bool>();

    public static bool RangeOverlaps<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsStrictlyLeftOf<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsStrictlyRightOf<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsAdjacentTo<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeContains<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> multirange,
        BlueTuskMultirange<T> contained)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeContains<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> multirange,
        BlueTuskRange<T> contained)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeContains<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> multirange,
        T element)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeContainedBy<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> multirange,
        BlueTuskMultirange<T> container)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeOverlaps<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool JsonContains(this DbFunctions _, string jsonb, string contained)
        => ThrowTranslationOnly<bool>();

    public static bool JsonContainedBy(this DbFunctions _, string jsonb, string container)
        => ThrowTranslationOnly<bool>();

    public static bool JsonExists(this DbFunctions _, string jsonb, string key)
        => ThrowTranslationOnly<bool>();

    public static bool JsonExistsAny(this DbFunctions _, string jsonb, string[] keys)
        => ThrowTranslationOnly<bool>();

    public static bool JsonExistsAll(this DbFunctions _, string jsonb, string[] keys)
        => ThrowTranslationOnly<bool>();

    public static bool JsonPathExists(this DbFunctions _, string jsonb, BlueTuskJsonPath path)
        => ThrowTranslationOnly<bool>();

    public static bool JsonPathMatches(this DbFunctions _, string jsonb, BlueTuskJsonPath predicate)
        => ThrowTranslationOnly<bool>();

    public static bool FullTextMatches(
        this DbFunctions _,
        BlueTuskTextSearchVector document,
        BlueTuskTextSearchQuery query)
        => ThrowTranslationOnly<bool>();

    public static bool NetworkContains(
        this DbFunctions _,
        BlueTuskNetworkAddress network,
        BlueTuskNetworkAddress address)
        => ThrowTranslationOnly<bool>();

    public static bool NetworkContainedBy(
        this DbFunctions _,
        BlueTuskNetworkAddress address,
        BlueTuskNetworkAddress network)
        => ThrowTranslationOnly<bool>();

    public static bool NetworkOverlaps(
        this DbFunctions _,
        BlueTuskNetworkAddress left,
        BlueTuskNetworkAddress right)
        => ThrowTranslationOnly<bool>();

    public static int? ArrayLength<T>(this DbFunctions _, T[] array, int dimension)
        => ThrowTranslationOnly<int?>();

    public static int? ArrayLowerBound<T>(this DbFunctions _, T[] array, int dimension)
        => ThrowTranslationOnly<int?>();

    public static int? ArrayUpperBound<T>(this DbFunctions _, T[] array, int dimension)
        => ThrowTranslationOnly<int?>();

    public static int? ArrayCardinality<T>(this DbFunctions _, T[] array)
        => ThrowTranslationOnly<int?>();

    public static IQueryable<string> JsonArrayElements(this DbFunctions _, string jsonb)
        => ThrowTranslationOnly<IQueryable<string>>();

    public static IQueryable<string?> JsonArrayElementsText(this DbFunctions _, string jsonb)
        => ThrowTranslationOnly<IQueryable<string?>>();

    public static IQueryable<string> JsonObjectKeys(this DbFunctions _, string jsonb)
        => ThrowTranslationOnly<IQueryable<string>>();

    public static IQueryable<KeyValuePair<string, string>> JsonEach(
        this DbFunctions _,
        string jsonb)
        => ThrowTranslationOnly<IQueryable<KeyValuePair<string, string>>>();

    public static IQueryable<KeyValuePair<string, string?>> JsonEachText(
        this DbFunctions _,
        string jsonb)
        => ThrowTranslationOnly<IQueryable<KeyValuePair<string, string?>>>();

    public static IQueryable<KeyValuePair<int?, string?>> Unnest(
        this DbFunctions _,
        int[] first,
        string?[] second)
        => ThrowTranslationOnly<IQueryable<KeyValuePair<int?, string?>>>();

    public static IQueryable<T> JsonToRecordset<T>(this DbFunctions _, string jsonb)
        where T : class
        => ThrowTranslationOnly<IQueryable<T>>();

    public static IQueryable<string> JsonPathQuery(
        this DbFunctions _,
        string jsonb,
        BlueTuskJsonPath path)
        => ThrowTranslationOnly<IQueryable<string>>();

    public static IQueryable<int> GenerateSeries(this DbFunctions _, int start, int stop)
        => ThrowTranslationOnly<IQueryable<int>>();

    public static IQueryable<int> GenerateSeries(this DbFunctions _, int start, int stop, int step)
        => ThrowTranslationOnly<IQueryable<int>>();

    public static IQueryable<long> GenerateSeries(this DbFunctions _, long start, long stop)
        => ThrowTranslationOnly<IQueryable<long>>();

    public static IQueryable<long> GenerateSeries(this DbFunctions _, long start, long stop, long step)
        => ThrowTranslationOnly<IQueryable<long>>();

    public static IQueryable<decimal> GenerateSeries(this DbFunctions _, decimal start, decimal stop)
        => ThrowTranslationOnly<IQueryable<decimal>>();

    public static IQueryable<decimal> GenerateSeries(
        this DbFunctions _,
        decimal start,
        decimal stop,
        decimal step)
        => ThrowTranslationOnly<IQueryable<decimal>>();

    public static IQueryable<DateTime> GenerateSeries(
        this DbFunctions _,
        DateTime start,
        DateTime stop,
        TimeSpan step)
        => ThrowTranslationOnly<IQueryable<DateTime>>();

    public static IQueryable<DateTimeOffset> GenerateSeries(
        this DbFunctions _,
        DateTimeOffset start,
        DateTimeOffset stop,
        TimeSpan step)
        => ThrowTranslationOnly<IQueryable<DateTimeOffset>>();

    public static T? RangeLower<T>(this DbFunctions _, BlueTuskRange<T> range)
        where T : struct
        => ThrowTranslationOnly<T?>();

    public static T? RangeUpper<T>(this DbFunctions _, BlueTuskRange<T> range)
        where T : struct
        => ThrowTranslationOnly<T?>();

    public static bool RangeIsEmpty<T>(this DbFunctions _, BlueTuskRange<T> range)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsLowerInclusive<T>(this DbFunctions _, BlueTuskRange<T> range)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsUpperInclusive<T>(this DbFunctions _, BlueTuskRange<T> range)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsLowerInfinite<T>(this DbFunctions _, BlueTuskRange<T> range)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsUpperInfinite<T>(this DbFunctions _, BlueTuskRange<T> range)
        => ThrowTranslationOnly<bool>();

    public static T? MultirangeLower<T>(this DbFunctions _, BlueTuskMultirange<T> multirange)
        where T : struct
        => ThrowTranslationOnly<T?>();

    public static T? MultirangeUpper<T>(this DbFunctions _, BlueTuskMultirange<T> multirange)
        where T : struct
        => ThrowTranslationOnly<T?>();

    public static bool MultirangeIsEmpty<T>(this DbFunctions _, BlueTuskMultirange<T> multirange)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeIsLowerInclusive<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> multirange)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeIsUpperInclusive<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> multirange)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeIsLowerInfinite<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> multirange)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeIsUpperInfinite<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> multirange)
        => ThrowTranslationOnly<bool>();

    public static string? JsonTypeOf(this DbFunctions _, string jsonb)
        => ThrowTranslationOnly<string?>();

    public static int? JsonArrayLength(this DbFunctions _, string jsonb)
        => ThrowTranslationOnly<int?>();

    public static string? JsonPathQueryFirst(
        this DbFunctions _,
        string jsonb,
        BlueTuskJsonPath path)
        => ThrowTranslationOnly<string?>();

    public static string? RegexReplace(
        this DbFunctions _,
        string input,
        string pattern,
        string replacement)
        => ThrowTranslationOnly<string?>();

    public static int? RegexCount(this DbFunctions _, string input, string pattern)
        => ThrowTranslationOnly<int?>();

    public static string? NetworkHost(this DbFunctions _, BlueTuskNetworkAddress address)
        => ThrowTranslationOnly<string?>();

    public static int? NetworkAddressFamily(this DbFunctions _, BlueTuskNetworkAddress address)
        => ThrowTranslationOnly<int?>();

    public static int? NetworkMaskLength(this DbFunctions _, BlueTuskNetworkAddress address)
        => ThrowTranslationOnly<int?>();

    public static BlueTuskNetworkAddress NetworkPart(
        this DbFunctions _,
        BlueTuskNetworkAddress address)
        => ThrowTranslationOnly<BlueTuskNetworkAddress>();

    public static BlueTuskNetworkAddress NetworkBroadcast(
        this DbFunctions _,
        BlueTuskNetworkAddress address)
        => ThrowTranslationOnly<BlueTuskNetworkAddress>();

    public static BlueTuskTextSearchVector ToTextSearchVector(
        this DbFunctions _,
        string document)
        => ThrowTranslationOnly<BlueTuskTextSearchVector>();

    public static BlueTuskTextSearchQuery ToTextSearchQuery(
        this DbFunctions _,
        string query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery PlainToTextSearchQuery(
        this DbFunctions _,
        string query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery PhraseToTextSearchQuery(
        this DbFunctions _,
        string query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery WebSearchToTextSearchQuery(
        this DbFunctions _,
        string query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static int? TextSearchVectorLength(
        this DbFunctions _,
        BlueTuskTextSearchVector vector)
        => ThrowTranslationOnly<int?>();

    public static int? TextSearchQueryNodeCount(
        this DbFunctions _,
        BlueTuskTextSearchQuery query)
        => ThrowTranslationOnly<int?>();

    public static float? TextSearchRank(
        this DbFunctions _,
        BlueTuskTextSearchVector vector,
        BlueTuskTextSearchQuery query)
        => ThrowTranslationOnly<float?>();

    public static T[]? ArrayAggregate<T>(this DbFunctions _, IEnumerable<T> values)
        => ThrowTranslationOnly<T[]?>();

    public static string? StringAggregate(
        this DbFunctions _,
        IEnumerable<string> values,
        string delimiter)
        => ThrowTranslationOnly<string?>();

    public static bool? BooleanAnd(this DbFunctions _, IEnumerable<bool> values)
        => ThrowTranslationOnly<bool?>();

    public static bool? BooleanOr(this DbFunctions _, IEnumerable<bool> values)
        => ThrowTranslationOnly<bool?>();

    public static BlueTuskMultirange<T>? RangeAggregate<T>(
        this DbFunctions _,
        IEnumerable<BlueTuskRange<T>> ranges)
        => ThrowTranslationOnly<BlueTuskMultirange<T>?>();

    public static BlueTuskRange<T>? RangeIntersectAggregate<T>(
        this DbFunctions _,
        IEnumerable<BlueTuskRange<T>> ranges)
        => ThrowTranslationOnly<BlueTuskRange<T>?>();

    private static T ThrowTranslationOnly<T>()
        => throw new InvalidOperationException(
            "BlueTusk PostgreSQL database functions can only be used in translated EF Core queries.");
}
