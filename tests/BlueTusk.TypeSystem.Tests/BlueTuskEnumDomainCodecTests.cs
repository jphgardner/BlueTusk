using System.Runtime.Serialization;
using System.Text;

namespace BlueTusk.TypeSystem.Tests;

public sealed class BlueTuskEnumDomainCodecTests
{
    private static readonly BlueTuskTypeDescriptor StatusType = new()
    {
        Id = new BlueTuskTypeId(90_100),
        Schema = "app",
        Name = "order_status",
        Kind = BlueTuskTypeKind.Enum,
        EnumLabels = ["pending", "in-progress", "Complete"],
    };

    [Fact]
    public void Mapped_enum_uses_attributes_and_exact_catalogue_labels()
    {
        var codec = new BlueTuskEnumCodec<OrderStatus>();

        AssertRoundTrip(codec, OrderStatus.Pending, "pending");
        AssertRoundTrip(codec, OrderStatus.InProgress, "in-progress");
        AssertRoundTrip(codec, OrderStatus.Complete, "Complete");
    }

    [Fact]
    public void Explicit_enum_labels_override_member_names()
    {
        var descriptor = StatusType with { EnumLabels = ["waiting", "working", "done"] };
        var codec = new BlueTuskEnumCodec<OrderStatus>(
            new Dictionary<OrderStatus, string>
            {
                [OrderStatus.Pending] = "waiting",
                [OrderStatus.InProgress] = "working",
                [OrderStatus.Complete] = "done",
            });

        Assert.Equal("working", Write(codec, descriptor, OrderStatus.InProgress, BlueTuskDataFormat.Binary));
        Assert.Equal(OrderStatus.Complete, Read(codec, descriptor, "done", BlueTuskDataFormat.Text));
    }

    [Fact]
    public void Unmapped_enum_value_preserves_label_and_validates_catalogue()
    {
        var codec = new BlueTuskEnumValueCodec();
        var value = new BlueTuskEnumValue("in-progress");

        Assert.Equal(value, Read(codec, StatusType, "in-progress", BlueTuskDataFormat.Binary));
        Assert.Equal("in-progress", Write(codec, StatusType, value, BlueTuskDataFormat.Text));
        Assert.Throws<InvalidOperationException>(
            () => Read(codec, StatusType, "missing", BlueTuskDataFormat.Text));
    }

    [Fact]
    public void Domain_delegates_text_and_binary_to_its_base_codec()
    {
        var domainType = new BlueTuskTypeDescriptor
        {
            Id = new BlueTuskTypeId(90_200),
            Schema = "app",
            Name = "positive_integer",
            Kind = BlueTuskTypeKind.Domain,
            BaseType = BlueTuskBuiltInTypes.Int4.Id,
        };
        var codec = new BlueTuskDomainCodec(BlueTuskBuiltInTypes.Int4, new BlueTuskInt32Codec());

        foreach (var format in new[] { BlueTuskDataFormat.Binary, BlueTuskDataFormat.Text })
        {
            var bytes = new byte[64];
            var writer = new BlueTuskWriter(bytes);
            codec.Write(ref writer, 42, format, domainType);
            var reader = new BlueTuskReader(bytes.AsSpan(0, writer.WrittenCount));

            Assert.Equal(42, codec.Read(ref reader, format, domainType));
            Assert.Equal(0, reader.Remaining);
        }
    }

    [Fact]
    public void Catalogue_without_a_mapped_type_keeps_the_mapping_until_a_reload_finds_it()
    {
        var configured = new BlueTuskTypeRegistryBuilder()
            .Register("app", "order_status", new BlueTuskEnumCodec<OrderStatus>())
            .Register("app", "address", new BlueTuskCompositeCodec<MissingAddress>())
            .Build();
        BlueTuskCatalogueType[] withoutMappedTypes =
        [
            new BlueTuskCatalogueType
            {
                Id = BlueTuskBuiltInTypes.Int4.Id,
                Schema = "pg_catalog",
                Name = "int4",
                PostgreSqlKind = 'b',
                PostgreSqlCategory = 'N',
            },
        ];

        // Before the migration that creates the types: building must not fail.
        var beforeMigration = BlueTuskTypeCatalogue.BuildRegistry(withoutMappedTypes, configured);
        Assert.False(beforeMigration.TryGetType(new BlueTuskTypeName("app", "order_status"), out _, out _));

        var afterMigration = BlueTuskTypeCatalogue.BuildRegistry(
        [
            .. withoutMappedTypes,
            new BlueTuskCatalogueType
            {
                Id = StatusType.Id,
                Schema = StatusType.Schema,
                Name = StatusType.Name,
                PostgreSqlKind = 'e',
                PostgreSqlCategory = 'E',
                EnumLabels = StatusType.EnumLabels,
            },
        ], configured);
        Assert.True(afterMigration.TryGetType(
            new BlueTuskTypeName("app", "order_status"),
            out _,
            out var enumCodec));
        Assert.IsType<BlueTuskEnumCodec<OrderStatus>>(enumCodec);
    }

    private sealed record MissingAddress(int Number);

