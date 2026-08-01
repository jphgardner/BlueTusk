using System.Runtime.CompilerServices;
using BlueTusk.EntityFrameworkCore.Query;
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

    public static bool RegexIsNotMatch(this DbFunctions _, string matchExpression, string pattern)
        => ThrowTranslationOnly<bool>();

    public static bool RegexIsNotMatchInsensitive(this DbFunctions _, string matchExpression, string pattern)
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

    public static bool RangeContains<T>(
        this DbFunctions _,
        BlueTuskRange<T> range,
        BlueTuskMultirange<T> contained)
        => ThrowTranslationOnly<bool>();

    public static bool RangeContains<T>(this DbFunctions _, BlueTuskRange<T> range, T element)
        => ThrowTranslationOnly<bool>();

    public static bool RangeContainedBy<T>(
        this DbFunctions _,
        BlueTuskRange<T> range,
        BlueTuskRange<T> container)
        => ThrowTranslationOnly<bool>();

    public static bool RangeContainedBy<T>(
        this DbFunctions _,
        BlueTuskRange<T> range,
        BlueTuskMultirange<T> container)
        => ThrowTranslationOnly<bool>();

    public static bool RangeOverlaps<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeOverlaps<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsStrictlyLeftOf<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsStrictlyLeftOf<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsStrictlyRightOf<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsStrictlyRightOf<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsAdjacentTo<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeIsAdjacentTo<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeDoesNotExtendRightOf<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeDoesNotExtendRightOf<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeDoesNotExtendLeftOf<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool RangeDoesNotExtendLeftOf<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskMultirange<T> right)
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

    public static bool MultirangeContainedBy<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> multirange,
        BlueTuskRange<T> container)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeOverlaps<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeOverlaps<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeIsStrictlyLeftOf<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeIsStrictlyLeftOf<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeIsStrictlyRightOf<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeIsStrictlyRightOf<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeDoesNotExtendRightOf<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeDoesNotExtendRightOf<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeDoesNotExtendLeftOf<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeDoesNotExtendLeftOf<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeIsAdjacentTo<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<bool>();

    public static bool MultirangeIsAdjacentTo<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskRange<T> right)
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

    public static bool FullTextQueryContains(
        this DbFunctions _,
        BlueTuskTextSearchQuery query,
        BlueTuskTextSearchQuery contained)
        => ThrowTranslationOnly<bool>();

    public static bool FullTextQueryContainedBy(
        this DbFunctions _,
        BlueTuskTextSearchQuery query,
        BlueTuskTextSearchQuery container)
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

    public static bool NetworkStrictlyContains(
        this DbFunctions _,
        BlueTuskNetworkAddress network,
        BlueTuskNetworkAddress address)
        => ThrowTranslationOnly<bool>();

    public static bool NetworkStrictlyContainedBy(
        this DbFunctions _,
        BlueTuskNetworkAddress address,
        BlueTuskNetworkAddress network)
        => ThrowTranslationOnly<bool>();

    public static T[] ArrayConcatenate<T>(this DbFunctions _, T[] left, T[] right)
        => ThrowTranslationOnly<T[]>();

    public static T[] ArrayAppend<T>(this DbFunctions _, T[] array, T value)
        => ThrowTranslationOnly<T[]>();

    public static T[] ArrayPrepend<T>(this DbFunctions _, T value, T[] array)
        => ThrowTranslationOnly<T[]>();

    public static BlueTuskRange<T> RangeUnion<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<BlueTuskRange<T>>();

    public static BlueTuskRange<T> RangeIntersect<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<BlueTuskRange<T>>();

    public static BlueTuskRange<T> RangeExcept<T>(
        this DbFunctions _,
        BlueTuskRange<T> left,
        BlueTuskRange<T> right)
        => ThrowTranslationOnly<BlueTuskRange<T>>();

    public static BlueTuskMultirange<T> MultirangeUnion<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<BlueTuskMultirange<T>>();

    public static BlueTuskMultirange<T> MultirangeIntersect<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<BlueTuskMultirange<T>>();

    public static BlueTuskMultirange<T> MultirangeExcept<T>(
        this DbFunctions _,
        BlueTuskMultirange<T> left,
        BlueTuskMultirange<T> right)
        => ThrowTranslationOnly<BlueTuskMultirange<T>>();

    public static string JsonConcatenate(this DbFunctions _, string leftJsonb, string rightJsonb)
        => ThrowTranslationOnly<string>();

    public static string JsonDelete(this DbFunctions _, string jsonb, string key)
        => ThrowTranslationOnly<string>();

    public static string JsonDelete(this DbFunctions _, string jsonb, int arrayIndex)
        => ThrowTranslationOnly<string>();

    public static string JsonDeletePath(this DbFunctions _, string jsonb, string[] path)
        => ThrowTranslationOnly<string>();

    public static string? JsonGet(this DbFunctions _, string jsonb, string key)
        => ThrowTranslationOnly<string?>();

    public static string? JsonGet(this DbFunctions _, string jsonb, int arrayIndex)
        => ThrowTranslationOnly<string?>();

    public static string? JsonGetText(this DbFunctions _, string jsonb, string key)
        => ThrowTranslationOnly<string?>();

    public static string? JsonGetText(this DbFunctions _, string jsonb, int arrayIndex)
        => ThrowTranslationOnly<string?>();

    public static string? JsonGetPath(this DbFunctions _, string jsonb, string[] path)
        => ThrowTranslationOnly<string?>();

    public static string? JsonGetPathText(this DbFunctions _, string jsonb, string[] path)
        => ThrowTranslationOnly<string?>();

    public static BlueTuskTextSearchVector FullTextVectorConcatenate(
        this DbFunctions _,
        BlueTuskTextSearchVector left,
        BlueTuskTextSearchVector right)
        => ThrowTranslationOnly<BlueTuskTextSearchVector>();

    public static BlueTuskTextSearchQuery FullTextQueryAnd(
        this DbFunctions _,
        BlueTuskTextSearchQuery left,
        BlueTuskTextSearchQuery right)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery FullTextQueryOr(
        this DbFunctions _,
        BlueTuskTextSearchQuery left,
        BlueTuskTextSearchQuery right)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery FullTextQueryPhrase(
        this DbFunctions _,
        BlueTuskTextSearchQuery left,
        BlueTuskTextSearchQuery right)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery FullTextQueryNot(
        this DbFunctions _,
        BlueTuskTextSearchQuery query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskNetworkAddress NetworkBitwiseNot(
        this DbFunctions _,
        BlueTuskNetworkAddress address)
        => ThrowTranslationOnly<BlueTuskNetworkAddress>();

    public static BlueTuskNetworkAddress NetworkBitwiseAnd(
        this DbFunctions _,
        BlueTuskNetworkAddress left,
        BlueTuskNetworkAddress right)
        => ThrowTranslationOnly<BlueTuskNetworkAddress>();

    public static BlueTuskNetworkAddress NetworkBitwiseOr(
        this DbFunctions _,
        BlueTuskNetworkAddress left,
        BlueTuskNetworkAddress right)
        => ThrowTranslationOnly<BlueTuskNetworkAddress>();

    public static BlueTuskNetworkAddress NetworkAdd(
        this DbFunctions _,
        BlueTuskNetworkAddress address,
        long offset)
        => ThrowTranslationOnly<BlueTuskNetworkAddress>();

    public static BlueTuskNetworkAddress NetworkAdd(
        this DbFunctions _,
        long offset,
        BlueTuskNetworkAddress address)
        => ThrowTranslationOnly<BlueTuskNetworkAddress>();

    public static BlueTuskNetworkAddress NetworkSubtract(
        this DbFunctions _,
        BlueTuskNetworkAddress address,
        long offset)
        => ThrowTranslationOnly<BlueTuskNetworkAddress>();

    public static long NetworkDistance(
        this DbFunctions _,
        BlueTuskNetworkAddress left,
        BlueTuskNetworkAddress right)
        => ThrowTranslationOnly<long>();

    public static BlueTuskBitString BitStringConcatenate(
        this DbFunctions _,
        BlueTuskBitString left,
        BlueTuskBitString right)
        => ThrowTranslationOnly<BlueTuskBitString>();

    public static BlueTuskBitString BitStringAnd(
        this DbFunctions _,
        BlueTuskBitString left,
        BlueTuskBitString right)
        => ThrowTranslationOnly<BlueTuskBitString>();

    public static BlueTuskBitString BitStringOr(
        this DbFunctions _,
        BlueTuskBitString left,
        BlueTuskBitString right)
        => ThrowTranslationOnly<BlueTuskBitString>();

    public static BlueTuskBitString BitStringXor(
        this DbFunctions _,
        BlueTuskBitString left,
        BlueTuskBitString right)
        => ThrowTranslationOnly<BlueTuskBitString>();

    public static BlueTuskBitString BitStringNot(
        this DbFunctions _,
        BlueTuskBitString value)
        => ThrowTranslationOnly<BlueTuskBitString>();

    public static BlueTuskBitString BitStringShiftLeft(
        this DbFunctions _,
        BlueTuskBitString value,
        int count)
        => ThrowTranslationOnly<BlueTuskBitString>();

    public static BlueTuskBitString BitStringShiftRight(
        this DbFunctions _,
        BlueTuskBitString value,
        int count)
        => ThrowTranslationOnly<BlueTuskBitString>();

    public static bool GeometryIsStrictlyLeftOf(
        this DbFunctions _,
        BlueTuskPoint left,
        BlueTuskPoint right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyLeftOf(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyLeftOf(
        this DbFunctions _,
        BlueTuskPolygon left,
        BlueTuskPolygon right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyLeftOf(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyRightOf(
        this DbFunctions _,
        BlueTuskPoint left,
        BlueTuskPoint right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyRightOf(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyRightOf(
        this DbFunctions _,
        BlueTuskPolygon left,
        BlueTuskPolygon right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyRightOf(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyBelow(
        this DbFunctions _,
        BlueTuskPoint lower,
        BlueTuskPoint upper)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyBelow(
        this DbFunctions _,
        BlueTuskBox lower,
        BlueTuskBox upper)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyBelow(
        this DbFunctions _,
        BlueTuskPolygon lower,
        BlueTuskPolygon upper)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyBelow(
        this DbFunctions _,
        BlueTuskCircle lower,
        BlueTuskCircle upper)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyAbove(
        this DbFunctions _,
        BlueTuskPoint upper,
        BlueTuskPoint lower)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyAbove(
        this DbFunctions _,
        BlueTuskBox upper,
        BlueTuskBox lower)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyAbove(
        this DbFunctions _,
        BlueTuskPolygon upper,
        BlueTuskPolygon lower)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsStrictlyAbove(
        this DbFunctions _,
        BlueTuskCircle upper,
        BlueTuskCircle lower)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryDoesNotExtendRightOf(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryDoesNotExtendRightOf(
        this DbFunctions _,
        BlueTuskPolygon left,
        BlueTuskPolygon right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryDoesNotExtendRightOf(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryDoesNotExtendLeftOf(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryDoesNotExtendLeftOf(
        this DbFunctions _,
        BlueTuskPolygon left,
        BlueTuskPolygon right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryDoesNotExtendLeftOf(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryDoesNotExtendAbove(
        this DbFunctions _,
        BlueTuskBox lower,
        BlueTuskBox upper)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryDoesNotExtendAbove(
        this DbFunctions _,
        BlueTuskPolygon lower,
        BlueTuskPolygon upper)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryDoesNotExtendAbove(
        this DbFunctions _,
        BlueTuskCircle lower,
        BlueTuskCircle upper)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryDoesNotExtendBelow(
        this DbFunctions _,
        BlueTuskBox upper,
        BlueTuskBox lower)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryDoesNotExtendBelow(
        this DbFunctions _,
        BlueTuskPolygon upper,
        BlueTuskPolygon lower)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryDoesNotExtendBelow(
        this DbFunctions _,
        BlueTuskCircle upper,
        BlueTuskCircle lower)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryOverlaps(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryOverlaps(
        this DbFunctions _,
        BlueTuskPolygon left,
        BlueTuskPolygon right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryOverlaps(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometrySameAs(
        this DbFunctions _,
        BlueTuskPoint left,
        BlueTuskPoint right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometrySameAs(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometrySameAs(
        this DbFunctions _,
        BlueTuskPolygon left,
        BlueTuskPolygon right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometrySameAs(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryEqual(
        this DbFunctions _,
        BlueTuskLineSegment left,
        BlueTuskLineSegment right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryEqual(
        this DbFunctions _,
        BlueTuskPath left,
        BlueTuskPath right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryEqual(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryEqual(
        this DbFunctions _,
        BlueTuskLine left,
        BlueTuskLine right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryEqual(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryNotEqual(
        this DbFunctions _,
        BlueTuskPoint left,
        BlueTuskPoint right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryNotEqual(
        this DbFunctions _,
        BlueTuskLineSegment left,
        BlueTuskLineSegment right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryNotEqual(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryLessThan(
        this DbFunctions _,
        BlueTuskLineSegment left,
        BlueTuskLineSegment right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryLessThan(
        this DbFunctions _,
        BlueTuskPath left,
        BlueTuskPath right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryLessThan(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryLessThan(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryLessThanOrEqual(
        this DbFunctions _,
        BlueTuskLineSegment left,
        BlueTuskLineSegment right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryLessThanOrEqual(
        this DbFunctions _,
        BlueTuskPath left,
        BlueTuskPath right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryLessThanOrEqual(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryLessThanOrEqual(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryGreaterThan(
        this DbFunctions _,
        BlueTuskLineSegment left,
        BlueTuskLineSegment right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryGreaterThan(
        this DbFunctions _,
        BlueTuskPath left,
        BlueTuskPath right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryGreaterThan(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryGreaterThan(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryGreaterThanOrEqual(
        this DbFunctions _,
        BlueTuskLineSegment left,
        BlueTuskLineSegment right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryGreaterThanOrEqual(
        this DbFunctions _,
        BlueTuskPath left,
        BlueTuskPath right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryGreaterThanOrEqual(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryGreaterThanOrEqual(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContains(
        this DbFunctions _,
        BlueTuskPath container,
        BlueTuskPoint point)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContains(
        this DbFunctions _,
        BlueTuskBox container,
        BlueTuskPoint point)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContains(
        this DbFunctions _,
        BlueTuskBox container,
        BlueTuskBox contained)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContains(
        this DbFunctions _,
        BlueTuskPolygon container,
        BlueTuskPoint point)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContains(
        this DbFunctions _,
        BlueTuskPolygon container,
        BlueTuskPolygon contained)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContains(
        this DbFunctions _,
        BlueTuskCircle container,
        BlueTuskPoint point)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContains(
        this DbFunctions _,
        BlueTuskCircle container,
        BlueTuskCircle contained)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContainedBy(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskLineSegment container)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContainedBy(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskPath container)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContainedBy(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskBox container)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContainedBy(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskPolygon container)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContainedBy(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskLine container)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContainedBy(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskCircle container)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContainedBy(
        this DbFunctions _,
        BlueTuskLineSegment segment,
        BlueTuskBox container)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContainedBy(
        this DbFunctions _,
        BlueTuskLineSegment segment,
        BlueTuskLine container)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContainedBy(
        this DbFunctions _,
        BlueTuskBox value,
        BlueTuskBox container)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContainedBy(
        this DbFunctions _,
        BlueTuskPolygon value,
        BlueTuskPolygon container)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryContainedBy(
        this DbFunctions _,
        BlueTuskCircle value,
        BlueTuskCircle container)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIntersects(
        this DbFunctions _,
        BlueTuskLineSegment left,
        BlueTuskLineSegment right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIntersects(
        this DbFunctions _,
        BlueTuskLineSegment segment,
        BlueTuskBox box)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIntersects(
        this DbFunctions _,
        BlueTuskLineSegment segment,
        BlueTuskLine line)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIntersects(
        this DbFunctions _,
        BlueTuskPath left,
        BlueTuskPath right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIntersects(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIntersects(
        this DbFunctions _,
        BlueTuskLine line,
        BlueTuskBox box)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIntersects(
        this DbFunctions _,
        BlueTuskLine left,
        BlueTuskLine right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsPerpendicular(
        this DbFunctions _,
        BlueTuskLineSegment left,
        BlueTuskLineSegment right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsPerpendicular(
        this DbFunctions _,
        BlueTuskLine left,
        BlueTuskLine right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsParallel(
        this DbFunctions _,
        BlueTuskLineSegment left,
        BlueTuskLineSegment right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsParallel(
        this DbFunctions _,
        BlueTuskLine left,
        BlueTuskLine right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsHorizontal(this DbFunctions _, BlueTuskLineSegment value)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsHorizontal(this DbFunctions _, BlueTuskLine value)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsHorizontal(
        this DbFunctions _,
        BlueTuskPoint left,
        BlueTuskPoint right)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsVertical(this DbFunctions _, BlueTuskLineSegment value)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsVertical(this DbFunctions _, BlueTuskLine value)
        => ThrowTranslationOnly<bool>();

    public static bool GeometryIsVertical(
        this DbFunctions _,
        BlueTuskPoint left,
        BlueTuskPoint right)
        => ThrowTranslationOnly<bool>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskPoint left,
        BlueTuskPoint right)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskLineSegment segment)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskPath path)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskBox box)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskPolygon polygon)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskLine line)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskCircle circle)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskLineSegment segment,
        BlueTuskPoint point)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskLineSegment left,
        BlueTuskLineSegment right)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskLineSegment segment,
        BlueTuskBox box)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskLineSegment segment,
        BlueTuskLine line)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskPath path,
        BlueTuskPoint point)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskPath left,
        BlueTuskPath right)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskBox box,
        BlueTuskPoint point)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskBox box,
        BlueTuskLineSegment segment)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskPolygon polygon,
        BlueTuskPoint point)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskPolygon left,
        BlueTuskPolygon right)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskPolygon polygon,
        BlueTuskCircle circle)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskLine line,
        BlueTuskPoint point)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskLine line,
        BlueTuskLineSegment segment)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskLine left,
        BlueTuskLine right)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskCircle circle,
        BlueTuskPoint point)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskCircle circle,
        BlueTuskPolygon polygon)
        => ThrowTranslationOnly<double>();

    public static double GeometryDistance(
        this DbFunctions _,
        BlueTuskCircle left,
        BlueTuskCircle right)
        => ThrowTranslationOnly<double>();

    public static BlueTuskPoint? GeometryIntersection(
        this DbFunctions _,
        BlueTuskLineSegment left,
        BlueTuskLineSegment right)
        => ThrowTranslationOnly<BlueTuskPoint?>();

    public static BlueTuskBox? GeometryIntersection(
        this DbFunctions _,
        BlueTuskBox left,
        BlueTuskBox right)
        => ThrowTranslationOnly<BlueTuskBox?>();

    public static BlueTuskPoint? GeometryIntersection(
        this DbFunctions _,
        BlueTuskLine left,
        BlueTuskLine right)
        => ThrowTranslationOnly<BlueTuskPoint?>();

    public static BlueTuskPoint GeometryClosestPoint(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskLineSegment segment)
        => ThrowTranslationOnly<BlueTuskPoint>();

    public static BlueTuskPoint GeometryClosestPoint(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskBox box)
        => ThrowTranslationOnly<BlueTuskPoint>();

    public static BlueTuskPoint GeometryClosestPoint(
        this DbFunctions _,
        BlueTuskPoint point,
        BlueTuskLine line)
        => ThrowTranslationOnly<BlueTuskPoint>();

    public static BlueTuskPoint GeometryClosestPoint(
        this DbFunctions _,
        BlueTuskLineSegment left,
        BlueTuskLineSegment right)
        => ThrowTranslationOnly<BlueTuskPoint>();

    public static BlueTuskPoint GeometryClosestPoint(
        this DbFunctions _,
        BlueTuskLineSegment segment,
        BlueTuskBox box)
        => ThrowTranslationOnly<BlueTuskPoint>();

    public static BlueTuskPoint GeometryClosestPoint(
        this DbFunctions _,
        BlueTuskLine line,
        BlueTuskLineSegment segment)
        => ThrowTranslationOnly<BlueTuskPoint>();

    public static BlueTuskPoint PointAdd(
        this DbFunctions _,
        BlueTuskPoint left,
        BlueTuskPoint right)
        => ThrowTranslationOnly<BlueTuskPoint>();

    public static BlueTuskPoint PointSubtract(
        this DbFunctions _,
        BlueTuskPoint left,
        BlueTuskPoint right)
        => ThrowTranslationOnly<BlueTuskPoint>();

    public static BlueTuskPoint PointMultiply(
        this DbFunctions _,
        BlueTuskPoint left,
        BlueTuskPoint right)
        => ThrowTranslationOnly<BlueTuskPoint>();

    public static BlueTuskPoint PointDivide(
        this DbFunctions _,
        BlueTuskPoint left,
        BlueTuskPoint right)
        => ThrowTranslationOnly<BlueTuskPoint>();

    public static BlueTuskPath PathTranslate(
        this DbFunctions _,
        BlueTuskPath path,
        BlueTuskPoint offset)
        => ThrowTranslationOnly<BlueTuskPath>();

    public static BlueTuskPath PathTranslateNegative(
        this DbFunctions _,
        BlueTuskPath path,
        BlueTuskPoint offset)
        => ThrowTranslationOnly<BlueTuskPath>();

    public static BlueTuskPath PathScale(
        this DbFunctions _,
        BlueTuskPath path,
        BlueTuskPoint factors)
        => ThrowTranslationOnly<BlueTuskPath>();

    public static BlueTuskPath PathScaleInverse(
        this DbFunctions _,
        BlueTuskPath path,
        BlueTuskPoint factors)
        => ThrowTranslationOnly<BlueTuskPath>();

    public static BlueTuskPath PathConcatenate(
        this DbFunctions _,
        BlueTuskPath left,
        BlueTuskPath right)
        => ThrowTranslationOnly<BlueTuskPath>();

    public static BlueTuskBox BoxTranslate(
        this DbFunctions _,
        BlueTuskBox box,
        BlueTuskPoint offset)
        => ThrowTranslationOnly<BlueTuskBox>();

    public static BlueTuskBox BoxTranslateNegative(
        this DbFunctions _,
        BlueTuskBox box,
        BlueTuskPoint offset)
        => ThrowTranslationOnly<BlueTuskBox>();

    public static BlueTuskBox BoxScale(
        this DbFunctions _,
        BlueTuskBox box,
        BlueTuskPoint factors)
        => ThrowTranslationOnly<BlueTuskBox>();

    public static BlueTuskBox BoxScaleInverse(
        this DbFunctions _,
        BlueTuskBox box,
        BlueTuskPoint factors)
        => ThrowTranslationOnly<BlueTuskBox>();

    public static BlueTuskCircle CircleTranslate(
        this DbFunctions _,
        BlueTuskCircle circle,
        BlueTuskPoint offset)
        => ThrowTranslationOnly<BlueTuskCircle>();

    public static BlueTuskCircle CircleTranslateNegative(
        this DbFunctions _,
        BlueTuskCircle circle,
        BlueTuskPoint offset)
        => ThrowTranslationOnly<BlueTuskCircle>();

    public static BlueTuskCircle CircleScale(
        this DbFunctions _,
        BlueTuskCircle circle,
        BlueTuskPoint factors)
        => ThrowTranslationOnly<BlueTuskCircle>();

    public static BlueTuskCircle CircleScaleInverse(
        this DbFunctions _,
        BlueTuskCircle circle,
        BlueTuskPoint factors)
        => ThrowTranslationOnly<BlueTuskCircle>();

    public static int? ArrayLength<T>(this DbFunctions _, T[] array, int dimension)
        => ThrowTranslationOnly<int?>();

    public static int? ArrayLowerBound<T>(this DbFunctions _, T[] array, int dimension)
        => ThrowTranslationOnly<int?>();

    public static int? ArrayUpperBound<T>(this DbFunctions _, T[] array, int dimension)
        => ThrowTranslationOnly<int?>();

    public static int? ArrayCardinality<T>(this DbFunctions _, T[] array)
        => ThrowTranslationOnly<int?>();

    public static string? ArrayDimensions<T>(this DbFunctions _, T[] array)
        => ThrowTranslationOnly<string?>();

    public static int? ArrayDimensionCount<T>(this DbFunctions _, T[] array)
        => ThrowTranslationOnly<int?>();

    public static int? ArrayPosition<T>(this DbFunctions _, T[] array, T value)
        => ThrowTranslationOnly<int?>();

    public static int? ArrayPosition<T>(this DbFunctions _, T[] array, T value, int start)
        => ThrowTranslationOnly<int?>();

    public static int[]? ArrayPositions<T>(this DbFunctions _, T[] array, T value)
        => ThrowTranslationOnly<int[]?>();

    public static T[] ArrayRemove<T>(this DbFunctions _, T[] array, T value)
        => ThrowTranslationOnly<T[]>();

    public static T[] ArrayReplace<T>(this DbFunctions _, T[] array, T oldValue, T newValue)
        => ThrowTranslationOnly<T[]>();

    public static T[] ArrayReverse<T>(this DbFunctions _, T[] array)
        => ThrowTranslationOnly<T[]>();

    public static T[] ArrayShuffle<T>(this DbFunctions _, T[] array)
        => ThrowTranslationOnly<T[]>();

    public static T[] ArraySample<T>(this DbFunctions _, T[] array, int count)
        => ThrowTranslationOnly<T[]>();

    public static T[] ArrayTrim<T>(this DbFunctions _, T[] array, int count)
        => ThrowTranslationOnly<T[]>();

    public static string? ArrayToString<T>(this DbFunctions _, T[] array, string delimiter)
        => ThrowTranslationOnly<string?>();

    public static string? ArrayToString<T>(
        this DbFunctions _,
        T[] array,
        string delimiter,
        string nullString)
        => ThrowTranslationOnly<string?>();

    public static string[]? StringToArray(this DbFunctions _, string value, string delimiter)
        => ThrowTranslationOnly<string[]?>();

    public static string[]? StringToArray(
        this DbFunctions _,
        string value,
        string delimiter,
        string nullString)
        => ThrowTranslationOnly<string[]?>();

    public static int? StringAscii(this DbFunctions _, string value)
        => ThrowTranslationOnly<int?>();

    public static string? StringCharacter(this DbFunctions _, int codePoint)
        => ThrowTranslationOnly<string?>();

    public static int? BitLength(this DbFunctions _, string value)
        => ThrowTranslationOnly<int?>();

    public static int? BitLength(this DbFunctions _, byte[] value)
        => ThrowTranslationOnly<int?>();

    public static int? BitLength(this DbFunctions _, BlueTuskBitString value)
        => ThrowTranslationOnly<int?>();

    public static int? ByteLength(this DbFunctions _, string value)
        => ThrowTranslationOnly<int?>();

    public static int? ByteLength(this DbFunctions _, byte[] value)
        => ThrowTranslationOnly<int?>();

    public static int? ByteLength(this DbFunctions _, BlueTuskBitString value)
        => ThrowTranslationOnly<int?>();

    public static string? StringInitialCapital(this DbFunctions _, string value)
        => ThrowTranslationOnly<string?>();

    public static string? StringLeft(this DbFunctions _, string value, int count)
        => ThrowTranslationOnly<string?>();

    public static string? StringRight(this DbFunctions _, string value, int count)
        => ThrowTranslationOnly<string?>();

    public static string? StringPadLeft(this DbFunctions _, string value, int length)
        => ThrowTranslationOnly<string?>();

    public static string? StringPadLeft(
        this DbFunctions _,
        string value,
        int length,
        string fill)
        => ThrowTranslationOnly<string?>();

    public static string? StringPadRight(this DbFunctions _, string value, int length)
        => ThrowTranslationOnly<string?>();

    public static string? StringPadRight(
        this DbFunctions _,
        string value,
        int length,
        string fill)
        => ThrowTranslationOnly<string?>();

    public static string? StringTrimLeft(this DbFunctions _, string value)
        => ThrowTranslationOnly<string?>();

    public static string? StringTrimLeft(this DbFunctions _, string value, string characters)
        => ThrowTranslationOnly<string?>();

    public static string? StringTrimRight(this DbFunctions _, string value)
        => ThrowTranslationOnly<string?>();

    public static string? StringTrimRight(this DbFunctions _, string value, string characters)
        => ThrowTranslationOnly<string?>();

    public static string? StringTrim(this DbFunctions _, string value)
        => ThrowTranslationOnly<string?>();

    public static string? StringTrim(this DbFunctions _, string value, string characters)
        => ThrowTranslationOnly<string?>();

    public static string? Md5(this DbFunctions _, string value)
        => ThrowTranslationOnly<string?>();

    public static string? Md5(this DbFunctions _, byte[] value)
        => ThrowTranslationOnly<string?>();

    public static string[]? ParseIdentifier(this DbFunctions _, string value)
        => ThrowTranslationOnly<string[]?>();

    public static string[]? ParseIdentifier(this DbFunctions _, string value, bool strict)
        => ThrowTranslationOnly<string[]?>();

    public static string? QuoteIdentifier(this DbFunctions _, string value)
        => ThrowTranslationOnly<string?>();

    public static string? QuoteLiteral<T>(this DbFunctions _, T value)
        => ThrowTranslationOnly<string?>();

    public static string? QuoteNullableLiteral<T>(this DbFunctions _, T? value)
        => ThrowTranslationOnly<string?>();

    public static string? StringRepeat(this DbFunctions _, string value, int count)
        => ThrowTranslationOnly<string?>();

    public static string? StringReverse(this DbFunctions _, string value)
        => ThrowTranslationOnly<string?>();

    public static string? StringSplitPart(
        this DbFunctions _,
        string value,
        string delimiter,
        int field)
        => ThrowTranslationOnly<string?>();

    public static bool StringStartsWith(this DbFunctions _, string value, string prefix)
        => ThrowTranslationOnly<bool>();

    public static string? StringTranslate(
        this DbFunctions _,
        string value,
        string fromCharacters,
        string toCharacters)
        => ThrowTranslationOnly<string?>();

    public static string? BinaryEncode(this DbFunctions _, byte[] value, string format)
        => ThrowTranslationOnly<string?>();

    public static byte[]? BinaryDecode(this DbFunctions _, string value, string format)
        => ThrowTranslationOnly<byte[]?>();

    public static int? BinaryGetByte(this DbFunctions _, byte[] value, int offset)
        => ThrowTranslationOnly<int?>();

    public static byte[]? BinarySetByte(
        this DbFunctions _,
        byte[] value,
        int offset,
        int newValue)
        => ThrowTranslationOnly<byte[]?>();

    public static int? BinaryGetBit(this DbFunctions _, byte[] value, long offset)
        => ThrowTranslationOnly<int?>();

    public static byte[]? BinarySetBit(
        this DbFunctions _,
        byte[] value,
        long offset,
        int newValue)
        => ThrowTranslationOnly<byte[]?>();

    public static byte[]? BinaryTrim(this DbFunctions _, byte[] value, byte[] bytes)
        => ThrowTranslationOnly<byte[]?>();

    public static byte[]? BinaryTrimLeft(this DbFunctions _, byte[] value, byte[] bytes)
        => ThrowTranslationOnly<byte[]?>();

    public static byte[]? BinaryTrimRight(this DbFunctions _, byte[] value, byte[] bytes)
        => ThrowTranslationOnly<byte[]?>();

    public static byte[]? BinaryReverse(this DbFunctions _, byte[] value)
        => ThrowTranslationOnly<byte[]?>();

    public static double CubeRoot(this DbFunctions _, double value)
        => ThrowTranslationOnly<double>();

    public static double Degrees(this DbFunctions _, double radians)
        => ThrowTranslationOnly<double>();

    public static double Radians(this DbFunctions _, double degrees)
        => ThrowTranslationOnly<double>();

    public static decimal NumericDivide(this DbFunctions _, decimal dividend, decimal divisor)
        => ThrowTranslationOnly<decimal>();

    public static decimal Factorial(this DbFunctions _, long value)
        => ThrowTranslationOnly<decimal>();

    public static int GreatestCommonDivisor(this DbFunctions _, int left, int right)
        => ThrowTranslationOnly<int>();

    public static long GreatestCommonDivisor(this DbFunctions _, long left, long right)
        => ThrowTranslationOnly<long>();

    public static decimal GreatestCommonDivisor(this DbFunctions _, decimal left, decimal right)
        => ThrowTranslationOnly<decimal>();

    public static int LeastCommonMultiple(this DbFunctions _, int left, int right)
        => ThrowTranslationOnly<int>();

    public static long LeastCommonMultiple(this DbFunctions _, long left, long right)
        => ThrowTranslationOnly<long>();

    public static decimal LeastCommonMultiple(this DbFunctions _, decimal left, decimal right)
        => ThrowTranslationOnly<decimal>();

    public static int NumericMinimumScale(this DbFunctions _, decimal value)
        => ThrowTranslationOnly<int>();

    public static int NumericScale(this DbFunctions _, decimal value)
        => ThrowTranslationOnly<int>();

    public static decimal NumericTrimScale(this DbFunctions _, decimal value)
        => ThrowTranslationOnly<decimal>();

    public static int WidthBucket(
        this DbFunctions _,
        double operand,
        double low,
        double high,
        int count)
        => ThrowTranslationOnly<int>();

    public static int WidthBucket(
        this DbFunctions _,
        decimal operand,
        decimal low,
        decimal high,
        int count)
        => ThrowTranslationOnly<int>();

    public static int WidthBucket<T>(this DbFunctions _, T operand, T[] thresholds)
        => ThrowTranslationOnly<int>();

    public static string? FormatValue(this DbFunctions _, int value, string format)
        => ThrowTranslationOnly<string?>();

    public static string? FormatValue(this DbFunctions _, long value, string format)
        => ThrowTranslationOnly<string?>();

    public static string? FormatValue(this DbFunctions _, double value, string format)
        => ThrowTranslationOnly<string?>();

    public static string? FormatValue(this DbFunctions _, decimal value, string format)
        => ThrowTranslationOnly<string?>();

    public static string? FormatValue(this DbFunctions _, DateTime value, string format)
        => ThrowTranslationOnly<string?>();

    public static string? FormatValue(this DbFunctions _, DateTimeOffset value, string format)
        => ThrowTranslationOnly<string?>();

    public static string? FormatValue(this DbFunctions _, BlueTuskInterval value, string format)
        => ThrowTranslationOnly<string?>();

    public static DateOnly ParseDate(this DbFunctions _, string value, string format)
        => ThrowTranslationOnly<DateOnly>();

    public static decimal ParseNumber(this DbFunctions _, string value, string format)
        => ThrowTranslationOnly<decimal>();

    public static DateTimeOffset ParseTimestamp(
        this DbFunctions _,
        string value,
        string format)
        => ThrowTranslationOnly<DateTimeOffset>();

    public static DateTimeOffset UnixTimestamp(this DbFunctions _, double seconds)
        => ThrowTranslationOnly<DateTimeOffset>();

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

    /// <summary>Zips two PostgreSQL arrays, padding shorter inputs with nulls.</summary>
    public static IQueryable<BlueTuskUnnestPair<TFirst, TSecond>> Unnest<TFirst, TSecond>(
        this DbFunctions _,
        TFirst[] first,
        TSecond[] second)
        => ThrowTranslationOnly<IQueryable<BlueTuskUnnestPair<TFirst, TSecond>>>();

    /// <summary>Zips three PostgreSQL arrays, padding shorter inputs with nulls.</summary>
    public static IQueryable<BlueTuskUnnestTriple<TFirst, TSecond, TThird>> Unnest<TFirst, TSecond, TThird>(
        this DbFunctions _,
        TFirst[] first,
        TSecond[] second,
        TThird[] third)
        => ThrowTranslationOnly<IQueryable<BlueTuskUnnestTriple<TFirst, TSecond, TThird>>>();

    /// <summary>Zips four PostgreSQL arrays, padding shorter inputs with nulls.</summary>
    public static IQueryable<BlueTuskUnnestQuadruple<TFirst, TSecond, TThird, TFourth>>
        Unnest<TFirst, TSecond, TThird, TFourth>(
            this DbFunctions _,
            TFirst[] first,
            TSecond[] second,
            TThird[] third,
            TFourth[] fourth)
        => ThrowTranslationOnly<
            IQueryable<BlueTuskUnnestQuadruple<TFirst, TSecond, TThird, TFourth>>>();

    public static IQueryable<T> JsonToRecordset<T>(this DbFunctions _, string jsonb)
        where T : class
        => ThrowTranslationOnly<IQueryable<T>>();

    public static IQueryable<string> JsonPathQuery(
        this DbFunctions _,
        string jsonb,
        BlueTuskJsonPath path)
        => ThrowTranslationOnly<IQueryable<string>>();

    public static IQueryable<string> JsonPathQuery(
        this DbFunctions _,
        string jsonb,
        BlueTuskJsonPath path,
        string variablesJsonb,
        bool silent)
        => ThrowTranslationOnly<IQueryable<string>>();

    public static IQueryable<int> GenerateSubscripts<T>(
        this DbFunctions _,
        T[] array,
        int dimension)
        => ThrowTranslationOnly<IQueryable<int>>();

    public static IQueryable<int> GenerateSubscripts<T>(
        this DbFunctions _,
        T[] array,
        int dimension,
        bool reverse)
        => ThrowTranslationOnly<IQueryable<int>>();

    public static IQueryable<string[]> RegexMatches(
        this DbFunctions _,
        string input,
        string pattern)
        => ThrowTranslationOnly<IQueryable<string[]>>();

    public static IQueryable<string[]> RegexMatches(
        this DbFunctions _,
        string input,
        string pattern,
        string flags)
        => ThrowTranslationOnly<IQueryable<string[]>>();

    public static IQueryable<string> RegexSplitToTable(
        this DbFunctions _,
        string input,
        string pattern)
        => ThrowTranslationOnly<IQueryable<string>>();

    public static IQueryable<string> RegexSplitToTable(
        this DbFunctions _,
        string input,
        string pattern,
        string flags)
        => ThrowTranslationOnly<IQueryable<string>>();

    public static IQueryable<string?> StringToTable(
        this DbFunctions _,
        string input,
        string delimiter)
        => ThrowTranslationOnly<IQueryable<string?>>();

    public static IQueryable<string?> StringToTable(
        this DbFunctions _,
        string input,
        string delimiter,
        string nullString)
        => ThrowTranslationOnly<IQueryable<string?>>();

    public static T WindowDescending<T>(this DbFunctions _, T value)
        => ThrowTranslationOnly<T>();

    public static long WindowRowNumber<TOrder>(this DbFunctions _, TOrder orderBy)
        => ThrowTranslationOnly<long>();

    public static long WindowRowNumber<TPartition, TOrder>(
        this DbFunctions _,
        TPartition partitionBy,
        TOrder orderBy)
        => ThrowTranslationOnly<long>();

    public static long WindowRank<TOrder>(this DbFunctions _, TOrder orderBy)
        => ThrowTranslationOnly<long>();

    public static long WindowRank<TPartition, TOrder>(
        this DbFunctions _,
        TPartition partitionBy,
        TOrder orderBy)
        => ThrowTranslationOnly<long>();

    public static long WindowDenseRank<TOrder>(this DbFunctions _, TOrder orderBy)
        => ThrowTranslationOnly<long>();

    public static long WindowDenseRank<TPartition, TOrder>(
        this DbFunctions _,
        TPartition partitionBy,
        TOrder orderBy)
        => ThrowTranslationOnly<long>();

    public static double WindowPercentRank<TOrder>(this DbFunctions _, TOrder orderBy)
        => ThrowTranslationOnly<double>();

    public static double WindowPercentRank<TPartition, TOrder>(
        this DbFunctions _,
        TPartition partitionBy,
        TOrder orderBy)
        => ThrowTranslationOnly<double>();

    public static double WindowCumulativeDistribution<TOrder>(
        this DbFunctions _,
        TOrder orderBy)
        => ThrowTranslationOnly<double>();

    public static double WindowCumulativeDistribution<TPartition, TOrder>(
        this DbFunctions _,
        TPartition partitionBy,
        TOrder orderBy)
        => ThrowTranslationOnly<double>();

    public static int WindowNtile<TOrder>(this DbFunctions _, int buckets, TOrder orderBy)
        => ThrowTranslationOnly<int>();

    public static int WindowNtile<TPartition, TOrder>(
        this DbFunctions _,
        int buckets,
        TPartition partitionBy,
        TOrder orderBy)
        => ThrowTranslationOnly<int>();

    public static TValue WindowLag<TValue, TOrder>(
        this DbFunctions _,
        TValue value,
        int offset,
        TValue defaultValue,
        TOrder orderBy)
        => ThrowTranslationOnly<TValue>();

    public static TValue WindowLag<TValue, TPartition, TOrder>(
        this DbFunctions _,
        TValue value,
        int offset,
        TValue defaultValue,
        TPartition partitionBy,
        TOrder orderBy)
        => ThrowTranslationOnly<TValue>();

    public static TValue WindowLead<TValue, TOrder>(
        this DbFunctions _,
        TValue value,
        int offset,
        TValue defaultValue,
        TOrder orderBy)
        => ThrowTranslationOnly<TValue>();

    public static TValue WindowLead<TValue, TPartition, TOrder>(
        this DbFunctions _,
        TValue value,
        int offset,
        TValue defaultValue,
        TPartition partitionBy,
        TOrder orderBy)
        => ThrowTranslationOnly<TValue>();

    public static TValue WindowFirstValue<TValue, TOrder>(
        this DbFunctions _,
        TValue value,
        TOrder orderBy)
        => ThrowTranslationOnly<TValue>();

    public static TValue WindowFirstValue<TValue, TPartition, TOrder>(
        this DbFunctions _,
        TValue value,
        TPartition partitionBy,
        TOrder orderBy)
        => ThrowTranslationOnly<TValue>();

    public static TValue WindowLastValue<TValue, TOrder>(
        this DbFunctions _,
        TValue value,
        TOrder orderBy)
        => ThrowTranslationOnly<TValue>();

    public static TValue WindowLastValue<TValue, TPartition, TOrder>(
        this DbFunctions _,
        TValue value,
        TPartition partitionBy,
        TOrder orderBy)
        => ThrowTranslationOnly<TValue>();

    public static TValue WindowNthValue<TValue, TOrder>(
        this DbFunctions _,
        TValue value,
        int position,
        TOrder orderBy)
        => ThrowTranslationOnly<TValue>();

    public static TValue WindowNthValue<TValue, TPartition, TOrder>(
        this DbFunctions _,
        TValue value,
        int position,
        TPartition partitionBy,
        TOrder orderBy)
        => ThrowTranslationOnly<TValue>();

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

    public static string? JsonPathQueryFirst(
        this DbFunctions _,
        string jsonb,
        BlueTuskJsonPath path,
        string variablesJsonb,
        bool silent)
        => ThrowTranslationOnly<string?>();

    public static string? JsonPathQueryArray(
        this DbFunctions _,
        string jsonb,
        BlueTuskJsonPath path,
        string variablesJsonb,
        bool silent)
        => ThrowTranslationOnly<string?>();

    public static bool JsonPathExistsFunction(
        this DbFunctions _,
        string jsonb,
        BlueTuskJsonPath path,
        string variablesJsonb,
        bool silent)
        => ThrowTranslationOnly<bool>();

    public static bool JsonPathMatchesFunction(
        this DbFunctions _,
        string jsonb,
        BlueTuskJsonPath path,
        string variablesJsonb,
        bool silent)
        => ThrowTranslationOnly<bool>();

    public static string? JsonPretty(this DbFunctions _, string jsonb)
        => ThrowTranslationOnly<string?>();

    public static string JsonStripNulls(this DbFunctions _, string jsonb)
        => ThrowTranslationOnly<string>();

    public static string JsonStripNulls(this DbFunctions _, string jsonb, bool stripInArrays)
        => ThrowTranslationOnly<string>();

    public static string JsonSet(
        this DbFunctions _,
        string jsonb,
        string[] path,
        string replacementJsonb,
        bool createIfMissing)
        => ThrowTranslationOnly<string>();

    public static string JsonSetLax(
        this DbFunctions _,
        string jsonb,
        string[] path,
        string? replacementJsonb,
        bool createIfMissing,
        string nullValueTreatment)
        => ThrowTranslationOnly<string>();

    public static string JsonInsert(
        this DbFunctions _,
        string jsonb,
        string[] path,
        string replacementJsonb,
        bool insertAfter)
        => ThrowTranslationOnly<string>();

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

    public static BlueTuskTextSearchVector ToTextSearchVector(
        this DbFunctions _,
        BlueTuskRegConfig configuration,
        string document)
        => ThrowTranslationOnly<BlueTuskTextSearchVector>();

    public static BlueTuskTextSearchVector JsonToTextSearchVector(
        this DbFunctions _,
        string jsonb,
        string filterJsonb)
        => ThrowTranslationOnly<BlueTuskTextSearchVector>();

    public static BlueTuskTextSearchVector JsonToTextSearchVector(
        this DbFunctions _,
        BlueTuskRegConfig configuration,
        string jsonb,
        string filterJsonb)
        => ThrowTranslationOnly<BlueTuskTextSearchVector>();

    public static BlueTuskTextSearchQuery ToTextSearchQuery(
        this DbFunctions _,
        string query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery ToTextSearchQuery(
        this DbFunctions _,
        BlueTuskRegConfig configuration,
        string query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery PlainToTextSearchQuery(
        this DbFunctions _,
        string query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery PlainToTextSearchQuery(
        this DbFunctions _,
        BlueTuskRegConfig configuration,
        string query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery PhraseToTextSearchQuery(
        this DbFunctions _,
        string query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery PhraseToTextSearchQuery(
        this DbFunctions _,
        BlueTuskRegConfig configuration,
        string query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery WebSearchToTextSearchQuery(
        this DbFunctions _,
        string query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchQuery WebSearchToTextSearchQuery(
        this DbFunctions _,
        BlueTuskRegConfig configuration,
        string query)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static BlueTuskTextSearchVector TextSearchSetWeight(
        this DbFunctions _,
        BlueTuskTextSearchVector vector,
        BlueTuskInternalChar weight)
        => ThrowTranslationOnly<BlueTuskTextSearchVector>();

    public static BlueTuskTextSearchVector TextSearchSetWeight(
        this DbFunctions _,
        BlueTuskTextSearchVector vector,
        BlueTuskInternalChar weight,
        string[] lexemes)
        => ThrowTranslationOnly<BlueTuskTextSearchVector>();

    public static BlueTuskTextSearchVector TextSearchStrip(
        this DbFunctions _,
        BlueTuskTextSearchVector vector)
        => ThrowTranslationOnly<BlueTuskTextSearchVector>();

    public static int? TextSearchVectorLength(
        this DbFunctions _,
        BlueTuskTextSearchVector vector)
        => ThrowTranslationOnly<int?>();

    public static int? TextSearchQueryNodeCount(
        this DbFunctions _,
        BlueTuskTextSearchQuery query)
        => ThrowTranslationOnly<int?>();

    public static string? TextSearchQueryTree(
        this DbFunctions _,
        BlueTuskTextSearchQuery query)
        => ThrowTranslationOnly<string?>();

    public static BlueTuskTextSearchQuery TextSearchRewrite(
        this DbFunctions _,
        BlueTuskTextSearchQuery query,
        BlueTuskTextSearchQuery target,
        BlueTuskTextSearchQuery substitute)
        => ThrowTranslationOnly<BlueTuskTextSearchQuery>();

    public static float? TextSearchRank(
        this DbFunctions _,
        BlueTuskTextSearchVector vector,
        BlueTuskTextSearchQuery query)
        => ThrowTranslationOnly<float?>();

    public static float? TextSearchRank(
        this DbFunctions _,
        BlueTuskTextSearchVector vector,
        BlueTuskTextSearchQuery query,
        int normalization)
        => ThrowTranslationOnly<float?>();

    public static float? TextSearchRank(
        this DbFunctions _,
        float[] weights,
        BlueTuskTextSearchVector vector,
        BlueTuskTextSearchQuery query,
        int normalization)
        => ThrowTranslationOnly<float?>();

    public static float? TextSearchCoverDensityRank(
        this DbFunctions _,
        BlueTuskTextSearchVector vector,
        BlueTuskTextSearchQuery query,
        int normalization)
        => ThrowTranslationOnly<float?>();

    public static float? TextSearchCoverDensityRank(
        this DbFunctions _,
        float[] weights,
        BlueTuskTextSearchVector vector,
        BlueTuskTextSearchQuery query,
        int normalization)
        => ThrowTranslationOnly<float?>();

    public static string? TextSearchHeadline(
        this DbFunctions _,
        string document,
        BlueTuskTextSearchQuery query,
        string options)
        => ThrowTranslationOnly<string?>();

    public static string? TextSearchHeadline(
        this DbFunctions _,
        BlueTuskRegConfig configuration,
        string document,
        BlueTuskTextSearchQuery query,
        string options)
        => ThrowTranslationOnly<string?>();

    public static string? JsonTextSearchHeadline(
        this DbFunctions _,
        string jsonb,
        BlueTuskTextSearchQuery query,
        string options)
        => ThrowTranslationOnly<string?>();

    public static string? JsonTextSearchHeadline(
        this DbFunctions _,
        BlueTuskRegConfig configuration,
        string jsonb,
        BlueTuskTextSearchQuery query,
        string options)
        => ThrowTranslationOnly<string?>();

    public static double DatePart(this DbFunctions _, string field, DateTime value)
        => ThrowTranslationOnly<double>();

    public static double DatePart(this DbFunctions _, string field, DateTimeOffset value)
        => ThrowTranslationOnly<double>();

    public static double DatePart(this DbFunctions _, string field, BlueTuskInterval value)
        => ThrowTranslationOnly<double>();

    public static DateTime DateTrunc(this DbFunctions _, string field, DateTime value)
        => ThrowTranslationOnly<DateTime>();

    public static DateTimeOffset DateTrunc(
        this DbFunctions _,
        string field,
        DateTimeOffset value)
        => ThrowTranslationOnly<DateTimeOffset>();

    public static DateTimeOffset DateTrunc(
        this DbFunctions _,
        string field,
        DateTimeOffset value,
        string timeZone)
        => ThrowTranslationOnly<DateTimeOffset>();

    public static BlueTuskInterval DateTrunc(
        this DbFunctions _,
        string field,
        BlueTuskInterval value)
        => ThrowTranslationOnly<BlueTuskInterval>();

    public static DateTime DateBin(
        this DbFunctions _,
        TimeSpan stride,
        DateTime value,
        DateTime origin)
        => ThrowTranslationOnly<DateTime>();

    public static DateTimeOffset DateBin(
        this DbFunctions _,
        TimeSpan stride,
        DateTimeOffset value,
        DateTimeOffset origin)
        => ThrowTranslationOnly<DateTimeOffset>();

    public static BlueTuskInterval DateAge(this DbFunctions _, DateTime value, DateTime earlier)
        => ThrowTranslationOnly<BlueTuskInterval>();

    public static BlueTuskInterval DateAge(
        this DbFunctions _,
        DateTimeOffset value,
        DateTimeOffset earlier)
        => ThrowTranslationOnly<BlueTuskInterval>();

    public static DateOnly MakeDate(this DbFunctions _, int year, int month, int day)
        => ThrowTranslationOnly<DateOnly>();

    public static TimeOnly MakeTime(
        this DbFunctions _,
        int hour,
        int minute,
        double second)
        => ThrowTranslationOnly<TimeOnly>();

    public static DateTime MakeTimestamp(
        this DbFunctions _,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        double second)
        => ThrowTranslationOnly<DateTime>();

    public static DateTimeOffset MakeTimestampWithTimeZone(
        this DbFunctions _,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        double second,
        string timeZone)
        => ThrowTranslationOnly<DateTimeOffset>();

    public static BlueTuskInterval MakeInterval(
        this DbFunctions _,
        int years,
        int months,
        int weeks,
        int days,
        int hours,
        int minutes,
        double seconds)
        => ThrowTranslationOnly<BlueTuskInterval>();

    public static BlueTuskInterval JustifyDays(this DbFunctions _, BlueTuskInterval value)
        => ThrowTranslationOnly<BlueTuskInterval>();

    public static BlueTuskInterval JustifyHours(this DbFunctions _, BlueTuskInterval value)
        => ThrowTranslationOnly<BlueTuskInterval>();

    public static BlueTuskInterval JustifyInterval(this DbFunctions _, BlueTuskInterval value)
        => ThrowTranslationOnly<BlueTuskInterval>();

    public static double GeometryArea(this DbFunctions _, BlueTuskBox value)
        => ThrowTranslationOnly<double>();

    public static double? GeometryArea(this DbFunctions _, BlueTuskPath value)
        => ThrowTranslationOnly<double?>();

    public static double GeometryArea(this DbFunctions _, BlueTuskCircle value)
        => ThrowTranslationOnly<double>();

    public static BlueTuskPoint GeometryCenter(this DbFunctions _, BlueTuskBox value)
        => ThrowTranslationOnly<BlueTuskPoint>();

    public static BlueTuskPoint GeometryCenter(this DbFunctions _, BlueTuskCircle value)
        => ThrowTranslationOnly<BlueTuskPoint>();

    public static BlueTuskLineSegment BoxDiagonal(this DbFunctions _, BlueTuskBox value)
        => ThrowTranslationOnly<BlueTuskLineSegment>();

    public static double CircleDiameter(this DbFunctions _, BlueTuskCircle value)
        => ThrowTranslationOnly<double>();

    public static double BoxHeight(this DbFunctions _, BlueTuskBox value)
        => ThrowTranslationOnly<double>();

    public static bool PathIsClosed(this DbFunctions _, BlueTuskPath value)
        => ThrowTranslationOnly<bool>();

    public static bool PathIsOpen(this DbFunctions _, BlueTuskPath value)
        => ThrowTranslationOnly<bool>();

    public static double GeometryLength(this DbFunctions _, BlueTuskLineSegment value)
        => ThrowTranslationOnly<double>();

    public static double GeometryLength(this DbFunctions _, BlueTuskPath value)
        => ThrowTranslationOnly<double>();

    public static int GeometryPointCount(this DbFunctions _, BlueTuskPath value)
        => ThrowTranslationOnly<int>();

    public static int GeometryPointCount(this DbFunctions _, BlueTuskPolygon value)
        => ThrowTranslationOnly<int>();

    public static BlueTuskPath PathClose(this DbFunctions _, BlueTuskPath value)
        => ThrowTranslationOnly<BlueTuskPath>();

    public static BlueTuskPath PathOpen(this DbFunctions _, BlueTuskPath value)
        => ThrowTranslationOnly<BlueTuskPath>();

    public static double CircleRadius(this DbFunctions _, BlueTuskCircle value)
        => ThrowTranslationOnly<double>();

    public static double PointSlope(
        this DbFunctions _,
        BlueTuskPoint first,
        BlueTuskPoint second)
        => ThrowTranslationOnly<double>();

    public static double BoxWidth(this DbFunctions _, BlueTuskBox value)
        => ThrowTranslationOnly<double>();

    public static T[]? ArrayAggregate<T>(this DbFunctions _, IEnumerable<T> values)
        => ThrowTranslationOnly<T[]?>();

    public static string? StringAggregate(
        this DbFunctions _,
        IEnumerable<string> values,
        string delimiter)
        => ThrowTranslationOnly<string?>();

    public static byte[]? StringAggregate(
        this DbFunctions _,
        IEnumerable<byte[]> values,
        byte[] delimiter)
        => ThrowTranslationOnly<byte[]?>();

    public static T? AnyValue<T>(this DbFunctions _, IEnumerable<T> values)
        => ThrowTranslationOnly<T?>();

    public static bool? BooleanAnd(this DbFunctions _, IEnumerable<bool> values)
        => ThrowTranslationOnly<bool?>();

    public static bool? BooleanOr(this DbFunctions _, IEnumerable<bool> values)
        => ThrowTranslationOnly<bool?>();

    public static BlueTuskMultirange<T>? RangeAggregate<T>(
        this DbFunctions _,
        IEnumerable<BlueTuskRange<T>> ranges)
        => ThrowTranslationOnly<BlueTuskMultirange<T>?>();

    public static BlueTuskMultirange<T>? RangeAggregate<T>(
        this DbFunctions _,
        IEnumerable<BlueTuskMultirange<T>> multiranges)
        => ThrowTranslationOnly<BlueTuskMultirange<T>?>();

    public static BlueTuskRange<T>? RangeIntersectAggregate<T>(
        this DbFunctions _,
        IEnumerable<BlueTuskRange<T>> ranges)
        => ThrowTranslationOnly<BlueTuskRange<T>?>();

    public static BlueTuskMultirange<T>? RangeIntersectAggregate<T>(
        this DbFunctions _,
        IEnumerable<BlueTuskMultirange<T>> multiranges)
        => ThrowTranslationOnly<BlueTuskMultirange<T>?>();

    public static string? JsonAggregate<T>(this DbFunctions _, IEnumerable<T> values)
        => ThrowTranslationOnly<string?>();

    public static string? JsonbAggregate<T>(this DbFunctions _, IEnumerable<T> values)
        => ThrowTranslationOnly<string?>();

    public static string? JsonAggregateStrict<T>(this DbFunctions _, IEnumerable<T> values)
        => ThrowTranslationOnly<string?>();

    public static string? JsonbAggregateStrict<T>(this DbFunctions _, IEnumerable<T> values)
        => ThrowTranslationOnly<string?>();

    public static string? JsonObjectAggregate<TValue>(
        this DbFunctions _,
        IEnumerable<(string Key, TValue Value)> values)
        => ThrowTranslationOnly<string?>();

    public static string? JsonbObjectAggregate<TValue>(
        this DbFunctions _,
        IEnumerable<(string Key, TValue Value)> values)
        => ThrowTranslationOnly<string?>();

    public static string? JsonObjectAggregateStrict<TValue>(
        this DbFunctions _,
        IEnumerable<(string Key, TValue Value)> values)
        => ThrowTranslationOnly<string?>();

    public static string? JsonObjectAggregateUnique<TValue>(
        this DbFunctions _,
        IEnumerable<(string Key, TValue Value)> values)
        => ThrowTranslationOnly<string?>();

    public static string? JsonObjectAggregateUniqueStrict<TValue>(
        this DbFunctions _,
        IEnumerable<(string Key, TValue Value)> values)
        => ThrowTranslationOnly<string?>();

    public static string? JsonbObjectAggregateStrict<TValue>(
        this DbFunctions _,
        IEnumerable<(string Key, TValue Value)> values)
        => ThrowTranslationOnly<string?>();

    public static string? JsonbObjectAggregateUnique<TValue>(
        this DbFunctions _,
        IEnumerable<(string Key, TValue Value)> values)
        => ThrowTranslationOnly<string?>();

    public static string? JsonbObjectAggregateUniqueStrict<TValue>(
        this DbFunctions _,
        IEnumerable<(string Key, TValue Value)> values)
        => ThrowTranslationOnly<string?>();

    public static string? XmlAggregate(this DbFunctions _, IEnumerable<string> values)
        => ThrowTranslationOnly<string?>();

    public static int? IntegerBitAnd(this DbFunctions _, IEnumerable<int> values)
        => ThrowTranslationOnly<int?>();

    public static short? SmallIntBitAnd(this DbFunctions _, IEnumerable<short> values)
        => ThrowTranslationOnly<short?>();

    public static BlueTuskBitString? BitStringAnd(
        this DbFunctions _,
        IEnumerable<BlueTuskBitString> values)
        => ThrowTranslationOnly<BlueTuskBitString?>();

    public static int? IntegerBitOr(this DbFunctions _, IEnumerable<int> values)
        => ThrowTranslationOnly<int?>();

    public static short? SmallIntBitOr(this DbFunctions _, IEnumerable<short> values)
        => ThrowTranslationOnly<short?>();

    public static BlueTuskBitString? BitStringOr(
        this DbFunctions _,
        IEnumerable<BlueTuskBitString> values)
        => ThrowTranslationOnly<BlueTuskBitString?>();

    public static int? IntegerBitXor(this DbFunctions _, IEnumerable<int> values)
        => ThrowTranslationOnly<int?>();

    public static short? SmallIntBitXor(this DbFunctions _, IEnumerable<short> values)
        => ThrowTranslationOnly<short?>();

    public static BlueTuskBitString? BitStringXor(
        this DbFunctions _,
        IEnumerable<BlueTuskBitString> values)
        => ThrowTranslationOnly<BlueTuskBitString?>();

    public static long? BigIntBitAnd(this DbFunctions _, IEnumerable<long> values)
        => ThrowTranslationOnly<long?>();

    public static long? BigIntBitOr(this DbFunctions _, IEnumerable<long> values)
        => ThrowTranslationOnly<long?>();

    public static long? BigIntBitXor(this DbFunctions _, IEnumerable<long> values)
        => ThrowTranslationOnly<long?>();

    public static double? StandardDeviationPopulation(
        this DbFunctions _,
        IEnumerable<double> values)
        => ThrowTranslationOnly<double?>();

    public static decimal? StandardDeviationPopulation(
        this DbFunctions _,
        IEnumerable<decimal> values)
        => ThrowTranslationOnly<decimal?>();

    public static double? StandardDeviationSample(
        this DbFunctions _,
        IEnumerable<double> values)
        => ThrowTranslationOnly<double?>();

    public static decimal? StandardDeviationSample(
        this DbFunctions _,
        IEnumerable<decimal> values)
        => ThrowTranslationOnly<decimal?>();

    public static double? VariancePopulation(this DbFunctions _, IEnumerable<double> values)
        => ThrowTranslationOnly<double?>();

    public static decimal? VariancePopulation(this DbFunctions _, IEnumerable<decimal> values)
        => ThrowTranslationOnly<decimal?>();

    public static double? VarianceSample(this DbFunctions _, IEnumerable<double> values)
        => ThrowTranslationOnly<double?>();

    public static decimal? VarianceSample(this DbFunctions _, IEnumerable<decimal> values)
        => ThrowTranslationOnly<decimal?>();

    public static double? Correlation(
        this DbFunctions _,
        IEnumerable<(double Y, double X)> values)
        => ThrowTranslationOnly<double?>();

    public static double? CovariancePopulation(
        this DbFunctions _,
        IEnumerable<(double Y, double X)> values)
        => ThrowTranslationOnly<double?>();

    public static double? CovarianceSample(
        this DbFunctions _,
        IEnumerable<(double Y, double X)> values)
        => ThrowTranslationOnly<double?>();

    public static double? RegressionAverageX(
        this DbFunctions _,
        IEnumerable<(double Y, double X)> values)
        => ThrowTranslationOnly<double?>();

    public static double? RegressionAverageY(
        this DbFunctions _,
        IEnumerable<(double Y, double X)> values)
        => ThrowTranslationOnly<double?>();

    public static long RegressionCount(
        this DbFunctions _,
        IEnumerable<(double Y, double X)> values)
        => ThrowTranslationOnly<long>();

    public static double? RegressionIntercept(
        this DbFunctions _,
        IEnumerable<(double Y, double X)> values)
        => ThrowTranslationOnly<double?>();

    public static double? RegressionR2(
        this DbFunctions _,
        IEnumerable<(double Y, double X)> values)
        => ThrowTranslationOnly<double?>();

    public static double? RegressionSlope(
        this DbFunctions _,
        IEnumerable<(double Y, double X)> values)
        => ThrowTranslationOnly<double?>();

    public static double? RegressionSumSquaresX(
        this DbFunctions _,
        IEnumerable<(double Y, double X)> values)
        => ThrowTranslationOnly<double?>();

    public static double? RegressionSumProducts(
        this DbFunctions _,
        IEnumerable<(double Y, double X)> values)
        => ThrowTranslationOnly<double?>();

    public static double? RegressionSumSquaresY(
        this DbFunctions _,
        IEnumerable<(double Y, double X)> values)
        => ThrowTranslationOnly<double?>();

    public static int? Mode(this DbFunctions _, IEnumerable<int> values)
        => ThrowTranslationOnly<int?>();

    public static long? Mode(this DbFunctions _, IEnumerable<long> values)
        => ThrowTranslationOnly<long?>();

    public static double? Mode(this DbFunctions _, IEnumerable<double> values)
        => ThrowTranslationOnly<double?>();

    public static decimal? Mode(this DbFunctions _, IEnumerable<decimal> values)
        => ThrowTranslationOnly<decimal?>();

    public static string? Mode(this DbFunctions _, IEnumerable<string> values)
        => ThrowTranslationOnly<string?>();

    public static T? Mode<T>(this DbFunctions _, IEnumerable<T> values)
        => ThrowTranslationOnly<T?>();

    public static double? PercentileContinuous(
        this DbFunctions _,
        IEnumerable<double> values,
        double fraction)
        => ThrowTranslationOnly<double?>();

    public static double[]? PercentileContinuous(
        this DbFunctions _,
        IEnumerable<double> values,
        double[] fractions)
        => ThrowTranslationOnly<double[]?>();

    public static BlueTuskInterval? PercentileContinuous(
        this DbFunctions _,
        IEnumerable<BlueTuskInterval> values,
        double fraction)
        => ThrowTranslationOnly<BlueTuskInterval?>();

    public static BlueTuskInterval[]? PercentileContinuous(
        this DbFunctions _,
        IEnumerable<BlueTuskInterval> values,
        double[] fractions)
        => ThrowTranslationOnly<BlueTuskInterval[]?>();

    public static int? PercentileDiscrete(
        this DbFunctions _,
        IEnumerable<int> values,
        double fraction)
        => ThrowTranslationOnly<int?>();

    public static int[]? PercentileDiscrete(
        this DbFunctions _,
        IEnumerable<int> values,
        double[] fractions)
        => ThrowTranslationOnly<int[]?>();

    public static long? PercentileDiscrete(
        this DbFunctions _,
        IEnumerable<long> values,
        double fraction)
        => ThrowTranslationOnly<long?>();

    public static long[]? PercentileDiscrete(
        this DbFunctions _,
        IEnumerable<long> values,
        double[] fractions)
        => ThrowTranslationOnly<long[]?>();

    public static double? PercentileDiscrete(
        this DbFunctions _,
        IEnumerable<double> values,
        double fraction)
        => ThrowTranslationOnly<double?>();

    public static double[]? PercentileDiscrete(
        this DbFunctions _,
        IEnumerable<double> values,
        double[] fractions)
        => ThrowTranslationOnly<double[]?>();

    public static decimal? PercentileDiscrete(
        this DbFunctions _,
        IEnumerable<decimal> values,
        double fraction)
        => ThrowTranslationOnly<decimal?>();

    public static T? PercentileDiscrete<T>(
        this DbFunctions _,
        IEnumerable<T> values,
        double fraction)
        => ThrowTranslationOnly<T?>();

    public static T[]? PercentileDiscrete<T>(
        this DbFunctions _,
        IEnumerable<T> values,
        double[] fractions)
        => ThrowTranslationOnly<T[]?>();

    public static long HypotheticalRank<T>(
        this DbFunctions _,
        IEnumerable<T> values,
        T hypotheticalValue)
        => ThrowTranslationOnly<long>();

    public static long HypotheticalDenseRank<T>(
        this DbFunctions _,
        IEnumerable<T> values,
        T hypotheticalValue)
        => ThrowTranslationOnly<long>();

    public static double HypotheticalPercentRank<T>(
        this DbFunctions _,
        IEnumerable<T> values,
        T hypotheticalValue)
        => ThrowTranslationOnly<double>();

    public static double HypotheticalCumulativeDistribution<T>(
        this DbFunctions _,
        IEnumerable<T> values,
        T hypotheticalValue)
        => ThrowTranslationOnly<double>();

    private static T ThrowTranslationOnly<T>()
        => throw new InvalidOperationException(
            "BlueTusk PostgreSQL database functions can only be used in translated EF Core queries.");
}
