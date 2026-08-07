using BlueTusk.Client;
using BlueTusk.Data;
using BlueTusk.Diagnostics;
using BlueTusk.Protocol;
using BlueTusk.Security;
using BlueTusk.Transport;
using BlueTusk.TypeSystem;

RunSmoke();
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
RunSmoke();
var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
Console.WriteLine(
    $"BLUETUSK_PROVIDER_CORE_SMOKE_OK allocatedBytes={allocatedBytes}");

static void RunSmoke()
{
    var endpoint = new BlueTuskEndpoint.Tcp("localhost", 5432);
    if (endpoint.Port != 5432 ||
        BlueTuskProtocolVersion.Version30.ToWireValue() != 196608)
    {
        throw new InvalidOperationException("Transport or protocol smoke failed.");
    }

    using (var scram = new BlueTuskScramSha256Client(
                   "smoke",
                   "secret",
                   "fixed-client-nonce"))
    {
        if (!scram.ClientFirstMessage.Contains(
                "fixed-client-nonce",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Security smoke failed.");
        }
    }

    var clientOptions = BlueTuskClientOptions.FromConnectionString(
        "Host=localhost;Database=smoke;Username=smoke;Password=secret;" +
        "Pooling=false;SSL Mode=Disable");
    if (clientOptions.Database != "smoke")
    {
        throw new InvalidOperationException("Client options smoke failed.");
    }

    var diagnostics = new BlueTuskDiagnosticsOptions
    {
        SlowCommandThreshold = TimeSpan.FromSeconds(1),
    };
    using var dataSource = new BlueTuskDataSourceBuilder(
            "Host=localhost;Database=smoke;Username=smoke;Password=secret;" +
            "Pooling=false;SSL Mode=Disable")
        .ConfigureDiagnostics(diagnostics)
        .Build();
    using var connection = dataSource.CreateConnection();
    using var command = dataSource.CreateCommand("SELECT 1");
    if (connection.Database != "smoke" ||
        command.CommandText != "SELECT 1" ||
        BlueTuskDiagnostics.Meter.Name != BlueTuskDiagnostics.InstrumentationName)
    {
        throw new InvalidOperationException("Data or diagnostics smoke failed.");
    }

    VerifyBuiltInArrayAndRange();
    VerifyComposite(
        SmokeAddress.RegisterCodec(new BlueTuskTypeRegistryBuilder()).Build(),
        new SmokeAddress(42, "Main Street"));
    VerifyComposite(
        new BlueTuskTypeRegistryBuilder()
            .Register(
                "app",
                "smoke_address",
                new BlueTuskCompositeCodec<ReflectionAddress>())
            .Build(),
        new ReflectionAddress(42, "Main Street"));
}

static void VerifyBuiltInArrayAndRange()
{
    var rangeId = new BlueTuskTypeId(91_410);
    var registry = BlueTuskTypeCatalogue.BuildRegistry(
    [
        new BlueTuskCatalogueType
        {
            Id = BlueTuskBuiltInTypes.Int4.Id,
            Schema = "pg_catalog",
            Name = "int4",
            PostgreSqlKind = 'b',
            PostgreSqlCategory = 'N',
            ArrayType = new BlueTuskTypeId(1007),
        },
        new BlueTuskCatalogueType
        {
            Id = new BlueTuskTypeId(1007),
            Schema = "pg_catalog",
            Name = "_int4",
            PostgreSqlKind = 'b',
            PostgreSqlCategory = 'A',
            ElementType = BlueTuskBuiltInTypes.Int4.Id,
        },
        new BlueTuskCatalogueType
        {
            Id = rangeId,
            Schema = "app",
            Name = "smoke_int_range",
            PostgreSqlKind = 'r',
            PostgreSqlCategory = 'R',
            RangeSubtype = BlueTuskBuiltInTypes.Int4.Id,
            RangeType = rangeId,
        },
    ]);
    var arrayId = new BlueTuskTypeId(1007);
    if (!registry.TryGetType(arrayId, out var type) ||
        type is null ||
        !registry.TryGetCodec(arrayId, out var codec) ||
        codec is null)
    {
        throw new InvalidOperationException("The int4[] codec is unavailable.");
    }

    int[] expected = [1, 2, 3, 4];
    var buffer = new byte[256];
    var writer = new BlueTuskWriter(buffer);
    codec.Write(ref writer, expected, BlueTuskDataFormat.Binary, type);
    var reader = new BlueTuskReader(buffer.AsSpan(0, writer.WrittenCount));
    if (codec.Read(ref reader, BlueTuskDataFormat.Binary, type) is not int[] actual ||
        !expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException("The statically rooted array codec failed.");
    }

    if (!registry.TryGetType(rangeId, out var rangeType) ||
        rangeType is null ||
        !registry.TryGetCodec(rangeId, out var rangeCodec) ||
        rangeCodec is null)
    {
        throw new InvalidOperationException("The range codec is unavailable.");
    }

    var expectedRange = new BlueTuskRange<int>(1, 5);
    writer = new BlueTuskWriter(buffer);
    rangeCodec.Write(
        ref writer,
        expectedRange,
        BlueTuskDataFormat.Binary,
        rangeType);
    reader = new BlueTuskReader(buffer.AsSpan(0, writer.WrittenCount));
    if (rangeCodec.Read(
            ref reader,
            BlueTuskDataFormat.Binary,
            rangeType) is not BlueTuskRange<int> actualRange ||
        actualRange != expectedRange)
    {
        throw new InvalidOperationException("The statically rooted range codec failed.");
    }
}

static void VerifyComposite<T>(BlueTuskTypeRegistry configuredTypes, T expected)
    where T : class
{
    var registry = BlueTuskTypeCatalogue.BuildRegistry(
        CreateCompositeCatalogue(),
        configuredTypes);
    var typeId = new BlueTuskTypeId(91_400);
    if (!registry.TryGetType(typeId, out var type) ||
        type is null ||
        !registry.TryGetCodec(typeId, out var codec) ||
        codec is null)
    {
        throw new InvalidOperationException("The composite codec is unavailable.");
    }

    var buffer = new byte[512];
    var writer = new BlueTuskWriter(buffer);
    codec.Write(ref writer, expected, BlueTuskDataFormat.Binary, type);
    var reader = new BlueTuskReader(buffer.AsSpan(0, writer.WrittenCount));
    var actual = codec.Read(ref reader, BlueTuskDataFormat.Binary, type);
    if (actual is not T)
    {
        throw new InvalidOperationException("The composite codec failed.");
    }
}

static BlueTuskCatalogueType[] CreateCompositeCatalogue() =>
[
    new BlueTuskCatalogueType
    {
        Id = new BlueTuskTypeId(91_400),
        Schema = "app",
        Name = "smoke_address",
        PostgreSqlKind = 'c',
        PostgreSqlCategory = 'C',
        ArrayType = new BlueTuskTypeId(91_401),
        CompositeFields =
        [
            new BlueTuskCompositeField
            {
                Position = 1,
                Name = "house_number",
                Type = BlueTuskBuiltInTypes.Int4.Id,
            },
            new BlueTuskCompositeField
            {
                Position = 2,
                Name = "street",
                Type = BlueTuskBuiltInTypes.Text.Id,
            },
        ],
    },
    new BlueTuskCatalogueType
    {
        Id = new BlueTuskTypeId(91_401),
        Schema = "app",
        Name = "_smoke_address",
        PostgreSqlKind = 'b',
        PostgreSqlCategory = 'A',
        ElementType = new BlueTuskTypeId(91_400),
    },
];

[BlueTuskComposite("app", "smoke_address")]
internal sealed partial record SmokeAddress(int HouseNumber, string Street);

internal sealed record ReflectionAddress(int HouseNumber, string Street);