    [Fact]
    public void Catalogue_composes_mapped_enum_domain_and_their_arrays()
    {
        var configured = new BlueTuskTypeRegistryBuilder()
            .Register("app", "order_status", new BlueTuskEnumCodec<OrderStatus>())
            .Build();
        var registry = BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = StatusType.Id,
                Schema = StatusType.Schema,
                Name = StatusType.Name,
                PostgreSqlKind = 'e',
                PostgreSqlCategory = 'E',
                ArrayType = new BlueTuskTypeId(90_101),
                EnumLabels = StatusType.EnumLabels,
            },
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(90_101),
                Schema = "app",
                Name = "_order_status",
                PostgreSqlKind = 'b',
                PostgreSqlCategory = 'A',
                ElementType = StatusType.Id,
            },
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(90_200),
                Schema = "app",
                Name = "positive_integer",
                PostgreSqlKind = 'd',
                PostgreSqlCategory = 'N',
                BaseType = BlueTuskBuiltInTypes.Int4.Id,
                ArrayType = new BlueTuskTypeId(90_201),
            },
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(90_201),
                Schema = "app",
                Name = "_positive_integer",
                PostgreSqlKind = 'b',
                PostgreSqlCategory = 'A',
                ElementType = new BlueTuskTypeId(90_200),
            },
        ], configured);

        Assert.True(registry.TryGetCodec(StatusType.Id, out var enumCodec));
        Assert.IsType<BlueTuskEnumCodec<OrderStatus>>(enumCodec);
        Assert.True(registry.TryGetCodec(new BlueTuskTypeId(90_101), out var enumArrayCodec));
        Assert.Equal(
            typeof(OrderStatus[]),
            Assert.IsAssignableFrom<IBlueTuskCodec<OrderStatus[]>>(enumArrayCodec).ClrType);
        Assert.True(registry.TryGetCodec(new BlueTuskTypeId(90_200), out var domainCodec));
        Assert.IsType<BlueTuskDomainCodec>(domainCodec);
        Assert.True(registry.TryGetCodec(new BlueTuskTypeId(90_201), out var domainArrayCodec));
        Assert.Equal(typeof(int[]), Assert.IsType<BlueTuskArrayCodec>(domainArrayCodec).ClrType);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Domain_arrays_preserve_base_descriptor_and_nested_domain_identity(
        bool nested,
        bool directCustomCodec)
    {
        var domain = new BlueTuskTypeDescriptor
        {
            Id = new BlueTuskTypeId(90_300),
            Schema = "app",
            Name = "domain_array_element",
            Kind = BlueTuskTypeKind.Domain,
            BaseType = BlueTuskBuiltInTypes.Int4.Id,
        };
        IBlueTuskCodec baseCodec = directCustomCodec
            ? new DirectInt32Codec()
            : new BlueTuskInt32Codec();
        IBlueTuskCodec elementCodec = new BlueTuskDomainCodec(BlueTuskBuiltInTypes.Int4, baseCodec);
        if (nested)
        {
            elementCodec = new BlueTuskDomainCodec(domain, elementCodec);
            domain = domain with { Id = new BlueTuskTypeId(90_301), BaseType = domain.Id };
        }
        var arrayType = new BlueTuskTypeDescriptor
        {
            Id = new BlueTuskTypeId(90_302),
            Schema = "app",
            Name = "_domain_array_element",
            Kind = BlueTuskTypeKind.Array,
            ElementType = domain.Id,
        };
        var codec = new BlueTuskArrayCodec(domain, elementCodec);
        Assert.Equal(typeof(int[]), codec.ClrType);
        int[] expected = [11, 22, 33];
        foreach (var format in new[] { BlueTuskDataFormat.Binary, BlueTuskDataFormat.Text })
        {
            var bytes = new byte[256];
            var writer = new BlueTuskWriter(bytes);
            codec.Write(ref writer, expected, format, arrayType);
            if (format == BlueTuskDataFormat.Binary)
            {
                Assert.Equal(domain.Id.Oid,
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)));
            }
            var reader = new BlueTuskReader(bytes.AsSpan(0, writer.WrittenCount));
            Assert.Equal(expected, Assert.IsType<int[]>(codec.Read(ref reader, format, arrayType)));
            Assert.Equal(0, reader.Remaining);
        }
    }

    private sealed class DirectInt32Codec : IBlueTuskCodec
    {
        private readonly BlueTuskInt32Codec _inner = new();
        public Type ClrType => typeof(int);
        public object Read(ref BlueTuskReader reader, BlueTuskDataFormat format, BlueTuskTypeDescriptor type)
        {
            Assert.Equal(BlueTuskBuiltInTypes.Int4.Id, type.Id);
            return _inner.ReadTyped(ref reader, format, type);
        }
        public void Write(ref BlueTuskWriter writer, object? value, BlueTuskDataFormat format, BlueTuskTypeDescriptor type)
        {
            Assert.Equal(BlueTuskBuiltInTypes.Int4.Id, type.Id);
            _inner.WriteTyped(ref writer, Assert.IsType<int>(value), format, type);
        }
    }

    private static void AssertRoundTrip(
        BlueTuskEnumCodec<OrderStatus> codec,
        OrderStatus value,
        string label)
    {
        foreach (var format in new[] { BlueTuskDataFormat.Binary, BlueTuskDataFormat.Text })
        {
            Assert.Equal(label, Write(codec, StatusType, value, format));
            Assert.Equal(value, Read(codec, StatusType, label, format));
        }
    }

    private static string Write<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        T value,
        BlueTuskDataFormat format)
    {
        Span<byte> destination = stackalloc byte[128];
        var writer = new BlueTuskWriter(destination);
        codec.WriteTyped(ref writer, value, format, type);
        return Encoding.UTF8.GetString(destination[..writer.WrittenCount]);
    }

    private static T Read<T>(
        BlueTuskCodec<T> codec,
        BlueTuskTypeDescriptor type,
        string label,
        BlueTuskDataFormat format)
    {
        var reader = new BlueTuskReader(Encoding.UTF8.GetBytes(label));
        return codec.ReadTyped(ref reader, format, type);
    }

    private enum OrderStatus
    {
        [BlueTuskName("pending")]
        Pending,

        [EnumMember(Value = "in-progress")]
        InProgress,

        Complete,
    }
}
