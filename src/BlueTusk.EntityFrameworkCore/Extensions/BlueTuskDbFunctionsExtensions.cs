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

    private static T ThrowTranslationOnly<T>()
        => throw new InvalidOperationException(
            "BlueTusk PostgreSQL database functions can only be used in translated EF Core queries.");
}
