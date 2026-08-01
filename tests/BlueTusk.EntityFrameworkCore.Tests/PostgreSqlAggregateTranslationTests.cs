using System.Text.Json;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class PostgreSqlAggregateTranslationTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    [Fact]
    public void PostgreSQL_aggregates_translate_ordering_distinct_and_filters()
    {
        using var context = CreateContext();
        var delimiter = "|";
        var minimum = 10;
        var fractions = new[] { 0.25, 0.5, 0.75 };
        var hypothetical = 15;
        var byteDelimiter = new byte[] { 0 };

        var sql = context.Values
            .GroupBy(value => value.GroupId)
            .Select(group => new
            {
                group.Key,
                OrderedValues = EF.Functions.ArrayAggregate(
                    group.OrderBy(value => value.SortOrder).Select(value => value.Number)),
                UniqueValues = EF.Functions.ArrayAggregate(
                    group.Select(value => value.Number).Distinct()),
                IncludedValues = EF.Functions.ArrayAggregate(
                    group
                        .Where(value => value.Include && value.Number >= minimum)
                        .Select(value => value.Number)),
                Text = EF.Functions.StringAggregate(
                    group.OrderBy(value => value.SortOrder).Select(value => value.Text),
                    delimiter),
                Bytes = EF.Functions.StringAggregate(
                    group.OrderBy(value => value.SortOrder).Select(value => value.Bytes),
                    byteDelimiter),
                AnyValue = EF.Functions.AnyValue(group.Select(value => value.Number)),
                All = EF.Functions.BooleanAnd(group.Select(value => value.Flag)),
                Any = EF.Functions.BooleanOr(group.Select(value => value.Flag)),
                Union = EF.Functions.RangeAggregate(group.Select(value => value.Range)),
                MultirangeUnion = EF.Functions.RangeAggregate(
                    group.Select(value => value.Multirange)),
                Intersection = EF.Functions.RangeIntersectAggregate(
                    group
                        .Where(value => value.Include && value.Number >= minimum)
                        .Select(value => value.Range)),
                MultirangeIntersection = EF.Functions.RangeIntersectAggregate(
                    group
                        .Where(value => value.Include && value.Number >= minimum)
                        .Select(value => value.Multirange)),
                Json = EF.Functions.JsonAggregate(
                    group.OrderBy(value => value.SortOrder).Select(value => value.Json)),
                Jsonb = EF.Functions.JsonbAggregate(
                    group.OrderBy(value => value.SortOrder).Select(value => value.Json)),
                JsonStrict = EF.Functions.JsonAggregateStrict(
                    group.OrderBy(value => value.SortOrder).Select(value => value.NullableJson)),
                JsonbStrict = EF.Functions.JsonbAggregateStrict(
                    group.OrderBy(value => value.SortOrder).Select(value => value.NullableJson)),
                Xml = EF.Functions.XmlAggregate(
                    group.OrderBy(value => value.SortOrder).Select(value => value.Xml)),
                IntegerAnd = EF.Functions.IntegerBitAnd(group.Select(value => value.Number)),
                SmallIntAnd = EF.Functions.SmallIntBitAnd(group.Select(value => value.SmallBits)),
                BitStringAnd = EF.Functions.BitStringAnd(group.Select(value => value.BitValue)),
                IntegerOr = EF.Functions.IntegerBitOr(group.Select(value => value.Number)),
                SmallIntOr = EF.Functions.SmallIntBitOr(group.Select(value => value.SmallBits)),
                BitStringOr = EF.Functions.BitStringOr(group.Select(value => value.BitValue)),
                IntegerXor = EF.Functions.IntegerBitXor(group.Select(value => value.Number)),
                SmallIntXor = EF.Functions.SmallIntBitXor(group.Select(value => value.SmallBits)),
                BitStringXor = EF.Functions.BitStringXor(group.Select(value => value.BitValue)),
                BigIntAnd = EF.Functions.BigIntBitAnd(group.Select(value => value.LongBits)),
                BigIntOr = EF.Functions.BigIntBitOr(group.Select(value => value.LongBits)),
                BigIntXor = EF.Functions.BigIntBitXor(group.Select(value => value.LongBits)),
                PopulationDeviation = EF.Functions.StandardDeviationPopulation(
                    group.Select(value => value.Measurement)),
                SampleDeviation = EF.Functions.StandardDeviationSample(
                    group.Select(value => value.Amount)),
                PopulationVariance = EF.Functions.VariancePopulation(
                    group.Select(value => value.Amount)),
                SampleVariance = EF.Functions.VarianceSample(
                    group.Select(value => value.Measurement)),
                JsonObject = EF.Functions.JsonObjectAggregate(
                    group.OrderBy(value => value.SortOrder)
                        .Select(value => ValueTuple.Create(value.Text, value.Number))),
                JsonbObject = EF.Functions.JsonbObjectAggregate(
                    group.Select(value => ValueTuple.Create(value.Text, value.Json))),
                JsonObjectStrict = EF.Functions.JsonObjectAggregateStrict(
                    group.Select(value => ValueTuple.Create(value.Text, value.NullableJson))),
                JsonObjectUnique = EF.Functions.JsonObjectAggregateUnique(
                    group.Select(value => ValueTuple.Create(value.Text, value.Number))),
                JsonObjectUniqueStrict = EF.Functions.JsonObjectAggregateUniqueStrict(
                    group.Select(value => ValueTuple.Create(value.Text, value.NullableJson))),
                JsonbObjectStrict = EF.Functions.JsonbObjectAggregateStrict(
                    group.Select(value => ValueTuple.Create(value.Text, value.NullableJson))),
                JsonbObjectUnique = EF.Functions.JsonbObjectAggregateUnique(
                    group.Select(value => ValueTuple.Create(value.Text, value.Number))),
                JsonbObjectUniqueStrict = EF.Functions.JsonbObjectAggregateUniqueStrict(
                    group.Select(value => ValueTuple.Create(value.Text, value.NullableJson))),
                Correlation = EF.Functions.Correlation(
                    group.Select(value => ValueTuple.Create(
                        value.Measurement,
                        (double)value.Number))),
                Covariance = EF.Functions.CovariancePopulation(
                    group.Select(value => ValueTuple.Create(
                        value.Measurement,
                        (double)value.Number))),
                Regression = EF.Functions.RegressionSlope(
                    group.Select(value => ValueTuple.Create(
                        value.Measurement,
                        (double)value.Number))),
                Mode = EF.Functions.Mode(group.Select(value => value.Number)),
                RangeMode = EF.Functions.Mode(group.Select(value => value.Range)),
                ContinuousMedian = EF.Functions.PercentileContinuous(
                    group.Select(value => value.Measurement),
                    0.5),
                DiscreteMedian = EF.Functions.PercentileDiscrete(
                    group.Select(value => value.Number),
                    0.5),
                ContinuousQuartiles = EF.Functions.PercentileContinuous(
                    group.Select(value => value.Measurement),
                    fractions),
                IntervalMedian = EF.Functions.PercentileContinuous(
                    group.Select(value => value.Duration),
                    0.5),
                IntervalQuartiles = EF.Functions.PercentileContinuous(
                    group.Select(value => value.Duration),
                    fractions),
                DiscreteQuartiles = EF.Functions.PercentileDiscrete(
                    group.Select(value => value.Number),
                    fractions),
                TextMedian = EF.Functions.PercentileDiscrete(
                    group.Select(value => value.Text),
                    0.5),
                TextQuartiles = EF.Functions.PercentileDiscrete(
                    group.Select(value => value.Text),
                    fractions),
                HypotheticalRank = EF.Functions.HypotheticalRank(
                    group.Select(value => value.Number),
                    hypothetical),
                HypotheticalDenseRank = EF.Functions.HypotheticalDenseRank(
                    group.Select(value => value.Number),
                    hypothetical),
                HypotheticalPercentRank = EF.Functions.HypotheticalPercentRank(
                    group.Select(value => value.Number),
                    hypothetical),
                HypotheticalDistribution = EF.Functions.HypotheticalCumulativeDistribution(
                    group.Select(value => value.Number),
                    hypothetical),
            })
            .ToQueryString();

        Assert.Contains("array_agg(", sql, StringComparison.Ordinal);
        Assert.Contains("array_agg(DISTINCT ", sql, StringComparison.Ordinal);
        Assert.Contains("string_agg(", sql, StringComparison.Ordinal);
        Assert.Contains("any_value(", sql, StringComparison.Ordinal);
        Assert.Contains("bool_and(", sql, StringComparison.Ordinal);
        Assert.Contains("bool_or(", sql, StringComparison.Ordinal);
        Assert.Contains("range_agg(", sql, StringComparison.Ordinal);
        Assert.Contains("range_intersect_agg(", sql, StringComparison.Ordinal);
        Assert.Contains("json_agg(", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_agg(", sql, StringComparison.Ordinal);
        Assert.Contains("json_agg_strict(", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_agg_strict(", sql, StringComparison.Ordinal);
        Assert.Contains("xmlagg(", sql, StringComparison.Ordinal);
        Assert.Contains("bit_and(", sql, StringComparison.Ordinal);
        Assert.Contains("bit_or(", sql, StringComparison.Ordinal);
        Assert.Contains("bit_xor(", sql, StringComparison.Ordinal);
        Assert.Contains("stddev_pop(", sql, StringComparison.Ordinal);
        Assert.Contains("stddev_samp(", sql, StringComparison.Ordinal);
        Assert.Contains("var_pop(", sql, StringComparison.Ordinal);
        Assert.Contains("var_samp(", sql, StringComparison.Ordinal);
        Assert.Contains("json_object_agg(", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_object_agg(", sql, StringComparison.Ordinal);
        Assert.Contains("json_object_agg_strict(", sql, StringComparison.Ordinal);
        Assert.Contains("json_object_agg_unique(", sql, StringComparison.Ordinal);
        Assert.Contains("json_object_agg_unique_strict(", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_object_agg_strict(", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_object_agg_unique(", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb_object_agg_unique_strict(", sql, StringComparison.Ordinal);
        Assert.Contains("corr(", sql, StringComparison.Ordinal);
        Assert.Contains("covar_pop(", sql, StringComparison.Ordinal);
        Assert.Contains("regr_slope(", sql, StringComparison.Ordinal);
        Assert.Contains("mode() WITHIN GROUP (ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains("percentile_cont(", sql, StringComparison.Ordinal);
        Assert.Contains("percentile_disc(", sql, StringComparison.Ordinal);
        Assert.Contains("rank(", sql, StringComparison.Ordinal);
        Assert.Contains("dense_rank(", sql, StringComparison.Ordinal);
        Assert.Contains("percent_rank(", sql, StringComparison.Ordinal);
        Assert.Contains("cume_dist(", sql, StringComparison.Ordinal);
        Assert.Contains("WITHIN GROUP (ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains(" ORDER BY ", sql, StringComparison.Ordinal);
        Assert.Contains(" FILTER (WHERE ", sql, StringComparison.Ordinal);
        Assert.Contains("@delimiter", sql, StringComparison.Ordinal);
        Assert.Contains("@minimum", sql, StringComparison.Ordinal);
        Assert.Contains("@fractions", sql, StringComparison.Ordinal);
        Assert.Contains("@hypothetical", sql, StringComparison.Ordinal);

        var orderedSetDistinct = Assert.Throws<InvalidOperationException>(() => context.Values
            .GroupBy(value => value.GroupId)
            .Select(group => EF.Functions.Mode(group.Select(value => value.Number).Distinct()))
            .ToQueryString());
        Assert.Contains("do not accept DISTINCT", orderedSetDistinct.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostgreSQL_aggregates_execute_with_typed_results()
    {
        var connectionString = GetConnectionString();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
        await ExecuteNonQueryAsync(
            dataSource,
            """
            DROP TABLE IF EXISTS "ef_aggregate_values";
            CREATE TABLE "ef_aggregate_values" (
                "Id" integer PRIMARY KEY,
                "GroupId" integer NOT NULL,
                "SortOrder" integer NOT NULL,
                "Number" integer NOT NULL,
                "Text" text NOT NULL,
                "Flag" boolean NOT NULL,
                "Include" boolean NOT NULL,
                "Range" int4range NOT NULL,
                "Multirange" int4multirange NOT NULL,
                "LongBits" bigint NOT NULL,
                "SmallBits" smallint NOT NULL,
                "BitValue" bit varying NOT NULL,
                "Bytes" bytea NOT NULL,
                "Measurement" double precision NOT NULL,
                "Amount" numeric NOT NULL,
                "Duration" interval NOT NULL,
                "Json" jsonb NOT NULL,
                "NullableJson" jsonb NULL,
                "Xml" xml NOT NULL);
            INSERT INTO "ef_aggregate_values"
                (
                    "Id",
                    "GroupId",
                    "SortOrder",
                    "Number",
                    "Text",
                    "Flag",
                    "Include",
                    "Range",
                    "Multirange",
                    "LongBits",
                    "SmallBits",
                    "BitValue",
                    "Bytes",
                    "Measurement",
                    "Amount",
                    "Duration",
                    "Json",
                    "NullableJson",
                    "Xml")
            VALUES
                (
                    1, 7, 2, 20, 'beta', true, true, '[1,5)'::int4range,
                    '{[1,5)}'::int4multirange,
                    12, 12, B'1100', decode('02', 'hex'), 2, 2, interval '2 hours',
                    '{"id":2}'::jsonb, '{"id":2}'::jsonb, '<item>beta</item>'::xml),
                (
                    2, 7, 1, 10, 'alpha', true, true, '[4,8)'::int4range,
                    '{[4,8)}'::int4multirange,
                    10, 10, B'1010', decode('01', 'hex'), 1, 1, interval '1 hour',
                    '{"id":1}'::jsonb, '{"id":1}'::jsonb, '<item>alpha</item>'::xml),
                (
                    3, 7, 3, 20, 'gamma', false, false, '[10,12)'::int4range,
                    '{[10,12)}'::int4multirange,
                    5, 5, B'0101', decode('03', 'hex'), 3, 3, interval '3 hours',
                    '{"id":3}'::jsonb, NULL, '<item>gamma</item>'::xml)
            """);

        try
        {
            await using var context = CreateContext(dataSource);
            var delimiter = "|";
            var minimum = 10;
            var fractions = new[] { 0.25, 0.5, 0.75 };
            var hypothetical = 15;
            var byteDelimiter = new byte[] { 0 };
            var aggregate = await context.Values
                .GroupBy(value => value.GroupId)
                .Select(group => new
                {
                    group.Key,
                    OrderedValues = EF.Functions.ArrayAggregate(
                        group.OrderBy(value => value.SortOrder).Select(value => value.Number)),
                    UniqueValues = EF.Functions.ArrayAggregate(
                        group.Select(value => value.Number).Distinct()),
                    IncludedValues = EF.Functions.ArrayAggregate(
                        group
                            .Where(value => value.Include && value.Number >= minimum)
                            .Select(value => value.Number)),
                    Text = EF.Functions.StringAggregate(
                        group.OrderBy(value => value.SortOrder).Select(value => value.Text),
                        delimiter),
                    Bytes = EF.Functions.StringAggregate(
                        group.OrderBy(value => value.SortOrder).Select(value => value.Bytes),
                        byteDelimiter),
                    All = EF.Functions.BooleanAnd(group.Select(value => value.Flag)),
                    Any = EF.Functions.BooleanOr(group.Select(value => value.Flag)),
                    Union = EF.Functions.RangeAggregate(group.Select(value => value.Range)),
                    MultirangeUnion = EF.Functions.RangeAggregate(
                        group.Select(value => value.Multirange)),
                    Intersection = EF.Functions.RangeIntersectAggregate(
                        group
                            .Where(value => value.Include && value.Number >= minimum)
                            .Select(value => value.Range)),
                    MultirangeIntersection = EF.Functions.RangeIntersectAggregate(
                        group
                            .Where(value => value.Include && value.Number >= minimum)
                            .Select(value => value.Multirange)),
                    Json = EF.Functions.JsonAggregate(
                        group.OrderBy(value => value.SortOrder).Select(value => value.Json)),
                    Jsonb = EF.Functions.JsonbAggregate(
                        group.OrderBy(value => value.SortOrder).Select(value => value.Json)),
                    Xml = EF.Functions.XmlAggregate(
                        group.OrderBy(value => value.SortOrder).Select(value => value.Xml)),
                    IntegerAnd = EF.Functions.IntegerBitAnd(group.Select(value => value.Number)),
                    SmallIntAnd = EF.Functions.SmallIntBitAnd(group.Select(value => value.SmallBits)),
                    BitStringAnd = EF.Functions.BitStringAnd(group.Select(value => value.BitValue)),
                    IntegerOr = EF.Functions.IntegerBitOr(group.Select(value => value.Number)),
                    SmallIntOr = EF.Functions.SmallIntBitOr(group.Select(value => value.SmallBits)),
                    BitStringOr = EF.Functions.BitStringOr(group.Select(value => value.BitValue)),
                    IntegerXor = EF.Functions.IntegerBitXor(group.Select(value => value.Number)),
                    SmallIntXor = EF.Functions.SmallIntBitXor(group.Select(value => value.SmallBits)),
                    BitStringXor = EF.Functions.BitStringXor(group.Select(value => value.BitValue)),
                    BigIntAnd = EF.Functions.BigIntBitAnd(group.Select(value => value.LongBits)),
                    BigIntOr = EF.Functions.BigIntBitOr(group.Select(value => value.LongBits)),
                    BigIntXor = EF.Functions.BigIntBitXor(group.Select(value => value.LongBits)),
                    DoublePopulationDeviation = EF.Functions.StandardDeviationPopulation(
                        group.Select(value => value.Measurement)),
                    DecimalPopulationDeviation = EF.Functions.StandardDeviationPopulation(
                        group.Select(value => value.Amount)),
                    DoubleSampleDeviation = EF.Functions.StandardDeviationSample(
                        group.Select(value => value.Measurement)),
                    DecimalSampleDeviation = EF.Functions.StandardDeviationSample(
                        group.Select(value => value.Amount)),
                    DoublePopulationVariance = EF.Functions.VariancePopulation(
                        group.Select(value => value.Measurement)),
                    DecimalPopulationVariance = EF.Functions.VariancePopulation(
                        group.Select(value => value.Amount)),
                    DoubleSampleVariance = EF.Functions.VarianceSample(
                        group.Select(value => value.Measurement)),
                    DecimalSampleVariance = EF.Functions.VarianceSample(
                        group.Select(value => value.Amount)),
                    JsonObject = EF.Functions.JsonObjectAggregate(
                        group.OrderBy(value => value.SortOrder)
                            .Select(value => ValueTuple.Create(value.Text, value.Number))),
                    JsonbObject = EF.Functions.JsonbObjectAggregate(
                        group.Select(value => ValueTuple.Create(value.Text, value.Json))),
                    Correlation = EF.Functions.Correlation(
                        group.Select(value => ValueTuple.Create(
                            value.Measurement,
                            (double)value.Number))),
                    PopulationCovariance = EF.Functions.CovariancePopulation(
                        group.Select(value => ValueTuple.Create(
                            value.Measurement,
                            (double)value.Number))),
                    SampleCovariance = EF.Functions.CovarianceSample(
                        group.Select(value => ValueTuple.Create(
                            value.Measurement,
                            (double)value.Number))),
                    RegressionAverageX = EF.Functions.RegressionAverageX(
                        group.Select(value => ValueTuple.Create(
                            value.Measurement,
                            (double)value.Number))),
                    RegressionAverageY = EF.Functions.RegressionAverageY(
                        group.Select(value => ValueTuple.Create(
                            value.Measurement,
                            (double)value.Number))),
                    RegressionCount = EF.Functions.RegressionCount(
                        group.Select(value => ValueTuple.Create(
                            value.Measurement,
                            (double)value.Number))),
                    RegressionIntercept = EF.Functions.RegressionIntercept(
                        group.Select(value => ValueTuple.Create(
                            value.Measurement,
                            (double)value.Number))),
                    RegressionR2 = EF.Functions.RegressionR2(
                        group.Select(value => ValueTuple.Create(
                            value.Measurement,
                            (double)value.Number))),
                    RegressionSlope = EF.Functions.RegressionSlope(
                        group.Select(value => ValueTuple.Create(
                            value.Measurement,
                            (double)value.Number))),
                    RegressionSumSquaresX = EF.Functions.RegressionSumSquaresX(
                        group.Select(value => ValueTuple.Create(
                            value.Measurement,
                            (double)value.Number))),
                    RegressionSumProducts = EF.Functions.RegressionSumProducts(
                        group.Select(value => ValueTuple.Create(
                            value.Measurement,
                            (double)value.Number))),
                    RegressionSumSquaresY = EF.Functions.RegressionSumSquaresY(
                        group.Select(value => ValueTuple.Create(
                            value.Measurement,
                            (double)value.Number))),
                    Mode = EF.Functions.Mode(group.Select(value => value.Number)),
                    RangeMode = EF.Functions.Mode(group.Select(value => value.Range)),
                    ContinuousMedian = EF.Functions.PercentileContinuous(
                        group.Select(value => value.Measurement),
                        0.5),
                    DiscreteMedian = EF.Functions.PercentileDiscrete(
                        group.Select(value => value.Number),
                        0.5),
                    ContinuousQuartiles = EF.Functions.PercentileContinuous(
                        group.Select(value => value.Measurement),
                        fractions),
                    IntervalMedian = EF.Functions.PercentileContinuous(
                        group.Select(value => value.Duration),
                        0.5),
                    IntervalQuartiles = EF.Functions.PercentileContinuous(
                        group.Select(value => value.Duration),
                        fractions),
                    DiscreteQuartiles = EF.Functions.PercentileDiscrete(
                        group.Select(value => value.Number),
                        fractions),
                    TextMedian = EF.Functions.PercentileDiscrete(
                        group.Select(value => value.Text),
                        0.5),
                    TextQuartiles = EF.Functions.PercentileDiscrete(
                        group.Select(value => value.Text),
                        fractions),
                    HypotheticalRank = EF.Functions.HypotheticalRank(
                        group.Select(value => value.Number),
                        hypothetical),
                    HypotheticalDenseRank = EF.Functions.HypotheticalDenseRank(
                        group.Select(value => value.Number),
                        hypothetical),
                    HypotheticalPercentRank = EF.Functions.HypotheticalPercentRank(
                        group.Select(value => value.Number),
                        hypothetical),
                    HypotheticalDistribution = EF.Functions.HypotheticalCumulativeDistribution(
                        group.Select(value => value.Number),
                        hypothetical),
                })
                .SingleAsync();

            Assert.Equal(7, aggregate.Key);
            Assert.Equal([10, 20, 20], aggregate.OrderedValues!);
            Assert.Equal([10, 20], aggregate.UniqueValues!.Order());
            Assert.Equal([10, 20], aggregate.IncludedValues!.Order());
            Assert.Equal("alpha|beta|gamma", aggregate.Text);
            Assert.Equal([1, 0, 2, 0, 3], aggregate.Bytes);
            Assert.False(aggregate.All);
            Assert.True(aggregate.Any);
            Assert.Equal(
                new BlueTuskMultirange<int>(
                [
                    new BlueTuskRange<int>(1, 8),
                    new BlueTuskRange<int>(10, 12),
                ]),
                aggregate.Union);
            Assert.Equal(aggregate.Union, aggregate.MultirangeUnion);
            Assert.Equal(new BlueTuskRange<int>(4, 5), aggregate.Intersection);
            Assert.Equal(
                new BlueTuskMultirange<int>([new BlueTuskRange<int>(4, 5)]),
                aggregate.MultirangeIntersection);
            Assert.Contains("{\"id\": 1}", aggregate.Json, StringComparison.Ordinal);
            Assert.Contains("{\"id\": 2}", aggregate.Json, StringComparison.Ordinal);
            Assert.Contains("{\"id\": 3}", aggregate.Json, StringComparison.Ordinal);
            Assert.Contains("{\"id\": 1}", aggregate.Jsonb, StringComparison.Ordinal);
            Assert.Contains("{\"id\": 2}", aggregate.Jsonb, StringComparison.Ordinal);
            Assert.Contains("{\"id\": 3}", aggregate.Jsonb, StringComparison.Ordinal);
            Assert.Equal(
                "<item>alpha</item><item>beta</item><item>gamma</item>",
                aggregate.Xml);
            Assert.Equal(0, aggregate.IntegerAnd);
            Assert.Equal(30, aggregate.IntegerOr);
            Assert.Equal(10, aggregate.IntegerXor);
            Assert.Equal((short)0, aggregate.SmallIntAnd!.Value);
            Assert.Equal((short)15, aggregate.SmallIntOr!.Value);
            Assert.Equal((short)3, aggregate.SmallIntXor!.Value);
            Assert.Equal("0000", aggregate.BitStringAnd!.Value.ToString());
            Assert.Equal("1111", aggregate.BitStringOr!.Value.ToString());
            Assert.Equal("0011", aggregate.BitStringXor!.Value.ToString());
            Assert.Equal(0, aggregate.BigIntAnd);
            Assert.Equal(15, aggregate.BigIntOr);
            Assert.Equal(3, aggregate.BigIntXor);
            Assert.Equal(Math.Sqrt(2d / 3d), aggregate.DoublePopulationDeviation!.Value, 12);
            Assert.Equal(
                (decimal)Math.Sqrt(2d / 3d),
                aggregate.DecimalPopulationDeviation!.Value,
                precision: 12);
            Assert.Equal(1, aggregate.DoubleSampleDeviation);
            Assert.Equal(1, aggregate.DecimalSampleDeviation);
            Assert.Equal(2d / 3d, aggregate.DoublePopulationVariance!.Value, 12);
            Assert.Equal(2m / 3m, aggregate.DecimalPopulationVariance!.Value, 12);
            Assert.Equal(1, aggregate.DoubleSampleVariance);
            Assert.Equal(1, aggregate.DecimalSampleVariance);
            using var jsonObject = JsonDocument.Parse(aggregate.JsonObject!);
            Assert.Equal(10, jsonObject.RootElement.GetProperty("alpha").GetInt32());
            Assert.Equal(20, jsonObject.RootElement.GetProperty("beta").GetInt32());
            Assert.Equal(20, jsonObject.RootElement.GetProperty("gamma").GetInt32());
            using var jsonbObject = JsonDocument.Parse(aggregate.JsonbObject!);
            Assert.Equal(1, jsonbObject.RootElement.GetProperty("alpha").GetProperty("id").GetInt32());
            Assert.Equal(2, jsonbObject.RootElement.GetProperty("beta").GetProperty("id").GetInt32());
            Assert.Equal(3, jsonbObject.RootElement.GetProperty("gamma").GetProperty("id").GetInt32());
            Assert.Equal(Math.Sqrt(3d) / 2d, aggregate.Correlation!.Value, 12);
            Assert.Equal(10d / 3d, aggregate.PopulationCovariance!.Value, 12);
            Assert.Equal(5d, aggregate.SampleCovariance!.Value, 12);
            Assert.Equal(50d / 3d, aggregate.RegressionAverageX!.Value, 12);
            Assert.Equal(2d, aggregate.RegressionAverageY!.Value, 12);
            Assert.Equal(3, aggregate.RegressionCount);
            Assert.Equal(-0.5d, aggregate.RegressionIntercept!.Value, 12);
            Assert.Equal(0.75d, aggregate.RegressionR2!.Value, 12);
            Assert.Equal(0.15d, aggregate.RegressionSlope!.Value, 12);
            Assert.Equal(200d / 3d, aggregate.RegressionSumSquaresX!.Value, 12);
            Assert.Equal(10d, aggregate.RegressionSumProducts!.Value, 12);
            Assert.Equal(2d, aggregate.RegressionSumSquaresY!.Value, 12);
            Assert.Equal(20, aggregate.Mode);
            Assert.Equal(new BlueTuskRange<int>(1, 5), aggregate.RangeMode);
            Assert.Equal(2d, aggregate.ContinuousMedian);
            Assert.Equal(20, aggregate.DiscreteMedian);
            Assert.Equal([1.5, 2, 2.5], aggregate.ContinuousQuartiles!);
            Assert.Equal(BlueTuskInterval.Parse("2 hours"), aggregate.IntervalMedian);
            Assert.Equal(
                [
                    BlueTuskInterval.Parse("1 hour 30 minutes"),
                    BlueTuskInterval.Parse("2 hours"),
                    BlueTuskInterval.Parse("2 hours 30 minutes"),
                ],
                aggregate.IntervalQuartiles!);
            Assert.Equal([10, 20, 20], aggregate.DiscreteQuartiles!);
            Assert.Equal("beta", aggregate.TextMedian);
            Assert.Equal(["alpha", "beta", "gamma"], aggregate.TextQuartiles!);
            Assert.Equal(2, aggregate.HypotheticalRank);
            Assert.Equal(2, aggregate.HypotheticalDenseRank);
            Assert.Equal(1d / 3d, aggregate.HypotheticalPercentRank, 12);
            Assert.Equal(0.5d, aggregate.HypotheticalDistribution, 12);
        }
        finally
        {
            await ExecuteNonQueryAsync(dataSource, "DROP TABLE IF EXISTS \"ef_aggregate_values\"");
        }
    }

    [Fact]
    public async Task PostgreSQL_16_strict_unique_and_any_value_aggregates_execute()
    {
        var connectionString = GetConnectionString();
        await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
        var version = Convert.ToInt32(
            await ExecuteScalarAsync(dataSource, "SHOW server_version_num"),
            System.Globalization.CultureInfo.InvariantCulture);
        if (version < 160000)
        {
            throw SkipException.ForSkip("Strict/unique JSON aggregates and any_value require PostgreSQL 16.");
        }

        await ExecuteNonQueryAsync(
            dataSource,
            """
            DROP TABLE IF EXISTS "ef_aggregate_values";
            CREATE TABLE "ef_aggregate_values" (
                "Id" integer PRIMARY KEY,
                "GroupId" integer NOT NULL,
                "SortOrder" integer NOT NULL,
                "Number" integer NOT NULL,
                "Text" text NOT NULL,
                "NullableJson" jsonb NULL);
            INSERT INTO "ef_aggregate_values"
                ("Id", "GroupId", "SortOrder", "Number", "Text", "NullableJson")
            VALUES
                (1, 7, 2, 20, 'beta', '{"id":2}'::jsonb),
                (2, 7, 1, 10, 'alpha', '{"id":1}'::jsonb),
                (3, 7, 3, 20, 'gamma', NULL)
            """);

        try
        {
            await using var context = CreateContext(dataSource);
            var aggregate = await context.Values
                .GroupBy(value => value.GroupId)
                .Select(group => new
                {
                    AnyValue = EF.Functions.AnyValue(group.Select(value => value.Number)),
                    JsonStrict = EF.Functions.JsonAggregateStrict(
                        group.OrderBy(value => value.SortOrder).Select(value => value.NullableJson)),
                    JsonbStrict = EF.Functions.JsonbAggregateStrict(
                        group.OrderBy(value => value.SortOrder).Select(value => value.NullableJson)),
                    JsonObjectStrict = EF.Functions.JsonObjectAggregateStrict(
                        group.Select(value => ValueTuple.Create(value.Text, value.NullableJson))),
                    JsonObjectUnique = EF.Functions.JsonObjectAggregateUnique(
                        group.Select(value => ValueTuple.Create(value.Text, value.Number))),
                    JsonObjectUniqueStrict = EF.Functions.JsonObjectAggregateUniqueStrict(
                        group.Select(value => ValueTuple.Create(value.Text, value.NullableJson))),
                    JsonbObjectStrict = EF.Functions.JsonbObjectAggregateStrict(
                        group.Select(value => ValueTuple.Create(value.Text, value.NullableJson))),
                    JsonbObjectUnique = EF.Functions.JsonbObjectAggregateUnique(
                        group.Select(value => ValueTuple.Create(value.Text, value.Number))),
                    JsonbObjectUniqueStrict = EF.Functions.JsonbObjectAggregateUniqueStrict(
                        group.Select(value => ValueTuple.Create(value.Text, value.NullableJson))),
                })
                .SingleAsync();

            Assert.True(aggregate.AnyValue is 10 or 20);
            Assert.DoesNotContain("null", aggregate.JsonStrict!, StringComparison.Ordinal);
            Assert.DoesNotContain("null", aggregate.JsonbStrict!, StringComparison.Ordinal);
            foreach (var json in new[]
                     {
                         aggregate.JsonObjectStrict,
                         aggregate.JsonObjectUniqueStrict,
                         aggregate.JsonbObjectStrict,
                         aggregate.JsonbObjectUniqueStrict,
                     })
            {
                using var document = JsonDocument.Parse(json!);
                Assert.False(document.RootElement.TryGetProperty("gamma", out _));
                Assert.Equal(1, document.RootElement.GetProperty("alpha").GetProperty("id").GetInt32());
                Assert.Equal(2, document.RootElement.GetProperty("beta").GetProperty("id").GetInt32());
            }

            foreach (var json in new[] { aggregate.JsonObjectUnique, aggregate.JsonbObjectUnique })
            {
                using var document = JsonDocument.Parse(json!);
                Assert.Equal(10, document.RootElement.GetProperty("alpha").GetInt32());
                Assert.Equal(20, document.RootElement.GetProperty("beta").GetInt32());
                Assert.Equal(20, document.RootElement.GetProperty("gamma").GetInt32());
            }
        }
        finally
        {
            await ExecuteNonQueryAsync(dataSource, "DROP TABLE IF EXISTS \"ef_aggregate_values\"");
        }
    }

    private static AggregateContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AggregateContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new AggregateContext(options);
    }

    private static AggregateContext CreateContext(BlueTuskDataSource dataSource)
    {
        var options = new DbContextOptionsBuilder<AggregateContext>()
            .UseBlueTusk(dataSource)
            .Options;
        return new AggregateContext(options);
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

    private sealed class AggregateContext(DbContextOptions<AggregateContext> options) : DbContext(options)
    {
        public DbSet<AggregateValue> Values => Set<AggregateValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var value = modelBuilder.Entity<AggregateValue>();
            value.ToTable("ef_aggregate_values");
            value.Property(item => item.Json).HasColumnType("jsonb");
            value.Property(item => item.NullableJson).HasColumnType("jsonb");
            value.Property(item => item.Xml).HasColumnType("xml");
            value.Property(item => item.BitValue).HasColumnType("bit varying");
        }
    }

    private sealed class AggregateValue
    {
        public int Id { get; set; }

        public int GroupId { get; set; }

        public int SortOrder { get; set; }

        public int Number { get; set; }

        public string Text { get; set; } = string.Empty;

        public bool Flag { get; set; }

        public bool Include { get; set; }

        public BlueTuskRange<int> Range { get; set; }

        public BlueTuskMultirange<int> Multirange { get; set; } = new([]);

        public long LongBits { get; set; }

        public short SmallBits { get; set; }

        public BlueTuskBitString BitValue { get; set; }

        public byte[] Bytes { get; set; } = [];

        public double Measurement { get; set; }

        public decimal Amount { get; set; }

        public BlueTuskInterval Duration { get; set; }

        public string Json { get; set; } = "{}";

        public string? NullableJson { get; set; }

        public string Xml { get; set; } = "<item />";
    }
}
