using BlueTusk.Extensions.PgVector;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Provides translation-only pgvector distance operations.</summary>
public static class BlueTuskPgVectorDbFunctionsExtensions
{
    /// <summary>Translates to pgvector's Euclidean-distance <c>&lt;-&gt;</c> operator.</summary>
    public static double L2Distance(
        this DbFunctions _,
        BlueTuskVector left,
        BlueTuskVector right) =>
        throw TranslationOnly();

    /// <summary>Translates to pgvector's negative-inner-product <c>&lt;#&gt;</c> operator for ascending searches.</summary>
    public static double MaxInnerProduct(
        this DbFunctions _,
        BlueTuskVector left,
        BlueTuskVector right) =>
        throw TranslationOnly();

    /// <summary>Translates to pgvector's cosine-distance <c>&lt;=&gt;</c> operator.</summary>
    public static double CosineDistance(
        this DbFunctions _,
        BlueTuskVector left,
        BlueTuskVector right) =>
        throw TranslationOnly();

    /// <summary>Translates to pgvector's taxicab-distance <c>&lt;+&gt;</c> operator.</summary>
    public static double L1Distance(
        this DbFunctions _,
        BlueTuskVector left,
        BlueTuskVector right) =>
        throw TranslationOnly();

    private static InvalidOperationException TranslationOnly() =>
        new("pgvector functions can only be used in translated database queries.");
}
