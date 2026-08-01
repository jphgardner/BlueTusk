using System.Text.Json;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class TypeMappingIntegrationTests
{
    [Fact]
    public void Composite_and_lossless_record_fields_translate_to_quoted_native_access()
    {
        var builder = new BlueTuskDataSourceBuilder(
            "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests");
        builder.MapEnum<EfOrderStatus>("public.ef_order_status");
        builder.MapComposite<EfAddress>("public.ef_address");
        using var dataSource = builder.Build();
        using var context = CreateUserTypeContext(
            dataSource,
            "ef_order_status",
            "ef_positive_integer",
            "ef_address",
            "ef_record_address");
        var street = "Baker Street";
        var status = EfOrderStatus.InProgress;
        var minimumDomainValue = 40;

        var sql = context.Values
            .Where(value => value.Status == status
                && value.DomainValue >= minimumDomainValue
                && value.Address.Street == street)
            .Select(value => new CompositeFieldProjection(
                value.Address.HouseNumber,
                value.Address.Street,
                value.Address.Note,
                EF.Functions.RecordField<int>(value.RecordAddress, "house_number"),
                EF.Functions.RecordField<string>(value.RecordAddress, "street"),
                EF.Functions.RecordField<string?>(value.RecordAddress, "note")))
            .ToQueryString();

        Assert.Contains("(\"e\".\"Address\").\"house_number\"", sql, StringComparison.Ordinal);
        Assert.Contains("(\"e\".\"Address\").\"street\"", sql, StringComparison.Ordinal);
        Assert.Contains("(\"e\".\"Address\").\"note\"", sql, StringComparison.Ordinal);
        Assert.Contains("(\"e\".\"RecordAddress\").\"house_number\"", sql, StringComparison.Ordinal);
        Assert.Contains("(\"e\".\"Address\").\"street\" = @street", sql, StringComparison.Ordinal);
        Assert.Contains("\"e\".\"Status\" = @status", sql, StringComparison.Ordinal);
        Assert.Contains("\"e\".\"DomainValue\" >= @minimumDomainValue", sql, StringComparison.Ordinal);

        var quotedNameSql = context.Values
            .Select(value => EF.Functions.RecordField<string>(value.RecordAddress, "street\"name"))
            .ToQueryString();
        Assert.Contains(
            "(\"e\".\"RecordAddress\").\"street\"\"name\"",
            quotedNameSql,
            StringComparison.Ordinal);

        var invalidName = Assert.Throws<ArgumentException>(() => context.Values
            .Select(value => EF.Functions.RecordField<string>(value.RecordAddress, ""))
            .ToQueryString());
        Assert.Equal("fieldName", invalidName.ParamName);
    }

    [Fact]
    public async Task Runtime_registered_enums_domains_composites_and_arrays_round_trip_through_EF_Core()
    {
        var connectionString = GetConnectionString();
        var suffix = Guid.NewGuid().ToString("N");
        var enumName = $"ef_order_status_{suffix}";
        var domainName = $"ef_positive_integer_{suffix}";
        var compositeName = $"ef_address_{suffix}";
        var recordName = $"ef_record_address_{suffix}";
        await ExecuteNonQueryAsync(
            connectionString,
            $"""
            DROP TABLE IF EXISTS "ef_user_type_values";
            CREATE TYPE public.{enumName} AS ENUM ('pending', 'in-progress', 'Complete');
            CREATE DOMAIN public.{domainName} AS int4 CHECK (VALUE > 0);
            CREATE TYPE public.{compositeName} AS
                (house_number int4, street text, note text);
            CREATE TYPE public.{recordName} AS
                (house_number int4, street text, note text);
            CREATE TABLE "ef_user_type_values" (
                "Id" integer PRIMARY KEY,
                "Status" public.{enumName} NOT NULL,
                "OptionalStatus" public.{enumName} NULL,
                "Statuses" public.{enumName}[] NOT NULL,
                "OptionalStatuses" public.{enumName}[] NULL,
                "DomainValue" public.{domainName} NOT NULL,
                "OptionalDomainValue" public.{domainName} NULL,
                "DomainValues" public.{domainName}[] NOT NULL,
                "Address" public.{compositeName} NOT NULL,
                "OptionalAddress" public.{compositeName} NULL,
                "Addresses" public.{compositeName}[] NOT NULL,
                "RecordAddress" public.{recordName} NOT NULL,
                "RecordAddresses" public.{recordName}[] NOT NULL)
            """);

        var builder = new BlueTuskDataSourceBuilder(connectionString);
        builder.MapEnum<EfOrderStatus>($"public.{enumName}");
        builder.MapComposite<EfAddress>($"public.{compositeName}");
        await using var dataSource = builder.Build();
        var expected = new UserTypeValue
        {
            Id = 1,
            Status = EfOrderStatus.InProgress,
            Statuses = [EfOrderStatus.Pending, EfOrderStatus.Complete],
            DomainValue = 42,
            DomainValues = [1, 2, 3],
            Address = new EfAddress(221, "Baker Street", null),
            Addresses =
            [
                new EfAddress(221, "Baker Street", null),
                new EfAddress(7, "Side Road", "rear entrance"),
            ],
            RecordAddress = CreateAddressRecord(19, "Record Road", null),
            RecordAddresses =
            [
                CreateAddressRecord(19, "Record Road", null),
                CreateAddressRecord(23, "Binary Boulevard", "north entrance"),
            ],
        };

        try
        {
            await using (var context = CreateUserTypeContext(
                dataSource,
                enumName,
                domainName,
                compositeName,
                recordName))
            {
                context.Values.Add(expected);
                Assert.Equal(1, await context.SaveChangesAsync());
            }

            await using (var context = CreateUserTypeContext(
                dataSource,
                enumName,
                domainName,
                compositeName,
                recordName))
            {
                var actual = await context.Values.AsNoTracking().SingleAsync();
                Assert.Equal(expected.Status, actual.Status);
                Assert.Null(actual.OptionalStatus);
                Assert.Equal(expected.Statuses, actual.Statuses);
                Assert.Null(actual.OptionalStatuses);
                Assert.Equal(expected.DomainValue, actual.DomainValue);
                Assert.Null(actual.OptionalDomainValue);
                Assert.Equal(expected.DomainValues, actual.DomainValues);
                Assert.Equal(expected.Address, actual.Address);
                Assert.Null(actual.OptionalAddress);
                Assert.Equal(expected.Addresses, actual.Addresses);
                AssertAddressRecord(expected.RecordAddress, actual.RecordAddress);
                Assert.Equal(expected.RecordAddresses.Length, actual.RecordAddresses.Length);
                for (var index = 0; index < expected.RecordAddresses.Length; index++)
                {
                    AssertAddressRecord(expected.RecordAddresses[index], actual.RecordAddresses[index]);
                }

                var street = "Baker Street";
                var status = EfOrderStatus.InProgress;
                var minimumDomainValue = 40;
                var fields = await context.Values
                    .AsNoTracking()
                    .Where(value => value.Status == status
                        && value.DomainValue >= minimumDomainValue
                        && value.Address.Street == street)
                    .Select(value => new CompositeFieldProjection(
                        value.Address.HouseNumber,
                        value.Address.Street,
                        value.Address.Note,
                        EF.Functions.RecordField<int>(value.RecordAddress, "house_number"),
                        EF.Functions.RecordField<string>(value.RecordAddress, "street"),
                        EF.Functions.RecordField<string?>(value.RecordAddress, "note")))
                    .SingleAsync();
                Assert.Equal(
                    new CompositeFieldProjection(221, "Baker Street", null, 19, "Record Road", null),
                    fields);

                var compiledFields = EF.CompileQuery(
                    (UserTypeValueContext database,
                        EfOrderStatus requestedStatus,
                        int requestedMinimum,
                        string requestedStreet) => database.Values
                        .AsNoTracking()
                        .Where(value => value.Status == requestedStatus
                            && value.DomainValue >= requestedMinimum
                            && value.Address.Street == requestedStreet)
                        .Select(value => new CompositeFieldProjection(
                            value.Address.HouseNumber,
                            value.Address.Street,
                            value.Address.Note,
                            EF.Functions.RecordField<int>(value.RecordAddress, "house_number"),
                            EF.Functions.RecordField<string>(value.RecordAddress, "street"),
                            EF.Functions.RecordField<string?>(value.RecordAddress, "note"))));
                Assert.Equal(
                    [new CompositeFieldProjection(221, "Baker Street", null, 19, "Record Road", null)],
                    compiledFields(context, status, minimumDomainValue, street).ToArray());
            }
        }
        finally
        {
            await ExecuteNonQueryAsync(
                connectionString,
                $"""
                DROP TABLE IF EXISTS "ef_user_type_values";
                DROP TYPE IF EXISTS public.{recordName};
                DROP TYPE IF EXISTS public.{compositeName};
                DROP DOMAIN IF EXISTS public.{domainName};
                DROP TYPE IF EXISTS public.{enumName}
                """);
        }
    }

    [Fact]
    public async Task PostgreSQL_ranges_multiranges_and_their_arrays_round_trip_through_EF_Core()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_range_values\"");
        await ExecuteNonQueryAsync(connectionString, CreateRangeTableSql);

        var intRange = new BlueTuskRange<int>(1, 5);
        var secondIntRange = new BlueTuskRange<int>(10, 20);
        var expected = new RangeValue
        {
            Id = 1,
            IntRange = intRange,
            LongRange = new BlueTuskRange<long>(1_000_000_000_000, 2_000_000_000_000),
            NumericRange = new BlueTuskRange<BlueTuskNumeric>(
                BlueTuskNumeric.Parse("1.25"),
                BlueTuskNumeric.Parse("99.75")),
            TimestampRange = new BlueTuskRange<DateTime>(
                new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Unspecified),
                new DateTime(2026, 1, 2, 17, 45, 0, DateTimeKind.Unspecified)),
            TimestampWithTimeZoneRange = new BlueTuskRange<DateTimeOffset>(
                new DateTimeOffset(2026, 1, 1, 8, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 2, 17, 45, 0, TimeSpan.Zero)),
            DateRange = new BlueTuskRange<DateOnly>(new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 1)),
            IntMultirange = new BlueTuskMultirange<int>([intRange, secondIntRange]),
            LongMultirange = new BlueTuskMultirange<long>(
                [new BlueTuskRange<long>(1, 5), new BlueTuskRange<long>(10, 15)]),
            NumericMultirange = new BlueTuskMultirange<BlueTuskNumeric>(
                [
                    new BlueTuskRange<BlueTuskNumeric>(
                        BlueTuskNumeric.Parse("1.25"),
                        BlueTuskNumeric.Parse("2.5")),
                    new BlueTuskRange<BlueTuskNumeric>(
                        BlueTuskNumeric.Parse("10"),
                        BlueTuskNumeric.Parse("20")),
                ]),
            TimestampMultirange = new BlueTuskMultirange<DateTime>(
                [
                    new BlueTuskRange<DateTime>(
                        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                        new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified)),
                ]),
            TimestampWithTimeZoneMultirange = new BlueTuskMultirange<DateTimeOffset>(
                [
                    new BlueTuskRange<DateTimeOffset>(
                        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
                ]),
            DateMultirange = new BlueTuskMultirange<DateOnly>(
                [
                    new BlueTuskRange<DateOnly>(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 5)),
                    new BlueTuskRange<DateOnly>(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 5)),
                ]),
            IntRangeArray = [intRange, BlueTuskRange.Empty<int>()],
            IntMultirangeArray =
            [
                new BlueTuskMultirange<int>([intRange, secondIntRange]),
                BlueTuskMultirange.Empty<int>(),
            ],
        };

        try
        {
            await using (var context = CreateRangeContext(connectionString))
            {
                context.Values.Add(expected);
                Assert.Equal(1, await context.SaveChangesAsync());
            }

            await using (var context = CreateRangeContext(connectionString))
            {
                var actual = await context.Values.AsNoTracking().SingleAsync();
                Assert.Equal(expected.IntRange, actual.IntRange);
                Assert.Equal(expected.LongRange, actual.LongRange);
                Assert.Equal(expected.NumericRange, actual.NumericRange);
                Assert.Equal(expected.TimestampRange, actual.TimestampRange);
                Assert.Equal(expected.TimestampWithTimeZoneRange, actual.TimestampWithTimeZoneRange);
                Assert.Equal(expected.DateRange, actual.DateRange);
                Assert.Equal(expected.IntMultirange, actual.IntMultirange);
                Assert.Equal(expected.LongMultirange, actual.LongMultirange);
                Assert.Equal(expected.NumericMultirange, actual.NumericMultirange);
                Assert.Equal(expected.TimestampMultirange, actual.TimestampMultirange);
                Assert.Equal(expected.TimestampWithTimeZoneMultirange, actual.TimestampWithTimeZoneMultirange);
                Assert.Equal(expected.DateMultirange, actual.DateMultirange);
                Assert.Equal(expected.IntRangeArray, actual.IntRangeArray);
                Assert.Equal(expected.IntMultirangeArray, actual.IntMultirangeArray);
                Assert.Null(actual.OptionalIntRange);
                Assert.Null(actual.OptionalIntMultirange);
            }
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_range_values\"");
        }
    }

    [Fact]
    public async Task PostgreSQL_arrays_round_trip_and_detect_in_place_changes_through_EF_Core()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_array_values\"");
        await ExecuteNonQueryAsync(connectionString, CreateArrayTableSql);

        var expected = new ArrayValue
        {
            Id = 1,
            IntValues = [1, 2, 3],
            MatrixValues = new[,] { { 1, 2 }, { 3, 4 } },
            TextValues = ["blue", null, "tusk"],
            BytesValues = [[0, 1, 2], [253, 254, 255]],
            GuidValues =
            [
                Guid.Parse("84057795-1ace-4ed0-8b1a-e399687814b7"),
                Guid.Parse("a41c84e3-6bfd-4873-a79a-bfb39fa6cf18"),
            ],
            DateValues = [new DateOnly(2026, 7, 31), new DateOnly(2027, 1, 1)],
            PointValues = [new BlueTuskPoint(1.5, -2.5), new BlueTuskPoint(3.25, 4.75)],
            CidrValues =
            [
                BlueTuskNetworkAddress.Parse("192.0.2.0/24", isCidr: true),
                BlueTuskNetworkAddress.Parse("2001:db8::/48", isCidr: true),
            ],
            BitValues = [new BlueTuskBitString("1010"), new BlueTuskBitString("0101")],
            JsonbValues = ["{\"index\":1}", "{\"index\":2}"],
        };

        try
        {
            await using (var context = CreateArrayContext(connectionString))
            {
                context.Values.Add(expected);
                Assert.Equal(1, await context.SaveChangesAsync());
            }

            await using (var context = CreateArrayContext(connectionString))
            {
                var tracked = await context.Values.SingleAsync();
                tracked.IntValues[0] = 42;
                Assert.Equal(1, await context.SaveChangesAsync());
            }

            await using (var context = CreateArrayContext(connectionString))
            {
                var actual = await context.Values.AsNoTracking().SingleAsync();
                Assert.Equal([42, 2, 3], actual.IntValues);
                Assert.Equal(expected.MatrixValues.Cast<int>(), actual.MatrixValues.Cast<int>());
                Assert.Equal(expected.TextValues, actual.TextValues);
                Assert.Equal(expected.BytesValues, actual.BytesValues);
                Assert.Equal(expected.GuidValues, actual.GuidValues);
                Assert.Equal(expected.DateValues, actual.DateValues);
                Assert.Equal(expected.PointValues, actual.PointValues);
                Assert.Equal(expected.CidrValues, actual.CidrValues);
                Assert.Equal(expected.BitValues, actual.BitValues);
                for (var index = 0; index < expected.JsonbValues.Length; index++)
                {
                    using var expectedJson = JsonDocument.Parse(expected.JsonbValues[index]);
                    using var actualJson = JsonDocument.Parse(actual.JsonbValues[index]);
                    Assert.True(JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement));
                }

                Assert.Null(actual.OptionalIntValues);
            }
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_array_values\"");
        }
    }

    [Fact]
    public async Task PostgreSQL_native_scalar_types_round_trip_through_EF_Core()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_native_type_values\"");
        await ExecuteNonQueryAsync(connectionString, CreateNativeTableSql);

        var expected = new NativeTypeValue
        {
            Id = 1,
            InetValue = BlueTuskNetworkAddress.Parse("192.0.2.42/24"),
            CidrValue = BlueTuskNetworkAddress.Parse("198.51.100.0/24", isCidr: true),
            MacAddressValue = BlueTuskMacAddress.Parse("08:00:2b:01:02:03"),
            MacAddress8Value = BlueTuskMacAddress8.Parse("08:00:2b:ff:fe:01:02:03"),
            PointValue = new BlueTuskPoint(12.5, -4.25),
            LineSegmentValue = new BlueTuskLineSegment(new BlueTuskPoint(0, 1), new BlueTuskPoint(2, 3)),
            BoxValue = new BlueTuskBox(new BlueTuskPoint(-1, -2), new BlueTuskPoint(3, 4)),
            CircleValue = new BlueTuskCircle(new BlueTuskPoint(5, 6), 7.5),
            BitValue = new BlueTuskBitString("101101"),
            LogSequenceNumberValue = BlueTuskLogSequenceNumber.Parse("16/B374D848"),
            NumericValue = BlueTuskNumeric.Parse("12345678901234567890.125"),
            TimeWithTimeZoneValue = BlueTuskTimeWithTimeZone.Parse("14:30:15.25+01:30"),
            IntervalValue = new BlueTuskInterval(months: 14, days: 3, microseconds: 4_500_000),
            JsonPathValue = new BlueTuskJsonPath("$.\"store\".\"book\"[*]?(@.\"price\" < 10)"),
            TextSearchVectorValue = BlueTuskTextSearchVector.Parse("'blue':1A 'tusk':2B"),
            TextSearchQueryValue = BlueTuskTextSearchQuery.Parse("'blue' & 'tusk':*"),
            JsonValue = "{\"provider\":\"BlueTusk\"}",
            JsonbValue = "{\"complete\":true,\"version\":3}",
            XmlValue = "<provider name=\"BlueTusk\" />",
        };

        try
        {
            await using (var context = CreateNativeContext(connectionString))
            {
                context.Values.Add(expected);
                Assert.Equal(1, await context.SaveChangesAsync());
            }

            await using (var context = CreateNativeContext(connectionString))
            {
                var actual = await context.Values.AsNoTracking().SingleAsync();
                Assert.Equal(expected.Id, actual.Id);
                Assert.Equal(expected.InetValue, actual.InetValue);
                Assert.Equal(expected.CidrValue, actual.CidrValue);
                Assert.Equal(expected.MacAddressValue, actual.MacAddressValue);
                Assert.Equal(expected.MacAddress8Value, actual.MacAddress8Value);
                Assert.Equal(expected.PointValue, actual.PointValue);
                Assert.Equal(expected.LineSegmentValue, actual.LineSegmentValue);
                Assert.Equal(expected.BoxValue, actual.BoxValue);
                Assert.Equal(expected.CircleValue, actual.CircleValue);
                Assert.Equal(expected.BitValue, actual.BitValue);
                Assert.Equal(expected.LogSequenceNumberValue, actual.LogSequenceNumberValue);
                Assert.Equal(expected.NumericValue, actual.NumericValue);
                Assert.Equal(expected.TimeWithTimeZoneValue, actual.TimeWithTimeZoneValue);
                Assert.Equal(expected.IntervalValue, actual.IntervalValue);
                Assert.Equal(expected.JsonPathValue, actual.JsonPathValue);
                Assert.Equal(expected.TextSearchVectorValue, actual.TextSearchVectorValue);
                Assert.Equal(expected.TextSearchQueryValue, actual.TextSearchQueryValue);
                Assert.Equal(expected.JsonValue, actual.JsonValue);
                using var expectedJsonb = JsonDocument.Parse(expected.JsonbValue);
                using var actualJsonb = JsonDocument.Parse(actual.JsonbValue);
                Assert.True(JsonElement.DeepEquals(expectedJsonb.RootElement, actualJsonb.RootElement));
                Assert.Equal(expected.XmlValue, actual.XmlValue);
                Assert.Null(actual.OptionalJsonPathValue);
            }
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_native_type_values\"");
        }
    }

    [Fact]
    public async Task Core_scalar_types_round_trip_through_EF_Core()
    {
        var connectionString = GetConnectionString();
        await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_type_values\"");
        await ExecuteNonQueryAsync(connectionString, CreateTableSql);

        var expected = new TypeValue
        {
            Id = 1,
            BooleanValue = true,
            ByteValue = 42,
            ShortValue = -1234,
            IntValue = 123456,
            LongValue = 9_876_543_210,
            FloatValue = 12.5f,
            DoubleValue = -9876.25,
            DecimalValue = 123456.7890m,
            StringValue = "BlueTusk types",
            CharacterValue = 'B',
            BytesValue = [0, 1, 2, 127, 255],
            GuidValue = Guid.Parse("d8a24f57-55f9-4e6e-b1ea-7cd66c468cc5"),
            DateTimeValue = new DateTime(2026, 7, 31, 14, 30, 15, DateTimeKind.Unspecified),
            DateTimeOffsetValue = new DateTimeOffset(2026, 7, 31, 13, 30, 15, TimeSpan.Zero),
            DateOnlyValue = new DateOnly(2026, 7, 31),
            TimeOnlyValue = new TimeOnly(14, 30, 15, 123),
            TimeSpanValue = new TimeSpan(2, 3, 4, 5, 678),
        };

        try
        {
            await using (var context = CreateContext(connectionString))
            {
                context.Values.Add(expected);
                Assert.Equal(1, await context.SaveChangesAsync());
            }

            await using (var context = CreateContext(connectionString))
            {
                var actual = await context.Values.AsNoTracking().SingleAsync();
                Assert.Equal(expected.Id, actual.Id);
                Assert.Equal(expected.BooleanValue, actual.BooleanValue);
                Assert.Equal(expected.ByteValue, actual.ByteValue);
                Assert.Equal(expected.ShortValue, actual.ShortValue);
                Assert.Equal(expected.IntValue, actual.IntValue);
                Assert.Equal(expected.LongValue, actual.LongValue);
                Assert.Equal(expected.FloatValue, actual.FloatValue);
                Assert.Equal(expected.DoubleValue, actual.DoubleValue);
                Assert.Equal(expected.DecimalValue, actual.DecimalValue);
                Assert.Equal(expected.StringValue, actual.StringValue);
                Assert.Equal(expected.CharacterValue, actual.CharacterValue);
                Assert.Equal(expected.BytesValue, actual.BytesValue);
                Assert.Equal(expected.GuidValue, actual.GuidValue);
                Assert.Equal(expected.DateTimeValue, actual.DateTimeValue);
                Assert.Equal(expected.DateTimeOffsetValue, actual.DateTimeOffsetValue);
                Assert.Equal(expected.DateOnlyValue, actual.DateOnlyValue);
                Assert.Equal(expected.TimeOnlyValue, actual.TimeOnlyValue);
                Assert.Equal(expected.TimeSpanValue, actual.TimeSpanValue);
            }
        }
        finally
        {
            await ExecuteNonQueryAsync(connectionString, "DROP TABLE IF EXISTS \"ef_type_values\"");
        }
    }

    private const string CreateTableSql = """
        CREATE TABLE "ef_type_values" (
            "Id" integer PRIMARY KEY,
            "BooleanValue" boolean NOT NULL,
            "ByteValue" smallint NOT NULL,
            "ShortValue" smallint NOT NULL,
            "IntValue" integer NOT NULL,
            "LongValue" bigint NOT NULL,
            "FloatValue" real NOT NULL,
            "DoubleValue" double precision NOT NULL,
            "DecimalValue" numeric(18,4) NOT NULL,
            "StringValue" character varying(64) NOT NULL,
            "CharacterValue" character(1) NOT NULL,
            "BytesValue" bytea NOT NULL,
            "GuidValue" uuid NOT NULL,
            "DateTimeValue" timestamp without time zone NOT NULL,
            "DateTimeOffsetValue" timestamp with time zone NOT NULL,
            "DateOnlyValue" date NOT NULL,
            "TimeOnlyValue" time without time zone NOT NULL,
            "TimeSpanValue" interval NOT NULL)
        """;

    private const string CreateNativeTableSql = """
        CREATE TABLE "ef_native_type_values" (
            "Id" integer PRIMARY KEY,
            "InetValue" inet NOT NULL,
            "CidrValue" cidr NOT NULL,
            "MacAddressValue" macaddr NOT NULL,
            "MacAddress8Value" macaddr8 NOT NULL,
            "PointValue" point NOT NULL,
            "LineSegmentValue" lseg NOT NULL,
            "BoxValue" box NOT NULL,
            "CircleValue" circle NOT NULL,
            "BitValue" bit(6) NOT NULL,
            "LogSequenceNumberValue" pg_lsn NOT NULL,
            "NumericValue" numeric NOT NULL,
            "TimeWithTimeZoneValue" time with time zone NOT NULL,
            "IntervalValue" interval NOT NULL,
            "JsonPathValue" jsonpath NOT NULL,
            "TextSearchVectorValue" tsvector NOT NULL,
            "TextSearchQueryValue" tsquery NOT NULL,
            "JsonValue" json NOT NULL,
            "JsonbValue" jsonb NOT NULL,
            "XmlValue" xml NOT NULL,
            "OptionalJsonPathValue" jsonpath NULL)
        """;

    private const string CreateArrayTableSql = """
        CREATE TABLE "ef_array_values" (
            "Id" integer PRIMARY KEY,
            "IntValues" integer[] NOT NULL,
            "MatrixValues" integer[] NOT NULL,
            "TextValues" text[] NOT NULL,
            "BytesValues" bytea[] NOT NULL,
            "GuidValues" uuid[] NOT NULL,
            "DateValues" date[] NOT NULL,
            "PointValues" point[] NOT NULL,
            "CidrValues" cidr[] NOT NULL,
            "BitValues" bit(4)[] NOT NULL,
            "JsonbValues" jsonb[] NOT NULL,
            "OptionalIntValues" integer[] NULL)
        """;

    private const string CreateRangeTableSql = """
        CREATE TABLE "ef_range_values" (
            "Id" integer PRIMARY KEY,
            "IntRange" int4range NOT NULL,
            "LongRange" int8range NOT NULL,
            "NumericRange" numrange NOT NULL,
            "TimestampRange" tsrange NOT NULL,
            "TimestampWithTimeZoneRange" tstzrange NOT NULL,
            "DateRange" daterange NOT NULL,
            "IntMultirange" int4multirange NOT NULL,
            "LongMultirange" int8multirange NOT NULL,
            "NumericMultirange" nummultirange NOT NULL,
            "TimestampMultirange" tsmultirange NOT NULL,
            "TimestampWithTimeZoneMultirange" tstzmultirange NOT NULL,
            "DateMultirange" datemultirange NOT NULL,
            "IntRangeArray" int4range[] NOT NULL,
            "IntMultirangeArray" int4multirange[] NOT NULL,
            "OptionalIntRange" int4range NULL,
            "OptionalIntMultirange" int4multirange NULL)
        """;

    private static TypeValueContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TypeValueContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new TypeValueContext(options);
    }

    private static NativeTypeValueContext CreateNativeContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<NativeTypeValueContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new NativeTypeValueContext(options);
    }

    private static ArrayValueContext CreateArrayContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ArrayValueContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new ArrayValueContext(options);
    }

    private static RangeValueContext CreateRangeContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<RangeValueContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new RangeValueContext(options);
    }

    private static UserTypeValueContext CreateUserTypeContext(
        BlueTuskDataSource dataSource,
        string enumName,
        string domainName,
        string compositeName,
        string recordName)
    {
        var options = new DbContextOptionsBuilder<UserTypeValueContext>()
            .UseBlueTusk(dataSource)
            .Options;
        return new UserTypeValueContext(options, enumName, domainName, compositeName, recordName);
    }

    private static async Task ExecuteNonQueryAsync(string connectionString, string sql)
    {
        await using var connection = new BlueTuskConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
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

    private sealed class TypeValueContext(DbContextOptions<TypeValueContext> options) : DbContext(options)
    {
        public DbSet<TypeValue> Values => Set<TypeValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var value = modelBuilder.Entity<TypeValue>();
            value.ToTable("ef_type_values");
            value.HasKey(entity => entity.Id);
            value.Property(entity => entity.Id).ValueGeneratedNever();
            value.Property(entity => entity.DecimalValue).HasPrecision(18, 4);
            value.Property(entity => entity.StringValue).HasMaxLength(64);
        }
    }

    private sealed class NativeTypeValueContext(DbContextOptions<NativeTypeValueContext> options) : DbContext(options)
    {
        public DbSet<NativeTypeValue> Values => Set<NativeTypeValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var value = modelBuilder.Entity<NativeTypeValue>();
            value.ToTable("ef_native_type_values");
            value.HasKey(entity => entity.Id);
            value.Property(entity => entity.Id).ValueGeneratedNever();
            value.Property(entity => entity.CidrValue).HasColumnType("cidr");
            value.Property(entity => entity.BitValue).HasColumnType("bit(6)");
            value.Property(entity => entity.JsonValue).HasColumnType("json");
            value.Property(entity => entity.JsonbValue).HasColumnType("jsonb");
            value.Property(entity => entity.XmlValue).HasColumnType("xml");
        }
    }

    private sealed class ArrayValueContext(DbContextOptions<ArrayValueContext> options) : DbContext(options)
    {
        public DbSet<ArrayValue> Values => Set<ArrayValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var value = modelBuilder.Entity<ArrayValue>();
            value.ToTable("ef_array_values");
            value.HasKey(entity => entity.Id);
            value.Property(entity => entity.Id).ValueGeneratedNever();
            value.Property(entity => entity.CidrValues).HasColumnType("cidr[]");
            value.Property(entity => entity.BitValues).HasColumnType("bit(4)[]");
            value.Property(entity => entity.JsonbValues).HasColumnType("jsonb[]");
        }
    }

    private sealed class RangeValueContext(DbContextOptions<RangeValueContext> options) : DbContext(options)
    {
        public DbSet<RangeValue> Values => Set<RangeValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var value = modelBuilder.Entity<RangeValue>();
            value.ToTable("ef_range_values");
            value.HasKey(entity => entity.Id);
            value.Property(entity => entity.Id).ValueGeneratedNever();
        }
    }

    private sealed class UserTypeValueContext(
        DbContextOptions<UserTypeValueContext> options,
        string enumName,
        string domainName,
        string compositeName,
        string recordName) : DbContext(options)
    {
        public DbSet<UserTypeValue> Values => Set<UserTypeValue>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var value = modelBuilder.Entity<UserTypeValue>();
            value.ToTable("ef_user_type_values");
            value.HasKey(entity => entity.Id);
            value.Property(entity => entity.Id).ValueGeneratedNever();
            value.Property(entity => entity.Status).HasColumnType($"public.{enumName}");
            value.Property(entity => entity.OptionalStatus).HasColumnType($"public.{enumName}");
            value.PrimitiveCollection(entity => entity.Statuses)
                .HasColumnType($"public.{enumName}[]")
                .ElementType(element => element.HasStoreType($"public.{enumName}"));
            value.PrimitiveCollection(entity => entity.OptionalStatuses)
                .HasColumnType($"public.{enumName}[]")
                .ElementType(element => element.HasStoreType($"public.{enumName}"));
            value.Property(entity => entity.DomainValue).HasColumnType($"public.{domainName}");
            value.Property(entity => entity.OptionalDomainValue).HasColumnType($"public.{domainName}");
            value.PrimitiveCollection(entity => entity.DomainValues)
                .HasColumnType($"public.{domainName}[]")
                .ElementType(element => element.HasStoreType($"public.{domainName}"));
            value.Property(entity => entity.Address).HasColumnType($"public.{compositeName}");
            value.Property(entity => entity.OptionalAddress).HasColumnType($"public.{compositeName}");
            value.PrimitiveCollection(entity => entity.Addresses)
                .HasColumnType($"public.{compositeName}[]")
                .ElementType(element => element.HasStoreType($"public.{compositeName}"));
            value.Property(entity => entity.RecordAddress).HasColumnType($"public.{recordName}");
            value.PrimitiveCollection(entity => entity.RecordAddresses)
                .HasColumnType($"public.{recordName}[]")
                .ElementType(element => element.HasStoreType($"public.{recordName}"));
        }
    }

    private sealed class TypeValue
    {
        public int Id { get; set; }
        public bool BooleanValue { get; set; }
        public byte ByteValue { get; set; }
        public short ShortValue { get; set; }
        public int IntValue { get; set; }
        public long LongValue { get; set; }
        public float FloatValue { get; set; }
        public double DoubleValue { get; set; }
        public decimal DecimalValue { get; set; }
        public string StringValue { get; set; } = string.Empty;
        public char CharacterValue { get; set; }
        public byte[] BytesValue { get; set; } = [];
        public Guid GuidValue { get; set; }
        public DateTime DateTimeValue { get; set; }
        public DateTimeOffset DateTimeOffsetValue { get; set; }
        public DateOnly DateOnlyValue { get; set; }
        public TimeOnly TimeOnlyValue { get; set; }
        public TimeSpan TimeSpanValue { get; set; }
    }

    private sealed class NativeTypeValue
    {
        public int Id { get; set; }
        public BlueTuskNetworkAddress InetValue { get; set; }
        public BlueTuskNetworkAddress CidrValue { get; set; }
        public BlueTuskMacAddress MacAddressValue { get; set; }
        public BlueTuskMacAddress8 MacAddress8Value { get; set; }
        public BlueTuskPoint PointValue { get; set; }
        public BlueTuskLineSegment LineSegmentValue { get; set; }
        public BlueTuskBox BoxValue { get; set; }
        public BlueTuskCircle CircleValue { get; set; }
        public BlueTuskBitString BitValue { get; set; }
        public BlueTuskLogSequenceNumber LogSequenceNumberValue { get; set; }
        public BlueTuskNumeric NumericValue { get; set; }
        public BlueTuskTimeWithTimeZone TimeWithTimeZoneValue { get; set; }
        public BlueTuskInterval IntervalValue { get; set; }
        public BlueTuskJsonPath JsonPathValue { get; set; }
        public BlueTuskTextSearchVector TextSearchVectorValue { get; set; } = BlueTuskTextSearchVector.Parse(string.Empty);
        public BlueTuskTextSearchQuery TextSearchQueryValue { get; set; } = BlueTuskTextSearchQuery.Empty;
        public string JsonValue { get; set; } = string.Empty;
        public string JsonbValue { get; set; } = string.Empty;
        public string XmlValue { get; set; } = string.Empty;
        public BlueTuskJsonPath? OptionalJsonPathValue { get; set; }
    }

    private sealed class ArrayValue
    {
        public int Id { get; set; }
        public int[] IntValues { get; set; } = [];
        public int[,] MatrixValues { get; set; } = new int[0, 0];
        public string?[] TextValues { get; set; } = [];
        public byte[][] BytesValues { get; set; } = [];
        public Guid[] GuidValues { get; set; } = [];
        public DateOnly[] DateValues { get; set; } = [];
        public BlueTuskPoint[] PointValues { get; set; } = [];
        public BlueTuskNetworkAddress[] CidrValues { get; set; } = [];
        public BlueTuskBitString[] BitValues { get; set; } = [];
        public string[] JsonbValues { get; set; } = [];
        public int[]? OptionalIntValues { get; set; }
    }

    private sealed class RangeValue
    {
        public int Id { get; set; }
        public BlueTuskRange<int> IntRange { get; set; }
        public BlueTuskRange<long> LongRange { get; set; }
        public BlueTuskRange<BlueTuskNumeric> NumericRange { get; set; }
        public BlueTuskRange<DateTime> TimestampRange { get; set; }
        public BlueTuskRange<DateTimeOffset> TimestampWithTimeZoneRange { get; set; }
        public BlueTuskRange<DateOnly> DateRange { get; set; }
        public BlueTuskMultirange<int> IntMultirange { get; set; } = BlueTuskMultirange.Empty<int>();
        public BlueTuskMultirange<long> LongMultirange { get; set; } = BlueTuskMultirange.Empty<long>();
        public BlueTuskMultirange<BlueTuskNumeric> NumericMultirange { get; set; } =
            BlueTuskMultirange.Empty<BlueTuskNumeric>();
        public BlueTuskMultirange<DateTime> TimestampMultirange { get; set; } =
            BlueTuskMultirange.Empty<DateTime>();
        public BlueTuskMultirange<DateTimeOffset> TimestampWithTimeZoneMultirange { get; set; } =
            BlueTuskMultirange.Empty<DateTimeOffset>();
        public BlueTuskMultirange<DateOnly> DateMultirange { get; set; } = BlueTuskMultirange.Empty<DateOnly>();
        public BlueTuskRange<int>[] IntRangeArray { get; set; } = [];
        public BlueTuskMultirange<int>[] IntMultirangeArray { get; set; } = [];
        public BlueTuskRange<int>? OptionalIntRange { get; set; }
        public BlueTuskMultirange<int>? OptionalIntMultirange { get; set; }
    }

    private sealed class UserTypeValue
    {
        public int Id { get; set; }
        public EfOrderStatus Status { get; set; }
        public EfOrderStatus? OptionalStatus { get; set; }
        public EfOrderStatus[] Statuses { get; set; } = [];
        public EfOrderStatus[]? OptionalStatuses { get; set; }
        public int DomainValue { get; set; }
        public int? OptionalDomainValue { get; set; }
        public int[] DomainValues { get; set; } = [];
        public EfAddress Address { get; set; } = new(0, string.Empty, null);
        public EfAddress? OptionalAddress { get; set; }
        public EfAddress[] Addresses { get; set; } = [];
        public BlueTuskRecord RecordAddress { get; set; } = CreateAddressRecord(0, string.Empty, null);
        public BlueTuskRecord[] RecordAddresses { get; set; } = [];
    }

    private enum EfOrderStatus
    {
        [BlueTuskName("pending")]
        Pending,

        [BlueTuskName("in-progress")]
        InProgress,

        Complete,
    }

    private sealed record EfAddress(int HouseNumber, string Street, string? Note);

    private sealed record CompositeFieldProjection(
        int HouseNumber,
        string Street,
        string? Note,
        int RecordHouseNumber,
        string RecordStreet,
        string? RecordNote);

    private static BlueTuskRecord CreateAddressRecord(int houseNumber, string street, string? note) => new(
    [
        new BlueTuskRecordField("house_number", BlueTuskBuiltInTypes.Int4, houseNumber),
        new BlueTuskRecordField("street", BlueTuskBuiltInTypes.Text, street),
        new BlueTuskRecordField("note", BlueTuskBuiltInTypes.Text, note),
    ]);

    private static void AssertAddressRecord(BlueTuskRecord expected, BlueTuskRecord actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(expected[index].Type!.Name, actual[index].Type!.Name);
            Assert.Equal(expected[index].Value, actual[index].Value);
        }
    }
}
