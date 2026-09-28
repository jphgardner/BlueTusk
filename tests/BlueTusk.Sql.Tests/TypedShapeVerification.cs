using System.Data.Common;
using System.Text.Json;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sql.Verification;

internal static class TypedShapeVerification
{
    internal static async Task RunAsync(DbDataSource source, CancellationToken token)
    {
        var arguments = new global::BlueTusk.Sql.Verification.Generated.TypeShapes.Arguments([true, false], [short.MinValue, null, short.MaxValue],
            [long.MinValue, null, long.MaxValue], [Guid.Empty, Guid.NewGuid()], [float.MinValue, float.NaN, float.PositiveInfinity],
            [double.MinValue, double.NaN, double.NegativeInfinity], [decimal.MinValue, decimal.MaxValue, -0.00001m],
            [BlueTuskNumeric.Parse("1234567890123456789012345678901234567890.000000000001"), BlueTuskNumeric.Parse("-0.000000000001")],
            [new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), new DateTime(2026, 9, 27, 12, 34, 56, DateTimeKind.Unspecified)],
            [new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(1))], [DateOnly.MinValue, new DateOnly(2026, 9, 27)],
            ["{\"answer\":42}", null], ["{\"answer\":42}", null], ["x,y", null, "NULL", "🐘"],
            new DateOnly(2026, 9, 27), new TimeOnly(12, 34, 56, 123), new BlueTuskInterval(13, -2, 1_234_567));
        await global::BlueTusk.Sql.Verification.Generated.TypeShapes.Definition.ValidateAsync(source, arguments, token);
        await using var connection = await source.OpenConnectionAsync(token);
        var count = 0;
        await foreach (var row in global::BlueTusk.Sql.Verification.Generated.TypeShapes.Definition.ReadAsync(connection, arguments, cancellationToken: token))
        {
            if (!row.Flags.SequenceEqual(arguments.flags) || !row.Shorts.SequenceEqual(arguments.shorts) || !row.Longs.SequenceEqual(arguments.longs) ||
                !row.Keys.SequenceEqual(arguments.keys) || !row.Singles.SequenceEqual(arguments.singles) || !row.Doubles.SequenceEqual(arguments.doubles) ||
                !row.Decimals.SequenceEqual(arguments.decimals) || !row.Precise.SequenceEqual(arguments.precise) ||
                !row.Timestamps.SequenceEqual(arguments.timestamps) || !row.Timezones.SequenceEqual(arguments.timezones) || !row.Dates.SequenceEqual(arguments.dates) ||
                !row.Jsons.SequenceEqual(arguments.jsons) || !row.Words.SequenceEqual(arguments.words) || row.Day != arguments.day || row.Time != arguments.time ||
                row.Interval != arguments.interval || row.Jsonbs.Length != 2 || row.Jsonbs[1] is not null)
            {
                throw new InvalidOperationException("Generated PostgreSQL type roundtrip changed a value, null element or temporal component.");
            }
            using var json = JsonDocument.Parse(row.Jsonbs[0]!);
            if (json.RootElement.GetProperty("answer").GetInt32() != 42) { throw new InvalidOperationException("Generated jsonb array changed semantic content."); }
            count++;
        }
        if (count != 1) { throw new InvalidOperationException("Generated PostgreSQL type roundtrip produced an unexpected row count."); }
    }
}
