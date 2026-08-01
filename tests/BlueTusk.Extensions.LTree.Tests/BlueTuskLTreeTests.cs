using System.Text;
using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Extensions.Testing;
using BlueTusk.TypeSystem;
using Xunit.Sdk;

namespace BlueTusk.Extensions.LTree.Tests;

public sealed class BlueTuskLTreeTests
{
    private static readonly BlueTuskTypeDescriptor LTreeType = Type("ltree", 16_386);
    private static readonly BlueTuskTypeDescriptor LQueryType = Type("lquery", 16_387);
    private static readonly BlueTuskTypeDescriptor LTxtQueryType = Type("ltxtquery", 16_388);

    [Fact]
    public void Values_preserve_server_canonical_text_and_reject_null_characters()
    {
        Assert.Equal("Top.Countries.Europe", new BlueTuskLTree("Top.Countries.Europe").Value);
        Assert.Equal("Top.*{,2}.Europe", new BlueTuskLQuery("Top.*{,2}.Europe").Value);
        Assert.Equal("Europe & !Asia", new BlueTuskLTxtQuery("Europe & !Asia").Value);
        Assert.Throws<ArgumentException>(() => new BlueTuskLTree("Top\0Europe"));
        Assert.Throws<ArgumentException>(() => new BlueTuskLQuery("Top\0*"));
        Assert.Throws<ArgumentException>(() => new BlueTuskLTxtQuery("Top\0& Europe"));
    }

    [Fact]
    public void Codecs_match_the_versioned_text_binary_layout()
    {
        AssertCodec(
            new BlueTuskLTreeCodec(),
            new BlueTuskLTree("Top.Éurope"),
            LTreeType,
            static value => value.Value);
        AssertCodec(
            new BlueTuskLQueryCodec(),
            new BlueTuskLQuery("Top.*{,2}.Éurope"),
            LQueryType,
            static value => value.Value);
        AssertCodec(
            new BlueTuskLTxtQueryCodec(),
            new BlueTuskLTxtQuery("Éurope & !Asia"),
            LTxtQueryType,
            static value => value.Value);
    }

    [Fact]
    public void Codecs_reject_missing_and_unsupported_binary_versions()
    {
        Assert.Throws<InvalidOperationException>(() => ReadLTree([]));
        Assert.Throws<InvalidOperationException>(() => ReadLTree([2, 0x54, 0x6f, 0x70]));
        Assert.Throws<InvalidOperationException>(() => ReadLTree([1, 0xff]));
        Assert.Throws<InvalidOperationException>(() => ReadLQuery([]));
        Assert.Throws<InvalidOperationException>(() => ReadLTxtQuery([2]));
    }

    [Fact]
    public void Build_carries_all_three_types_and_an_immutable_feature_snapshot()
    {
        var builder = new BlueTuskDataSourceBuilder(
            "Host=localhost;Username=test;Password=test");
        Assert.Same(builder, builder.UseLTree());

        using var dataSource = builder.Build();
        builder.Features.Register("test.late-feature", 42);

        var feature = dataSource.Features.GetRequired<BlueTuskLTreeFeature>(
            BlueTuskLTreeFeature.RegistryName);
        Assert.Equal(new BlueTuskTypeName("public", "ltree"), feature.LTreeTypeName);
        Assert.Equal(new BlueTuskTypeName("public", "lquery"), feature.LQueryTypeName);
        Assert.Equal(new BlueTuskTypeName("public", "ltxtquery"), feature.LTxtQueryTypeName);
        Assert.False(dataSource.Features.Contains("test.late-feature"));
    }

