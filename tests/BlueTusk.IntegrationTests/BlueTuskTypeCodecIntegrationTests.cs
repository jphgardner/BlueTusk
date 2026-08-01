using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.IntegrationTests;

public sealed class BlueTuskTypeCodecIntegrationTests
{
    [Fact]
    public async Task AdoNet_retries_text_only_catalogue_results_and_preserves_transactions()
    {
        var value = new BlueTuskAccessControlItem("postgres=arwdDxt/postgres");
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);

        await using (var command = CreateAclCommand(connection, transaction: null, value))
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(value, reader.GetFieldValue<BlueTuskAccessControlItem>(0));
            Assert.Equal([value], reader.GetFieldValue<BlueTuskAccessControlItem[]>(1));
            Assert.Empty(reader.GetFieldValue<BlueTuskAccessControlItem[]>(2));
        }

        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await using (var command = CreateAclCommand(connection, transaction, value))
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(value, reader.GetFieldValue<BlueTuskAccessControlItem>(0));
        }

        await using var verify = new BlueTuskCommand("SELECT $1::int4", connection)
        {
            Transaction = transaction,
        };
        verify.Parameters.Add(new BlueTuskParameter<int>(42));
        Assert.Equal(42, await verify.ExecuteScalarAsync<int>(CancellationToken.None));
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AdoNet_decodes_opaque_extended_statistics_values()
    {
        await using var connection = new BlueTuskConnection(GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using (var setup = new BlueTuskCommand(
                         """
                         CREATE TEMP TABLE bluetusk_opaque_statistics (a int4, b int4);
                         INSERT INTO bluetusk_opaque_statistics
                         SELECT value % 10, value % 10
                         FROM generate_series(1, 1000) AS value;
                         CREATE STATISTICS bluetusk_opaque_statistics_sample
                         (ndistinct, dependencies, mcv)
                         ON a, b FROM bluetusk_opaque_statistics;
                         ANALYZE bluetusk_opaque_statistics;
                         """,
                         connection))
        {
            _ = await setup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        BlueTuskNDistinctStatistics ndistinct;
        BlueTuskDependencyStatistics dependencies;
        BlueTuskMostCommonValueStatistics mostCommonValues;
        await using (var query = new BlueTuskCommand(
                         """
                         SELECT data.stxdndistinct, data.stxddependencies, data.stxdmcv
                         FROM pg_catalog.pg_statistic_ext_data AS data
                         JOIN pg_catalog.pg_statistic_ext AS definition
                           ON definition.oid = data.stxoid
                         WHERE definition.stxname = $1
                         """,
                         connection))
        {
            query.Parameters.Add(
                new BlueTuskParameter<string>("bluetusk_opaque_statistics_sample"));
            await using var reader = await query.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            ndistinct = reader.GetFieldValue<BlueTuskNDistinctStatistics>(0);
            dependencies = reader.GetFieldValue<BlueTuskDependencyStatistics>(1);
            mostCommonValues = reader.GetFieldValue<BlueTuskMostCommonValueStatistics>(2);
        }

        AssertOpaqueBinaryValue(ndistinct);
        AssertOpaqueBinaryValue(dependencies);
        AssertOpaqueBinaryValue(mostCommonValues);
    }

    [Fact]
    public async Task Extended_queries_negotiate_binary_result_columns()
    {
        var settings = new BlueTuskConnectionStringBuilder(GetConnectionString());
        await using var session = await BlueTuskSession.OpenAsync(
            new BlueTuskClientOptions
            {
                Host = settings.Host,
                Port = settings.Port,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                SslMode = BlueTuskSslMode.Disable,
                ChannelBinding = BlueTuskChannelBindingMode.Disable,
            },
            CancellationToken.None);

        var result = await session.ExecuteExtendedQueryAsync(
            "SELECT $1::bool AS flag, 42::int4 AS answer",
            [new BlueTuskExtendedQueryParameter(16, 1, new byte[] { 1 })],
            CancellationToken.None);

        var resultSet = Assert.Single(result.ResultSets);
        Assert.All(resultSet.Fields, field => Assert.Equal(1, field.FormatCode));
        var row = Assert.Single(resultSet.Rows);
        Assert.Equal(1, row.Values[0]!.Value.Length);
        Assert.Equal(sizeof(int), row.Values[1]!.Value.Length);
    }

    [Fact]
    public async Task AdoNet_decodes_core_binary_scalar_families_and_edge_values()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand(
            "SELECT " +
            "$1::bool AS flag, " +
            "'-32768'::int2 AS small_value, " +
            "'9223372036854775807'::int8 AS big_value, " +
            "'4294967295'::oid AS oid_value, " +
            "1.25::float4 AS single_value, " +
            "'Infinity'::float8 AS double_value, " +
            "'123456789012345678901234567890.123456789'::numeric AS numeric_value, " +
            "'00112233-4455-6677-8899-aabbccddeeff'::uuid AS uuid_value, " +
            "decode('0001ff', 'hex') AS bytes_value, " +
            "'infinity'::date AS date_value, " +
            "'24:00:00'::time AS time_value, " +
            "'-infinity'::timestamp AS timestamp_value, " +
            "'2000-01-01 00:00:00+02'::timestamptz AS timestamptz_value, " +
            "'{\"answer\":42}'::json AS json_value, " +
            "'{\"enabled\":true}'::jsonb AS jsonb_value");
        command.Parameters.Add(new BlueTuskParameter<bool>(true));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(short.MinValue, reader.GetInt16(1));
        Assert.Equal(long.MaxValue, reader.GetInt64(2));
        Assert.Equal(uint.MaxValue, reader.GetFieldValue<uint>(3));
        Assert.Equal(1.25F, reader.GetFloat(4));
        Assert.Equal(double.PositiveInfinity, reader.GetDouble(5));
        Assert.Equal(
            BlueTuskNumeric.Parse("123456789012345678901234567890.123456789"),
            reader.GetFieldValue<BlueTuskNumeric>(6));
        Assert.Equal(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), reader.GetGuid(7));
        Assert.Equal(new byte[] { 0, 1, 255 }, reader.GetFieldValue<byte[]>(8));
        Assert.Equal(DateOnly.MaxValue, reader.GetFieldValue<DateOnly>(9));
        Assert.Equal(TimeSpan.FromDays(1), reader.GetFieldValue<TimeSpan>(10));
        Assert.Equal(DateTime.MinValue, reader.GetDateTime(11));
        Assert.Equal(
            new DateTimeOffset(1999, 12, 31, 22, 0, 0, TimeSpan.Zero),
            reader.GetFieldValue<DateTimeOffset>(12));
        Assert.Contains("42", reader.GetString(13), StringComparison.Ordinal);
        Assert.Contains("true", reader.GetString(14), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Binary_temporal_uuid_and_numeric_parameters_round_trip()
    {
        var uuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var date = new DateOnly(2026, 7, 21);
        var time = new TimeSpan(23, 59, 59) + TimeSpan.FromTicks(123_456 * TimeSpan.TicksPerMicrosecond);
        var timestamp = new DateTime(2026, 7, 21, 12, 34, 56).AddTicks(654_321 * TimeSpan.TicksPerMicrosecond);
        var timestampWithTimeZone = new DateTimeOffset(2026, 7, 21, 12, 34, 56, TimeSpan.FromHours(2));
        var numeric = BlueTuskNumeric.Parse("999999999999999999999999999999.000000001");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand(
            "SELECT $1::uuid, $2::date, $3::time, $4::timestamp, $5::timestamptz, $6::numeric");
        command.Parameters.Add(new BlueTuskParameter<Guid>(uuid));
        command.Parameters.Add(new BlueTuskParameter<DateOnly>(date));
        command.Parameters.Add(new BlueTuskParameter<TimeSpan>(time));
        command.Parameters.Add(new BlueTuskParameter<DateTime>(timestamp));
        command.Parameters.Add(new BlueTuskParameter<DateTimeOffset>(timestampWithTimeZone));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskNumeric>(numeric));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(uuid, reader.GetGuid(0));
        Assert.Equal(date, reader.GetFieldValue<DateOnly>(1));
        Assert.Equal(time, reader.GetFieldValue<TimeSpan>(2));
        Assert.Equal(timestamp, reader.GetDateTime(3));
        Assert.Equal(timestampWithTimeZone.UtcDateTime, reader.GetFieldValue<DateTimeOffset>(4).UtcDateTime);
        Assert.Equal(numeric, reader.GetFieldValue<BlueTuskNumeric>(5));
    }

    [Fact]
    public async Task Advanced_postgresql_scalars_decode_from_binary_results()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var versionCommand = dataSource.CreateCommand("SHOW server_version_num");
        var serverVersion = int.Parse(
            (await versionCommand.ExecuteScalarAsync<string>(CancellationToken.None))!,
            System.Globalization.CultureInfo.InvariantCulture);
        var infinityExpression = serverVersion >= 170_000
            ? "'infinity'::interval"
            : "'0 seconds'::interval";
        await using var command = dataSource.CreateCommand(
            "SELECT $1::int4, " +
            "'(42,7)'::tid, " +
            "'24:00:00+05:30:45'::timetz, " +
            "'1 year 2 mons 3 days 04:05:06.789'::interval, " +
            infinityExpression + ", " +
            "B'10110'::bit(5), " +
            "B'001011'::varbit, " +
            "'16/B374D848'::pg_lsn");
        command.Parameters.Add(new BlueTuskParameter<int>(1));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(new BlueTuskTupleId(42, 7), reader.GetFieldValue<BlueTuskTupleId>(1));
        Assert.Equal(
            new BlueTuskTimeWithTimeZone(TimeSpan.FromDays(1), new TimeSpan(5, 30, 45)),
            reader.GetFieldValue<BlueTuskTimeWithTimeZone>(2));
        Assert.Equal(new BlueTuskInterval(14, 3, 14_706_789_000), reader.GetFieldValue<BlueTuskInterval>(3));
        Assert.Equal(
            serverVersion >= 170_000
                ? BlueTuskInterval.PositiveInfinity
                : new BlueTuskInterval(0, 0, 0),
            reader.GetFieldValue<BlueTuskInterval>(4));
        Assert.Equal(new BlueTuskBitString("10110"), reader.GetFieldValue<BlueTuskBitString>(5));
        Assert.Equal(new BlueTuskBitString("001011"), reader.GetFieldValue<BlueTuskBitString>(6));
        Assert.Equal(
            BlueTuskLogSequenceNumber.Parse("16/B374D848"),
            reader.GetFieldValue<BlueTuskLogSequenceNumber>(7));
    }

    [Fact]
    public async Task Advanced_postgresql_scalar_parameters_round_trip_in_binary()
    {
        var tupleId = new BlueTuskTupleId(42, 7);
        var timeWithTimeZone = new BlueTuskTimeWithTimeZone(
            new TimeSpan(23, 59, 59) + TimeSpan.FromTicks(123_456 * TimeSpan.TicksPerMicrosecond),
            new TimeSpan(-8, -30, -45));
        var interval = new BlueTuskInterval(-10, -3, 14_106_789_000);
        var bits = new BlueTuskBitString("1011001");
        var lsn = BlueTuskLogSequenceNumber.Parse("16/B374D848");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand(
            "SELECT $1::tid, $2::timetz, $3::interval, $4::varbit, $5::pg_lsn");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskTupleId>(tupleId));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskTimeWithTimeZone>(timeWithTimeZone));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskInterval>(interval));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskBitString>(bits));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskLogSequenceNumber>(lsn));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(tupleId, reader.GetFieldValue<BlueTuskTupleId>(0));
        Assert.Equal(timeWithTimeZone, reader.GetFieldValue<BlueTuskTimeWithTimeZone>(1));
        Assert.Equal(interval, reader.GetFieldValue<BlueTuskInterval>(2));
        Assert.Equal(bits, reader.GetFieldValue<BlueTuskBitString>(3));
        Assert.Equal(lsn, reader.GetFieldValue<BlueTuskLogSequenceNumber>(4));
    }

    [Fact]
    public async Task Transaction_catalogue_types_and_arrays_round_trip_in_binary()
    {
        var transactionId = new BlueTuskTransactionId(uint.MaxValue);
        var commandId = new BlueTuskCommandId(uint.MaxValue);
        var fullTransactionId = new BlueTuskFullTransactionId(ulong.MaxValue);
        var snapshot = new BlueTuskTransactionSnapshot(10, 20, [12, 15]);
        BlueTuskFullTransactionId[] fullTransactionIds =
        [
            new BlueTuskFullTransactionId(0),
            fullTransactionId,
        ];
        BlueTuskTransactionSnapshot[] snapshots = [snapshot];

        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand(
            "SELECT $1::xid, $2::cid, $3::xid8, $4::pg_snapshot, " +
            "$5::txid_snapshot, $6::xid8[], $7::pg_snapshot[]");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskTransactionId>(transactionId));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskCommandId>(commandId));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskFullTransactionId>(fullTransactionId));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskTransactionSnapshot>(snapshot));
        command.Parameters.Add(
            new BlueTuskParameter<BlueTuskTransactionSnapshot>(snapshot)
            {
                PostgreSqlTypeOid = BlueTuskBuiltInTypes.TxidSnapshot.Id.Oid,
            });
        command.Parameters.Add(
            new BlueTuskParameter<BlueTuskFullTransactionId[]>(fullTransactionIds));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskTransactionSnapshot[]>(snapshots));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(transactionId, reader.GetFieldValue<BlueTuskTransactionId>(0));
        Assert.Equal(commandId, reader.GetFieldValue<BlueTuskCommandId>(1));
        Assert.Equal(fullTransactionId, reader.GetFieldValue<BlueTuskFullTransactionId>(2));
        Assert.Equal(snapshot, reader.GetFieldValue<BlueTuskTransactionSnapshot>(3));
        Assert.Equal(snapshot, reader.GetFieldValue<BlueTuskTransactionSnapshot>(4));
        Assert.Equal(fullTransactionIds, reader.GetFieldValue<BlueTuskFullTransactionId[]>(5));
        Assert.Equal(snapshots, reader.GetFieldValue<BlueTuskTransactionSnapshot[]>(6));
    }

    [Fact]
    public async Task Every_queryable_pg_catalog_base_range_and_multirange_has_a_codec()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await dataSource.ReloadTypesAsync(CancellationToken.None);
        await using var command = dataSource.CreateCommand(
            "SELECT oid, typname FROM pg_catalog.pg_type " +
            "WHERE typnamespace = 'pg_catalog'::regnamespace " +
            "AND typtype IN ('b', 'r', 'm') " +
            "AND typinput <> 'pg_catalog.array_in'::regproc ORDER BY oid");
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        var missing = new List<string>();
        while (await reader.ReadAsync(CancellationToken.None))
        {
            var oid = reader.GetFieldValue<uint>(0);
            var name = reader.GetString(1);
            if (!dataSource.TypeRegistry.TryGetCodec(new BlueTuskTypeId(oid), out _))
            {
                missing.Add($"{name} ({oid})");
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public async Task PostgreSQL_19_oid8_regdatabase_and_arrays_round_trip()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        if (connection.ServerCapabilities is not { ServerVersion.Major: >= 19 })
        {
            return;
        }

        await dataSource.ReloadTypesAsync(CancellationToken.None);
        var oid8 = new BlueTuskObjectIdentifier64(ulong.MaxValue);
        BlueTuskObjectIdentifier64[] oid8Values =
        [
            new BlueTuskObjectIdentifier64(0),
            oid8,
        ];
        var database = new BlueTuskRegDatabase("template1");
        BlueTuskRegDatabase[] databases =
        [
            new BlueTuskRegDatabase("template1"),
            new BlueTuskRegDatabase("postgres"),
        ];

        await using (var binaryCommand = dataSource.CreateCommand(
            "SELECT $1::oid8, $2::oid8[], $3::regdatabase, $4::regdatabase[]"))
        {
            binaryCommand.Parameters.Add(new BlueTuskParameter<BlueTuskObjectIdentifier64>(oid8));
            binaryCommand.Parameters.Add(
                new BlueTuskParameter<BlueTuskObjectIdentifier64[]>(oid8Values));
            binaryCommand.Parameters.Add(new BlueTuskParameter<BlueTuskRegDatabase>(database));
            binaryCommand.Parameters.Add(new BlueTuskParameter<BlueTuskRegDatabase[]>(databases));
            await using var binaryReader =
                await binaryCommand.ExecuteReaderAsync(CancellationToken.None);

            Assert.True(await binaryReader.ReadAsync(CancellationToken.None));
            Assert.Equal(oid8, binaryReader.GetFieldValue<BlueTuskObjectIdentifier64>(0));
            Assert.Equal(
                oid8Values,
                binaryReader.GetFieldValue<BlueTuskObjectIdentifier64[]>(1));
            Assert.True(binaryReader.GetFieldValue<BlueTuskRegDatabase>(2).Identifier.Oid > 0);
            Assert.All(
                binaryReader.GetFieldValue<BlueTuskRegDatabase[]>(3),
                value => Assert.True(value.Identifier.Oid > 0));
        }

        await using var textCommand = dataSource.CreateCommand(
            "SELECT '18446744073709551615'::oid8, 'template1'::regdatabase");
        await using var textReader = await textCommand.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await textReader.ReadAsync(CancellationToken.None));
        Assert.Equal(oid8, textReader.GetFieldValue<BlueTuskObjectIdentifier64>(0));
        Assert.Equal(
            "template1",
            textReader.GetFieldValue<BlueTuskRegDatabase>(1).Identifier.Name);
    }

    [Fact]
    public async Task Object_identifier_aliases_and_catalogue_vectors_round_trip()
    {
        var relation = new BlueTuskRegClass("pg_type");
        var dataType = new BlueTuskRegType("integer");
        var binaryAddition = new BlueTuskRegOperator("+(integer,integer)");
        BlueTuskRegClass[] relations =
        [
            new BlueTuskRegClass("pg_type"),
            new BlueTuskRegClass("pg_proc"),
        ];
        var int2Vector = new BlueTuskInt16Vector([1, 2, -3]);
        var oidVector = new BlueTuskObjectIdentifierVector([0, uint.MaxValue, 23]);
        var emptyInt2Vector = new BlueTuskInt16Vector([]);
        var emptyOidVector = new BlueTuskObjectIdentifierVector([]);
        BlueTuskInt16Vector[] int2Vectors = [int2Vector, emptyInt2Vector];
        BlueTuskObjectIdentifierVector[] oidVectors = [oidVector];

        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using (var aliasCommand = dataSource.CreateCommand(
            "SELECT $1::regclass, $2::regtype, $3::regoperator, $4::regclass[]"))
        {
            aliasCommand.Parameters.Add(new BlueTuskParameter<BlueTuskRegClass>(relation));
            aliasCommand.Parameters.Add(new BlueTuskParameter<BlueTuskRegType>(dataType));
            aliasCommand.Parameters.Add(
                new BlueTuskParameter<BlueTuskRegOperator>(binaryAddition));
            aliasCommand.Parameters.Add(new BlueTuskParameter<BlueTuskRegClass[]>(relations));
            await using var aliasReader =
                await aliasCommand.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await aliasReader.ReadAsync(CancellationToken.None));
            Assert.Equal(
                1247U,
                aliasReader.GetFieldValue<BlueTuskRegClass>(0).Identifier.Oid);
            Assert.Equal(
                23U,
                aliasReader.GetFieldValue<BlueTuskRegType>(1).Identifier.Oid);
            Assert.True(
                aliasReader.GetFieldValue<BlueTuskRegOperator>(2).Identifier.Oid > 0);
            var decodedRelations = aliasReader.GetFieldValue<BlueTuskRegClass[]>(3);
            Assert.Equal(1247U, decodedRelations[0].Identifier.Oid);
            Assert.Equal(1255U, decodedRelations[1].Identifier.Oid);
        }

        await using (var vectorCommand = dataSource.CreateCommand(
            "SELECT $1::int2vector, $2::oidvector, $3::int2vector, $4::oidvector"))
        {
            vectorCommand.Parameters.Add(new BlueTuskParameter<BlueTuskInt16Vector>(int2Vector));
            vectorCommand.Parameters.Add(
                new BlueTuskParameter<BlueTuskObjectIdentifierVector>(oidVector));
            vectorCommand.Parameters.Add(
                new BlueTuskParameter<BlueTuskInt16Vector>(emptyInt2Vector));
            vectorCommand.Parameters.Add(
                new BlueTuskParameter<BlueTuskObjectIdentifierVector>(emptyOidVector));
            await using var vectorReader =
                await vectorCommand.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await vectorReader.ReadAsync(CancellationToken.None));
            Assert.Equal(
                int2Vector,
                vectorReader.GetFieldValue<BlueTuskInt16Vector>(0));
            Assert.Equal(
                oidVector,
                vectorReader.GetFieldValue<BlueTuskObjectIdentifierVector>(1));
            Assert.Equal(
                emptyInt2Vector,
                vectorReader.GetFieldValue<BlueTuskInt16Vector>(2));
            Assert.Equal(
                emptyOidVector,
                vectorReader.GetFieldValue<BlueTuskObjectIdentifierVector>(3));
        }

        await using (var vectorArrayCommand = dataSource.CreateCommand(
            "SELECT $1::int2vector[], $2::oidvector[]"))
        {
            vectorArrayCommand.Parameters.Add(
                new BlueTuskParameter<BlueTuskInt16Vector[]>(int2Vectors));
            vectorArrayCommand.Parameters.Add(
                new BlueTuskParameter<BlueTuskObjectIdentifierVector[]>(oidVectors));
            await using var vectorArrayReader =
                await vectorArrayCommand.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await vectorArrayReader.ReadAsync(CancellationToken.None));
            Assert.Equal(
                int2Vectors,
                vectorArrayReader.GetFieldValue<BlueTuskInt16Vector[]>(0));
            Assert.Equal(
                oidVectors,
                vectorArrayReader.GetFieldValue<BlueTuskObjectIdentifierVector[]>(1));
        }

        await using var textCommand = dataSource.CreateCommand(
            "SELECT 'pg_type'::regclass, 'integer'::regtype, " +
            "'1 2 -3'::int2vector, '0 4294967295 23'::oidvector");
        await using var textReader = await textCommand.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await textReader.ReadAsync(CancellationToken.None));
        Assert.Equal(
            "pg_type",
            textReader.GetFieldValue<BlueTuskRegClass>(0).Identifier.Name);
        Assert.Equal(
            "integer",
            textReader.GetFieldValue<BlueTuskRegType>(1).Identifier.Name);
        Assert.Equal(int2Vector, textReader.GetFieldValue<BlueTuskInt16Vector>(2));
        Assert.Equal(
            oidVector,
            textReader.GetFieldValue<BlueTuskObjectIdentifierVector>(3));
    }

    [Fact]
    public async Task Catalogue_text_types_round_trip_and_node_tree_decodes()
    {
        var tableName = $"bluetusk_node_tree_{Guid.NewGuid():N}";
        var zero = new BlueTuskInternalChar(0);
        var ascii = new BlueTuskInternalChar((byte)'A');
        var high = new BlueTuskInternalChar(byte.MaxValue);
        BlueTuskInternalChar[] characters = [zero, ascii, high];
        var cursor = new BlueTuskRefCursor("portal 🐘");
        BlueTuskRefCursor[] cursors = [cursor, new BlueTuskRefCursor(string.Empty)];
        var path = new BlueTuskJsonPath("$.answer");
        var normalizedPath = new BlueTuskJsonPath("$.\"answer\"");
        BlueTuskJsonPath[] paths = [path];
        BlueTuskJsonPath[] normalizedPaths = [normalizedPath];
        await using var administration = BlueTuskDataSource.Create(GetConnectionString());
        try
        {
            await using (var create = administration.CreateCommand(
                $"CREATE TABLE public.{tableName} (value int DEFAULT 42)"))
            {
                await create.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
            await using (var command = dataSource.CreateCommand(
                "SELECT $1::\"char\", $2::\"char\", $3::\"char\", $4::\"char\"[], " +
                "$5::refcursor, $6::refcursor[], $7::jsonpath, $8::jsonpath[]"))
            {
                command.Parameters.Add(new BlueTuskParameter<BlueTuskInternalChar>(zero));
                command.Parameters.Add(new BlueTuskParameter<BlueTuskInternalChar>(ascii));
                command.Parameters.Add(new BlueTuskParameter<BlueTuskInternalChar>(high));
                command.Parameters.Add(
                    new BlueTuskParameter<BlueTuskInternalChar[]>(characters));
                command.Parameters.Add(new BlueTuskParameter<BlueTuskRefCursor>(cursor));
                command.Parameters.Add(new BlueTuskParameter<BlueTuskRefCursor[]>(cursors));
                command.Parameters.Add(new BlueTuskParameter<BlueTuskJsonPath>(path));
                command.Parameters.Add(new BlueTuskParameter<BlueTuskJsonPath[]>(paths));

                await using var reader =
                    await command.ExecuteReaderAsync(CancellationToken.None);
                Assert.True(await reader.ReadAsync(CancellationToken.None));
                Assert.Equal(zero, reader.GetFieldValue<BlueTuskInternalChar>(0));
                Assert.Equal(ascii, reader.GetFieldValue<BlueTuskInternalChar>(1));
                Assert.Equal(high, reader.GetFieldValue<BlueTuskInternalChar>(2));
                Assert.Equal(
                    characters,
                    reader.GetFieldValue<BlueTuskInternalChar[]>(3));
                Assert.Equal(cursor, reader.GetFieldValue<BlueTuskRefCursor>(4));
                Assert.Equal(cursors, reader.GetFieldValue<BlueTuskRefCursor[]>(5));
                Assert.Equal(normalizedPath, reader.GetFieldValue<BlueTuskJsonPath>(6));
                Assert.Equal(
                    normalizedPaths,
                    reader.GetFieldValue<BlueTuskJsonPath[]>(7));
            }

            await using (var nodeCommand = dataSource.CreateCommand(
                "SELECT adbin FROM pg_attrdef WHERE adrelid = $1::regclass"))
            {
                nodeCommand.Parameters.Add(
                    new BlueTuskParameter<BlueTuskRegClass>(
                        new BlueTuskRegClass($"public.{tableName}")));
                await using var nodeReader =
                    await nodeCommand.ExecuteReaderAsync(CancellationToken.None);
                Assert.True(await nodeReader.ReadAsync(CancellationToken.None));
                var node = nodeReader.GetFieldValue<BlueTuskNodeTree>(0);
                Assert.Contains("CONST", node.Value, StringComparison.Ordinal);
                Assert.Contains(":consttype 23", node.Value, StringComparison.Ordinal);
            }

            await using var textCommand = dataSource.CreateCommand(
                "SELECT ''::\"char\", '\\377'::\"char\", " +
                "'portal'::refcursor, '$.answer'::jsonpath");
            await using var textReader =
                await textCommand.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await textReader.ReadAsync(CancellationToken.None));
            Assert.Equal(zero, textReader.GetFieldValue<BlueTuskInternalChar>(0));
            Assert.Equal(high, textReader.GetFieldValue<BlueTuskInternalChar>(1));
            Assert.Equal(
                new BlueTuskRefCursor("portal"),
                textReader.GetFieldValue<BlueTuskRefCursor>(2));
            Assert.Equal(
                normalizedPath,
                textReader.GetFieldValue<BlueTuskJsonPath>(3));
        }
        finally
        {
            await using var drop = administration.CreateCommand(
                $"DROP TABLE IF EXISTS public.{tableName}");
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Network_scalar_parameters_round_trip_in_binary()
    {
        var inet = BlueTuskNetworkAddress.Parse("192.168.1.5/24");
        var cidr = BlueTuskNetworkAddress.Parse("2001:db8::/32", isCidr: true);
        var macaddr = BlueTuskMacAddress.Parse("08:00:2b:01:02:03");
        var macaddr8 = BlueTuskMacAddress8.Parse("08:00:2b:01:02:03:04:05");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand(
            "SELECT $1::inet, $2::cidr, $3::macaddr, $4::macaddr8");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskNetworkAddress>(inet));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskNetworkAddress>(cidr));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskMacAddress>(macaddr));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskMacAddress8>(macaddr8));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(inet, reader.GetFieldValue<BlueTuskNetworkAddress>(0));
        Assert.Equal(cidr, reader.GetFieldValue<BlueTuskNetworkAddress>(1));
        Assert.Equal(macaddr, reader.GetFieldValue<BlueTuskMacAddress>(2));
        Assert.Equal(macaddr8, reader.GetFieldValue<BlueTuskMacAddress8>(3));
    }

    [Fact]
    public async Task Geometric_scalar_parameters_round_trip_in_binary()
    {
        var first = new BlueTuskPoint(1.5, -2.25);
        var second = new BlueTuskPoint(3, 4);
        var third = new BlueTuskPoint(-5.5, 6.75);
        var point = first;
        var line = new BlueTuskLine(1, 2, 3);
        var lineSegment = new BlueTuskLineSegment(first, second);
        var box = new BlueTuskBox(first, second);
        var path = new BlueTuskPath(new[] { first, second, third }, isClosed: false);
        var polygon = new BlueTuskPolygon(new[] { first, second, third });
        var circle = new BlueTuskCircle(first, 3.5);
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand(
            "SELECT $1::point, $2::line, $3::lseg, $4::box, $5::path, $6::polygon, $7::circle");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskPoint>(point));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskLine>(line));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskLineSegment>(lineSegment));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskBox>(box));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskPath>(path));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskPolygon>(polygon));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskCircle>(circle));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(point, reader.GetFieldValue<BlueTuskPoint>(0));
        Assert.Equal(line, reader.GetFieldValue<BlueTuskLine>(1));
        Assert.Equal(lineSegment, reader.GetFieldValue<BlueTuskLineSegment>(2));
        Assert.Equal(box, reader.GetFieldValue<BlueTuskBox>(3));
        Assert.Equal(path, reader.GetFieldValue<BlueTuskPath>(4));
        Assert.Equal(polygon, reader.GetFieldValue<BlueTuskPolygon>(5));
        Assert.Equal(circle, reader.GetFieldValue<BlueTuskCircle>(6));
    }

    [Fact]
    public async Task Text_search_parameters_round_trip_in_binary()
    {
        var vector = BlueTuskTextSearchVector.Parse("'cat':3 'fat':2A,4B 'rat':5");
        var query = BlueTuskTextSearchQuery.Parse("fat:AB & (rat | !cat:*)");
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using var command = dataSource.CreateCommand("SELECT $1::tsvector, $2::tsquery");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskTextSearchVector>(vector));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskTextSearchQuery>(query));

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(vector, reader.GetFieldValue<BlueTuskTextSearchVector>(0));
        Assert.Equal(query, reader.GetFieldValue<BlueTuskTextSearchQuery>(1));
    }

    [Fact]
    public async Task Data_source_discovers_caches_and_reloads_catalogue_type_metadata()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using (var connection = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            var registry = dataSource.TypeRegistry;
            Assert.True(registry.Types.Count > 100);
            Assert.True(registry.TryGetType(new BlueTuskTypeId(1007), out var array));
            Assert.Equal(BlueTuskTypeKind.Array, array!.Kind);
            Assert.Equal(new BlueTuskTypeId(23), array.ElementType);
            Assert.True(registry.TryGetType(new BlueTuskTypeId(3904), out var range));
            Assert.Equal(BlueTuskTypeKind.Range, range!.Kind);
            Assert.Equal(new BlueTuskTypeId(23), range.RangeSubtype);
            Assert.Equal(new BlueTuskTypeId(3904), range.RangeType);
            Assert.Equal(new BlueTuskTypeId(4451), range.MultirangeType);
            Assert.True(registry.TryGetType(new BlueTuskTypeId(4451), out var multirange));
            Assert.Equal(BlueTuskTypeKind.Multirange, multirange!.Kind);
            Assert.Equal(new BlueTuskTypeId(23), multirange.RangeSubtype);
            Assert.Equal(new BlueTuskTypeId(3904), multirange.RangeType);
            Assert.Equal(new BlueTuskTypeId(4451), multirange.MultirangeType);
            Assert.True(registry.TryGetCodec(range.Id, out var rangeCodec));
            Assert.IsType<BlueTuskRangeCodec<int>>(rangeCodec);
            Assert.True(registry.TryGetCodec(multirange.Id, out var multirangeCodec));
            Assert.IsType<BlueTuskMultirangeCodec<int>>(multirangeCodec);
            Assert.True(registry.TryGetType(new BlueTuskTypeId(71), out var composite));
            Assert.Equal(BlueTuskTypeKind.Composite, composite!.Kind);
            Assert.True(registry.TryGetCodec(new BlueTuskTypeId(23), out var codec));
            Assert.IsType<BlueTuskInt32Codec>(codec);
        }

        var cached = dataSource.TypeRegistry;
        await using (var secondConnection = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            Assert.Same(cached, dataSource.TypeRegistry);
        }

        await dataSource.ReloadTypesAsync(CancellationToken.None);
        Assert.NotSame(cached, dataSource.TypeRegistry);
        Assert.True(dataSource.TypeRegistry.Types.Count > 100);
        Assert.True(dataSource.TypeRegistry.TryGetType(new BlueTuskTypeId(23), out _));
    }

    [Fact]
    public async Task Money_uses_discovered_locale_scale_for_text_and_binary_round_trips()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        await using (var connection = await dataSource.OpenConnectionAsync(CancellationToken.None))
        {
            Assert.True(dataSource.TypeRegistry.TryGetCodec(BlueTuskBuiltInTypes.Money.Id, out var registered));
            var codec = Assert.IsType<BlueTuskMoneyCodec>(registered);
            Assert.Equal(2, codec.FractionalDigits);
        }

        var value = new BlueTuskMoney(123_456, 2);
        await using (var binaryCommand = dataSource.CreateCommand("SELECT $1::money"))
        {
            binaryCommand.Parameters.Add(new BlueTuskParameter<BlueTuskMoney>(value));
            await using var reader = await binaryCommand.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(value, reader.GetFieldValue<BlueTuskMoney>(0));
        }

        await using (var textCommand = dataSource.CreateCommand("SELECT 1234.56::money"))
        await using (var reader = await textCommand.ExecuteReaderAsync(CancellationToken.None))
        {
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(value, reader.GetFieldValue<BlueTuskMoney>(0));
        }
    }

    [Fact]
    public async Task Catalogue_driven_arrays_round_trip_shape_nulls_and_lower_bounds()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        var integers = new[] { int.MinValue, 0, int.MaxValue };
        string?[] text = ["a,b", null, "NULL", "snow 🐘"];
        var matrix = new int[,]
        {
            { 1, 2 },
            { 3, 4 },
        };

        await using (var binaryCommand = dataSource.CreateCommand(
            "SELECT $1::int4[], $2::text[], $3::int4[]"))
        {
            binaryCommand.Parameters.Add(new BlueTuskParameter<int[]>(integers));
            binaryCommand.Parameters.Add(
                new BlueTuskParameter<string?[]>(text) { PostgreSqlTypeOid = 1009 });
            binaryCommand.Parameters.Add(new BlueTuskParameter<int[,]>(matrix));
            await using var reader = await binaryCommand.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(integers, reader.GetFieldValue<int[]>(0));
            Assert.Equal(text, reader.GetFieldValue<string?[]>(1));
            Assert.Equal(matrix.Cast<int>(), reader.GetFieldValue<int[,]>(2).Cast<int>());
        }

        await using (var textCommand = dataSource.CreateCommand(
            "SELECT '{{1,2},{3,4}}'::int4[], '[0:1]={5,6}'::int4[], " +
            "'{\"a,b\",NULL,\"NULL\",\"snow 🐘\"}'::text[]"))
        await using (var reader = await textCommand.ExecuteReaderAsync(CancellationToken.None))
        {
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            var textMatrix = reader.GetFieldValue<int[,]>(0);
            Assert.Equal(1, textMatrix[0, 0]);
            Assert.Equal(2, textMatrix[0, 1]);
            Assert.Equal(3, textMatrix[1, 0]);
            Assert.Equal(4, textMatrix[1, 1]);

            var bounded = reader.GetFieldValue<Array>(1);
            Assert.Equal(-1, bounded.GetLowerBound(0));
            Assert.Equal(5, bounded.GetValue(-1));
            Assert.Equal(6, bounded.GetValue(0));
            Assert.Equal(text, reader.GetFieldValue<string?[]>(2));
        }
    }

    [Fact]
    public async Task Catalogue_driven_ranges_multiranges_and_arrays_round_trip()
    {
        await using var dataSource = BlueTuskDataSource.Create(GetConnectionString());
        var range = new BlueTuskRange<int>(1, 5);
        var secondRange = new BlueTuskRange<int>(10, 20);
        var multirange = new BlueTuskMultirange<int>([range, secondRange]);
        BlueTuskRange<int>[] ranges = [range, BlueTuskRange.Empty<int>()];
        BlueTuskMultirange<int>[] multiranges = [multirange, BlueTuskMultirange.Empty<int>()];
        await using var command = dataSource.CreateCommand(
            "SELECT $1::int4range, $2::int4multirange, " +
            "$3::int4range[], $4::int4multirange[]");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskRange<int>>(range));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskMultirange<int>>(multirange));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskRange<int>[]>(ranges));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskMultirange<int>[]>(multiranges));

        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(range, reader.GetFieldValue<BlueTuskRange<int>>(0));
            Assert.Equal(multirange, reader.GetFieldValue<BlueTuskMultirange<int>>(1));
            Assert.Equal(ranges, reader.GetFieldValue<BlueTuskRange<int>[]>(2));
            Assert.Equal(multiranges, reader.GetFieldValue<BlueTuskMultirange<int>[]>(3));
        }

        await using var literalCommand = dataSource.CreateCommand(
            "SELECT '(,5]'::int4range, '{[1,5),[10,20)}'::int4multirange");
        await using var literalReader = await literalCommand.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await literalReader.ReadAsync(CancellationToken.None));
        Assert.Equal(
            new BlueTuskRange<int>(
                BlueTuskRangeBound.Unbounded<int>(),
                BlueTuskRangeBound.Exclusive(6)),
            literalReader.GetFieldValue<BlueTuskRange<int>>(0));
        Assert.Equal(multirange, literalReader.GetFieldValue<BlueTuskMultirange<int>>(1));
    }

    [Fact]
    public async Task Catalogue_driven_enums_domains_and_enum_arrays_round_trip()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var enumName = $"bluetusk_order_status_{suffix}";
        var domainName = $"bluetusk_positive_integer_{suffix}";
        await using var administration = BlueTuskDataSource.Create(GetConnectionString());
        try
        {
            await using (var create = administration.CreateCommand(
                $"CREATE TYPE public.{enumName} AS ENUM ('pending', 'in-progress', 'Complete'); " +
                $"CREATE DOMAIN public.{domainName} AS int4 CHECK (VALUE > 0)"))
            {
                await create.ExecuteNonQueryAsync(CancellationToken.None);
            }

            var builder = new BlueTuskDataSourceBuilder(GetConnectionString());
            builder.MapEnum<IntegrationOrderStatus>($"public.{enumName}");
            await using (var dataSource = builder.Build())
            {
                await using (var connection = await dataSource.OpenConnectionAsync(CancellationToken.None))
                {
                    Assert.NotNull(connection);
                }

                var enumType = Assert.Single(
                    dataSource.TypeRegistry.Types,
                    type => type.Schema == "public" && type.Name == enumName);
                Assert.Collection(
                    enumType.EnumLabels,
                    label => Assert.Equal("pending", label),
                    label => Assert.Equal("in-progress", label),
                    label => Assert.Equal("Complete", label));
                var domainType = Assert.Single(
                    dataSource.TypeRegistry.Types,
                    type => type.Schema == "public" && type.Name == domainName);
                Assert.Equal(BlueTuskTypeKind.Domain, domainType.Kind);
                Assert.Equal(BlueTuskBuiltInTypes.Int4.Id, domainType.BaseType);
                Assert.True(dataSource.TypeRegistry.TryGetCodec(domainType.Id, out var registeredDomain));
                Assert.IsType<BlueTuskDomainCodec>(registeredDomain);

                var statuses = new[] { IntegrationOrderStatus.Pending, IntegrationOrderStatus.Complete };
                await using (var binaryCommand = dataSource.CreateCommand(
                    $"SELECT $1::public.{enumName}, $2::public.{enumName}[], $3::public.{domainName}"))
                {
                    binaryCommand.Parameters.Add(
                        new BlueTuskParameter<IntegrationOrderStatus>(IntegrationOrderStatus.InProgress));
                    binaryCommand.Parameters.Add(new BlueTuskParameter<IntegrationOrderStatus[]>(statuses));
                    binaryCommand.Parameters.Add(
                        new BlueTuskParameter<int>(42) { PostgreSqlTypeOid = domainType.Id.Oid });
                    await using var reader = await binaryCommand.ExecuteReaderAsync(CancellationToken.None);
                    Assert.True(await reader.ReadAsync(CancellationToken.None));
                    Assert.Equal(IntegrationOrderStatus.InProgress, reader.GetFieldValue<IntegrationOrderStatus>(0));
                    Assert.Equal(statuses, reader.GetFieldValue<IntegrationOrderStatus[]>(1));
                    Assert.Equal(42, reader.GetInt32(2));
                }

                await using var textCommand = dataSource.CreateCommand(
                    $"SELECT 'pending'::public.{enumName}, " +
                    $"ARRAY['in-progress', 'Complete']::public.{enumName}[]");
                await using var textReader = await textCommand.ExecuteReaderAsync(CancellationToken.None);
                Assert.True(await textReader.ReadAsync(CancellationToken.None));
                Assert.Equal(IntegrationOrderStatus.Pending, textReader.GetFieldValue<IntegrationOrderStatus>(0));
                var textStatuses = textReader.GetFieldValue<IntegrationOrderStatus[]>(1);
                Assert.Equal(IntegrationOrderStatus.InProgress, textStatuses[0]);
                Assert.Equal(IntegrationOrderStatus.Complete, textStatuses[1]);
            }
        }
        finally
        {
            await using var drop = administration.CreateCommand(
                $"DROP DOMAIN IF EXISTS public.{domainName}; DROP TYPE IF EXISTS public.{enumName}");
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Catalogue_driven_composites_records_and_composite_arrays_round_trip()
    {
        var typeName = $"bluetusk_address_{Guid.NewGuid():N}";
        await using var administration = BlueTuskDataSource.Create(GetConnectionString());
        try
        {
            await using (var create = administration.CreateCommand(
                $"CREATE TYPE public.{typeName} AS " +
                "(house_number int4, street text, note text)"))
            {
                await create.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await using (var dataSource = BlueTuskDataSource.Create(GetConnectionString()))
            {
                await using (var connection = await dataSource.OpenConnectionAsync(CancellationToken.None))
                {
                    Assert.NotNull(connection);
                }

                var compositeType = Assert.Single(
                    dataSource.TypeRegistry.Types,
                    type => type.Schema == "public" && type.Name == typeName);
                Assert.Collection(
                    compositeType.CompositeFields,
                    field => Assert.Equal("house_number", field.Name),
                    field => Assert.Equal("street", field.Name),
                    field => Assert.Equal("note", field.Name));
                Assert.True(dataSource.TypeRegistry.TryGetCodec(compositeType.Id, out var registered));
                Assert.IsType<BlueTuskRecordCodec>(registered);

                var value = new BlueTuskRecord(
                [
                    new BlueTuskRecordField("house_number", BlueTuskBuiltInTypes.Int4, 42),
                    new BlueTuskRecordField("street", BlueTuskBuiltInTypes.Text, "Main, Road"),
                    new BlueTuskRecordField("note", BlueTuskBuiltInTypes.Text, null),
                ]);
                await using (var binaryCommand = dataSource.CreateCommand(
                    $"SELECT $1::public.{typeName}, ARRAY[$1::public.{typeName}], " +
                    "ROW(7::int4, 'anonymous'::text, NULL::uuid)"))
                {
                    binaryCommand.Parameters.Add(
                        new BlueTuskParameter<BlueTuskRecord>(value)
                        {
                            PostgreSqlTypeOid = compositeType.Id.Oid,
                        });
                    await using var reader = await binaryCommand.ExecuteReaderAsync(CancellationToken.None);
                    Assert.True(await reader.ReadAsync(CancellationToken.None));
                    AssertAddress(value, reader.GetFieldValue<BlueTuskRecord>(0));
                    var array = reader.GetFieldValue<BlueTuskRecord[]>(1);
                    Assert.Single(array);
                    AssertAddress(value, array[0]);

                    var anonymous = reader.GetFieldValue<BlueTuskRecord>(2);
                    Assert.Equal(7, anonymous[0].Value);
                    Assert.Equal("int4", anonymous[0].Type!.Name);
                    Assert.Equal("anonymous", anonymous[1].Value);
                    Assert.Equal("text", anonymous[1].Type!.Name);
                    Assert.Null(anonymous[2].Value);
                    Assert.Equal("uuid", anonymous[2].Type!.Name);
                }

                await using var textCommand = dataSource.CreateCommand(
                    $"SELECT '(42,\"Main, Road\",)'::public.{typeName}");
                await using var textReader = await textCommand.ExecuteReaderAsync(CancellationToken.None);
                Assert.True(await textReader.ReadAsync(CancellationToken.None));
                AssertAddress(value, textReader.GetFieldValue<BlueTuskRecord>(0));
            }

            var mappedBuilder = new BlueTuskDataSourceBuilder(GetConnectionString());
            mappedBuilder.MapComposite<IntegrationAddress>($"public.{typeName}");
            await using (var mappedDataSource = mappedBuilder.Build())
            {
                await using (var connection = await mappedDataSource.OpenConnectionAsync(CancellationToken.None))
                {
                    Assert.NotNull(connection);
                }

                var mappedType = Assert.Single(
                    mappedDataSource.TypeRegistry.Types,
                    type => type.Schema == "public" && type.Name == typeName);
                Assert.True(mappedDataSource.TypeRegistry.TryGetCodec(mappedType.Id, out var mappedCodec));
                Assert.IsType<BlueTuskCompositeCodec<IntegrationAddress>>(mappedCodec);

                var value = new IntegrationAddress(88, "Baker, Street", null);
                IntegrationAddress[] values =
                [
                    value,
                    new IntegrationAddress(7, "Side Road", "rear entrance"),
                ];
                await using (var binaryCommand = mappedDataSource.CreateCommand(
                    $"SELECT $1::public.{typeName}, $2::public.{typeName}[]"))
                {
                    binaryCommand.Parameters.Add(new BlueTuskParameter<IntegrationAddress>(value));
                    binaryCommand.Parameters.Add(new BlueTuskParameter<IntegrationAddress[]>(values));
                    await using var reader = await binaryCommand.ExecuteReaderAsync(CancellationToken.None);
                    Assert.True(await reader.ReadAsync(CancellationToken.None));
                    Assert.Equal(value, reader.GetFieldValue<IntegrationAddress>(0));
                    Assert.Equal(values, reader.GetFieldValue<IntegrationAddress[]>(1));
                }

                await using var literalCommand = mappedDataSource.CreateCommand(
                    $"SELECT '(5,\"River, Lane\",)'::public.{typeName}");
                await using var literalReader = await literalCommand.ExecuteReaderAsync(CancellationToken.None);
                Assert.True(await literalReader.ReadAsync(CancellationToken.None));
                Assert.Equal(
                    new IntegrationAddress(5, "River, Lane", null),
                    literalReader.GetFieldValue<IntegrationAddress>(0));
            }
        }
        finally
        {
            await using var drop = administration.CreateCommand($"DROP TYPE IF EXISTS public.{typeName}");
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Data_source_builder_binds_runtime_codec_by_catalogue_name()
    {
        var builder = new BlueTuskDataSourceBuilder(GetConnectionString());
        builder.Types.Register("pg_catalog", "jsonpath", new TestJsonPathCodec());
        await using var dataSource = builder.Build();
        var value = new TestJsonPath("$.answer");
        var normalizedValue = new TestJsonPath("$.\"answer\"");

        await using (var binaryCommand = dataSource.CreateCommand("SELECT $1::jsonpath"))
        {
            binaryCommand.Parameters.Add(new BlueTuskParameter<TestJsonPath>(value));
            await using var reader = await binaryCommand.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(normalizedValue, reader.GetFieldValue<TestJsonPath>(0));
        }

        await using (var textCommand = dataSource.CreateCommand("SELECT '$.answer'::jsonpath"))
        await using (var reader = await textCommand.ExecuteReaderAsync(CancellationToken.None))
        {
            Assert.True(await reader.ReadAsync(CancellationToken.None));
            Assert.Equal(normalizedValue, reader.GetFieldValue<TestJsonPath>(0));
            Assert.Equal("jsonpath", reader.GetDataTypeName(0));
        }

        Assert.True(dataSource.TypeRegistry.TryGetType(typeof(TestJsonPath), out var type, out var codec));
        Assert.Equal(4072U, type!.Id.Oid);
        Assert.IsType<TestJsonPathCodec>(codec);
    }

    private readonly record struct TestJsonPath(string Value);

    private enum IntegrationOrderStatus
    {
        [BlueTuskName("pending")]
        Pending,

        [BlueTuskName("in-progress")]
        InProgress,

        Complete,
    }

    private sealed record IntegrationAddress(int HouseNumber, string Street, string? Note);

    private sealed class TestJsonPathCodec : BlueTuskCodec<TestJsonPath>
    {
        public override TestJsonPath ReadTyped(
            ref BlueTuskReader reader,
            BlueTuskDataFormat format,
            BlueTuskTypeDescriptor type)
        {
            if (format == BlueTuskDataFormat.Binary && reader.ReadByte() != 1)
            {
                throw new InvalidOperationException("PostgreSQL jsonpath binary version is not supported.");
            }

            return new TestJsonPath(reader.ReadRemainingUtf8());
        }

        public override void WriteTyped(
            ref BlueTuskWriter writer,
            TestJsonPath value,
            BlueTuskDataFormat format,
            BlueTuskTypeDescriptor type)
        {
            if (format == BlueTuskDataFormat.Binary)
            {
                writer.WriteByte(1);
            }

            writer.WriteUtf8(value.Value);
        }
    }

    private static BlueTuskCommand CreateAclCommand(
        BlueTuskConnection connection,
        BlueTuskTransaction? transaction,
        BlueTuskAccessControlItem value)
    {
        var command = new BlueTuskCommand(
            "SELECT $1::aclitem, ARRAY[$1::aclitem]::aclitem[], $2::aclitem[]",
            connection)
        {
            Transaction = transaction,
        };
        command.Parameters.Add(new BlueTuskParameter<BlueTuskAccessControlItem>(value));
        command.Parameters.Add(
            new BlueTuskParameter<BlueTuskAccessControlItem[]>(
                Array.Empty<BlueTuskAccessControlItem>()));
        return command;
    }

    private static void AssertOpaqueBinaryValue(BlueTuskOpaqueCatalogueValue value)
    {
        Assert.Equal(BlueTuskDataFormat.Binary, value.Format);
        Assert.False(value.Data.IsEmpty);
    }

    private static void AssertAddress(BlueTuskRecord expected, BlueTuskRecord actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(expected[index].Value, actual[index].Value);
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SkipException.ForSkip("BLUETUSK_TEST_CONNECTION_STRING is not configured.");
        }

        var settings = new BlueTuskConnectionStringBuilder(connectionString)
        {
            SslMode = BlueTuskSslMode.Disable,
            ChannelBinding = BlueTuskChannelBindingMode.Disable,
        };
        return settings.ConnectionString;
    }
}
