using BlueTusk.Extensions.PgVector;
using BlueTusk.TypeSystem;

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

    /// <summary>Translates half vectors to pgvector's Euclidean-distance operator.</summary>
    public static double L2Distance(
        this DbFunctions _,
        BlueTuskHalfVector left,
        BlueTuskHalfVector right) =>
        throw TranslationOnly();

    /// <summary>Translates half vectors to pgvector's negative-inner-product operator.</summary>
    public static double MaxInnerProduct(
        this DbFunctions _,
        BlueTuskHalfVector left,
        BlueTuskHalfVector right) =>
        throw TranslationOnly();

    /// <summary>Translates half vectors to pgvector's cosine-distance operator.</summary>
    public static double CosineDistance(
        this DbFunctions _,
        BlueTuskHalfVector left,
        BlueTuskHalfVector right) =>
        throw TranslationOnly();

    /// <summary>Translates half vectors to pgvector's taxicab-distance operator.</summary>
    public static double L1Distance(
        this DbFunctions _,
        BlueTuskHalfVector left,
        BlueTuskHalfVector right) =>
        throw TranslationOnly();

    /// <summary>Translates sparse vectors to pgvector's Euclidean-distance operator.</summary>
    public static double L2Distance(
        this DbFunctions _,
        BlueTuskSparseVector left,
        BlueTuskSparseVector right) =>
        throw TranslationOnly();

    /// <summary>Translates sparse vectors to pgvector's negative-inner-product operator.</summary>
    public static double MaxInnerProduct(
        this DbFunctions _,
        BlueTuskSparseVector left,
        BlueTuskSparseVector right) =>
        throw TranslationOnly();

    /// <summary>Translates sparse vectors to pgvector's cosine-distance operator.</summary>
    public static double CosineDistance(
        this DbFunctions _,
        BlueTuskSparseVector left,
        BlueTuskSparseVector right) =>
        throw TranslationOnly();

    /// <summary>Translates sparse vectors to pgvector's taxicab-distance operator.</summary>
    public static double L1Distance(
        this DbFunctions _,
        BlueTuskSparseVector left,
        BlueTuskSparseVector right) =>
        throw TranslationOnly();

    /// <summary>Translates PostgreSQL bit strings to pgvector's Hamming-distance operator.</summary>
    public static double HammingDistance(
        this DbFunctions _,
        BlueTuskBitString left,
        BlueTuskBitString right) =>
        throw TranslationOnly();

    /// <summary>Translates PostgreSQL bit strings to pgvector's Jaccard-distance operator.</summary>
    public static double JaccardDistance(
        this DbFunctions _,
        BlueTuskBitString left,
        BlueTuskBitString right) =>
        throw TranslationOnly();

    private static InvalidOperationException TranslationOnly() =>
        new("pgvector functions can only be used in translated database queries.");
}
