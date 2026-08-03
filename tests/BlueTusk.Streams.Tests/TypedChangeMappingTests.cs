using System.Buffers.Binary;
using System.Text;
using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Tests;

public sealed class TypedChangeMappingTests
{
    [Fact]
    public void Conventions_materialize_complete_text_and_binary_rows()
    {
        var table = OrdersTable();
        var mapping = new ChangeEntityMappingBuilder<Order>().Build(table);
        Span<byte> id = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(id, 42);
        var row = new ChangeRow(
            table,
            [
                ChangeColumnValue.FromValue(id, ChangeValueEncoding.Binary),
                Text("Ada"),
                Text("t"),
            ]);

        var typed = mapping.MapRow(row);

        Assert.True(typed.HasValue);
        Assert.NotNull(typed.Value);
        Assert.Equal(42, typed.Value.Id);
        Assert.Equal("Ada", typed.Value.DisplayName);
        Assert.True(typed.Value.IsActive);
        Assert.Equal(["id"], mapping.KeyColumns);
    }

    [Fact]
    public void Explicit_overrides_use_compiled_setters_and_custom_decoder()
    {
        var table = OrdersTable();
        var mapping = new ChangeEntityMappingBuilder<Order>()
            .UseConventions(false)
            .ToTable("sales", "orders")
            .HasKey("id")
            .Property(order => order.Id, "id", 23)
            .Property(
                order => order.DisplayName,
                "display_name",
                25,
                (_, value) => Encoding.UTF8.GetString(value.Data.Span).ToUpperInvariant())
            .Property(order => order.IsActive, "is_active", 16)
            .Build(table);
        var row = new ChangeRow(table, [Text("7"), Text("Ada"), Text("f")]);

        var typed = mapping.MapRow(row);

        Assert.Equal("ADA", typed.Value!.DisplayName);
        Assert.False(typed.Value.IsActive);
    }

    [Theory]
    [InlineData(ChangeColumnState.NotPublished)]
    [InlineData(ChangeColumnState.OldValueUnavailable)]
    [InlineData(ChangeColumnState.UnchangedToast)]
    public void Partial_tuple_states_never_claim_a_complete_clr_value(ChangeColumnState state)
    {
        var table = OrdersTable();
        var mapping = new ChangeEntityMappingBuilder<Order>().Build(table);
        var unavailable = state switch
        {
            ChangeColumnState.NotPublished => ChangeColumnValue.NotPublished,
            ChangeColumnState.OldValueUnavailable => ChangeColumnValue.OldValueUnavailable,
            ChangeColumnState.UnchangedToast => ChangeColumnValue.UnchangedToast,
            _ => throw new InvalidOperationException(),
        };
        var row = new ChangeRow(table, [Text("7"), unavailable, Text("t")]);

        var typed = mapping.MapRow(row);

        Assert.False(typed.HasValue);
        Assert.Null(typed.Value);
        Assert.Same(unavailable, typed.Columns["display_name"]);
    }

    [Fact]
    public void Typed_decoding_failures_pause_by_default()
    {
        var table = OrdersTable();
        var mapping = new ChangeEntityMappingBuilder<Order>().Build(table);
        var row = new ChangeRow(table, [Text("not-an-int"), Text("Ada"), Text("t")]);
        var change = new InsertChange(ChangeIdentity(), row);

        var error = Assert.Throws<TypedChangeDecodingException>(() => mapping.Map(change));

        Assert.Equal("id", error.Failure.Column.Name);
        Assert.Equal(typeof(int), error.Failure.TargetType);
    }

    [Fact]
    public void Explicit_dynamic_decode_policy_preserves_the_dynamic_change()
    {
        var table = OrdersTable();
        var mapping = new ChangeEntityMappingBuilder<Order>().Build(
            table,
            new ChangeMappingPolicy
            {
                DecodingFailureMode = TypedDecodingFailureMode.ContinueDynamically,
            });
        var dynamic = new InsertChange(
            ChangeIdentity(),
            new ChangeRow(table, [Text("broken"), Text("Ada"), Text("t")]));

        var mapped = mapping.Map(dynamic);

        Assert.Same(dynamic, mapped);
    }

    [Fact]
    public void Schema_drift_pauses_by_default_and_can_explicitly_continue_dynamically()
    {
        var expected = OrdersTable();
        var actual = new ChangeTable(
            200,
            "sales",
            "orders",
            'd',
            [
                .. expected.Columns,
                new ChangeColumn(3, "new_column", 25, -1, false),
            ]);
        var dynamic = new InsertChange(
            ChangeIdentity(),
            new ChangeRow(actual, [Text("7"), Text("Ada"), Text("t"), Text("new")]));
        var pauseMapping = new ChangeEntityMappingBuilder<Order>().Build(expected);
        var dynamicMapping = new ChangeEntityMappingBuilder<Order>().Build(
            expected,
            new ChangeMappingPolicy { SchemaChangeMode = SchemaChangeMode.ContinueDynamically });

        var error = Assert.Throws<ChangeSchemaReloadRequiredException>(() => pauseMapping.Map(dynamic));
        var continued = dynamicMapping.Map(dynamic);

        Assert.NotEqual(error.Difference.ExpectedFingerprint, error.Difference.ActualFingerprint);
        Assert.Same(dynamic, continued);
    }

    [Fact]
    public void Fingerprints_are_stable_and_relation_oid_independent()
    {
        var first = OrdersTable(relationId: 100);
        var second = OrdersTable(relationId: 999);
        var firstMapping = new ChangeEntityMappingBuilder<Order>().Build(first);
        var secondMapping = new ChangeEntityMappingBuilder<Order>().Build(second);

        Assert.Equal(ChangeSchemaFingerprint.Create(first), ChangeSchemaFingerprint.Create(second));
        Assert.Equal(firstMapping.MappingFingerprint, secondMapping.MappingFingerprint);
        Assert.Equal(64, firstMapping.MappingFingerprint.Length);
    }

    [Fact]
    public void Snapshot_row_identity_is_deterministic_within_an_epoch()
    {
        var epoch = SnapshotEpoch.Create(Source(), new BlueTuskLogSequenceNumber(900));
        var table = OrdersTable();
        var first = SnapshotRowId.Create(epoch, table, [Text("42")]);
        var second = SnapshotRowId.Create(epoch, table, [Text("42")]);
        var other = SnapshotRowId.Create(epoch, table, [Text("43")]);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal("sales.orders", first.TableIdentity);
    }

    private static ChangeTable OrdersTable(uint relationId = 100) =>
        new(
            relationId,
            "sales",
            "orders",
            'd',
            [
                new ChangeColumn(0, "id", 23, -1, true),
                new ChangeColumn(1, "display_name", 25, -1, false),
                new ChangeColumn(2, "is_active", 16, -1, false),
            ]);

    private static ChangeColumnValue Text(string value) =>
        ChangeColumnValue.FromValue(Encoding.UTF8.GetBytes(value), ChangeValueEncoding.Text);

    private static ChangeId ChangeIdentity() =>
        new(Source(), new BlueTuskLogSequenceNumber(1000), 42, 0);

    private static ChangeSourceIdentity Source() =>
        new("system-1", "app", "orders_slot", "publication-fingerprint");

    private sealed class Order
    {
        public int Id { get; set; }

        public string? DisplayName { get; set; }

        public bool IsActive { get; set; }
    }
}