    [Fact]
    public async Task Ltree_plugin_executes_all_types_arrays_and_operator_live_paths()
    {
        var connectionString = GetConnectionString();
        await using (var administration = BlueTuskDataSource.Create(connectionString))
        await using (var create = administration.CreateCommand("CREATE EXTENSION IF NOT EXISTS ltree"))
        {
            _ = await create.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var builder = new BlueTuskDataSourceBuilder(connectionString).UseLTree();
        await using var dataSource = builder.Build();
        await VerifyTypeAsync<BlueTuskLTree, BlueTuskLTreeCodec>(
            dataSource,
            new BlueTuskTypeName("public", "ltree"));
        await VerifyTypeAsync<BlueTuskLQuery, BlueTuskLQueryCodec>(
            dataSource,
            new BlueTuskTypeName("public", "lquery"));
        await VerifyTypeAsync<BlueTuskLTxtQuery, BlueTuskLTxtQueryCodec>(
            dataSource,
            new BlueTuskTypeName("public", "ltxtquery"));

        var path = new BlueTuskLTree("Top.Countries.Europe.Russia");
        var query = new BlueTuskLQuery("Top.*{,2}.Europe.Russ@*");
        var textQuery = new BlueTuskLTxtQuery("Europe & Russia@* & !Transportation");
        BlueTuskLTree[] paths = [path, new("Top.Countries.Asia.Japan")];
        await using var command = dataSource.CreateCommand(
            "SELECT $1::ltree, $2::lquery, $3::ltxtquery, $4::ltree[], " +
            "$1 ~ $2, $1 @ $3, nlevel($1)");
        command.Parameters.Add(new BlueTuskParameter<BlueTuskLTree>(path));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskLQuery>(query));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskLTxtQuery>(textQuery));
        command.Parameters.Add(new BlueTuskParameter<BlueTuskLTree[]>(paths));
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(path, reader.GetFieldValue<BlueTuskLTree>(0));
        Assert.Equal(query, reader.GetFieldValue<BlueTuskLQuery>(1));
        Assert.Equal(textQuery, reader.GetFieldValue<BlueTuskLTxtQuery>(2));
        Assert.Equal(paths, reader.GetFieldValue<BlueTuskLTree[]>(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.Equal(4, reader.GetInt32(6));
    }

    private static void AssertCodec<T>(
        BlueTuskCodec<T> codec,
        T expected,
        BlueTuskTypeDescriptor type,
        Func<T, string> getValue)
    {
        var text = getValue(expected);
        var payload = Encoding.UTF8.GetBytes(text);
        var bytes = new byte[payload.Length + 1];
        var writer = new BlueTuskWriter(bytes);
        codec.WriteTyped(ref writer, expected, BlueTuskDataFormat.Binary, type);

        Assert.Equal(1, bytes[0]);
        Assert.Equal(payload, bytes[1..]);
        var reader = new BlueTuskReader(bytes);
        Assert.Equal(expected, codec.ReadTyped(ref reader, BlueTuskDataFormat.Binary, type));
        Assert.Equal(0, reader.Remaining);
    }

    private static async ValueTask VerifyTypeAsync<TValue, TCodec>(
        BlueTuskDataSource dataSource,
        BlueTuskTypeName typeName)
        where TCodec : IBlueTuskCodec
    {
        var compatibility = await BlueTuskExtensionCompatibility.VerifyAsync(
            dataSource,
            new BlueTuskExtensionContract
            {
                FeatureName = BlueTuskLTreeFeature.RegistryName,
                FeatureType = typeof(BlueTuskLTreeFeature),
                PostgreSqlType = typeName,
                ClrType = typeof(TValue),
                CodecType = typeof(TCodec),
            });
        Assert.Equal(typeof(TCodec), compatibility.CodecType);
    }

    private static BlueTuskLTree ReadLTree(byte[] bytes)
    {
        var reader = new BlueTuskReader(bytes);
        return new BlueTuskLTreeCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            LTreeType);
    }

    private static BlueTuskLQuery ReadLQuery(byte[] bytes)
    {
        var reader = new BlueTuskReader(bytes);
        return new BlueTuskLQueryCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            LQueryType);
    }

    private static BlueTuskLTxtQuery ReadLTxtQuery(byte[] bytes)
    {
        var reader = new BlueTuskReader(bytes);
        return new BlueTuskLTxtQueryCodec().ReadTyped(
            ref reader,
            BlueTuskDataFormat.Binary,
            LTxtQueryType);
    }

    private static BlueTuskTypeDescriptor Type(string name, uint oid) => new()
    {
        Id = new BlueTuskTypeId(oid),
        Schema = "public",
        Name = name,
        Kind = BlueTuskTypeKind.Base,
    };

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
}
