using System.Data.Common;
using BlueTusk.Client;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskDataReaderTypeOidTests
{
    [Fact]
    public void Raw_type_oid_retains_full_unsigned_range_even_for_sql_null()
    {
        const uint oid = 0x80000001;
        using var reader = new BlueTuskDataReader(new BlueTuskQueryResult([
            new BlueTuskResultSet(
                [new BlueTuskFieldDescription("custom", 0, 0, oid, -1, -1, 0)],
                [new BlueTuskDataRow([null])], "SELECT 1"),
        ]), null, BlueTuskBuiltInTypes.CreateRegistry());

        Assert.Equal(oid, reader.GetPostgreSqlTypeOid(0));
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(oid, reader.GetPostgreSqlTypeOid(0));
        Assert.Equal(DBNull.Value, reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.ProviderType]);
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetPostgreSqlTypeOid(-1));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetPostgreSqlTypeOid(1));
    }

    [Fact]
    public void Signed_range_oid_remains_compatible_with_schema_table()
    {
        using var reader = new BlueTuskDataReader(new BlueTuskQueryResult([
            new BlueTuskResultSet(
                [new BlueTuskFieldDescription("integer", 0, 0, 23, -1, -1, 0)],
                [], "SELECT 0"),
        ]), null, BlueTuskBuiltInTypes.CreateRegistry());

        Assert.Equal(23u, reader.GetPostgreSqlTypeOid(0));
        Assert.Equal(23, reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.ProviderType]);
    }
}
