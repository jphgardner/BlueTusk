using BlueTusk.Client;
using BlueTusk.Data;
using Microsoft.EntityFrameworkCore;
using Xunit.Sdk;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class TypeMappingIntegrationTests
{
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

    private static TypeValueContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TypeValueContext>()
            .UseBlueTusk(connectionString)
            .Options;
        return new TypeValueContext(options);
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
}
