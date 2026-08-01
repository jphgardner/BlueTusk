using BlueTusk.TypeSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace BlueTusk.EntityFrameworkCore.Tests;

public sealed class QueryTranslationTests
{
    private const string ConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=bluetusk_tests";

    public static TheoryData<Type, string> NativeClrMappings => new()
    {
        { typeof(uint), "oid" },
        { typeof(BlueTuskObjectIdentifier64), "oid8" },
        { typeof(BlueTuskInterval), "interval" },
        { typeof(BlueTuskTimeWithTimeZone), "time with time zone" },
        { typeof(BlueTuskBitString), "bit varying" },
        { typeof(BlueTuskNumeric), "numeric" },
        { typeof(BlueTuskTupleId), "tid" },
        { typeof(BlueTuskLogSequenceNumber), "pg_lsn" },
        { typeof(BlueTuskNetworkAddress), "inet" },
        { typeof(BlueTuskMacAddress), "macaddr" },
        { typeof(BlueTuskMacAddress8), "macaddr8" },
        { typeof(BlueTuskPoint), "point" },
        { typeof(BlueTuskLine), "line" },
        { typeof(BlueTuskLineSegment), "lseg" },
        { typeof(BlueTuskBox), "box" },
        { typeof(BlueTuskPath), "path" },
        { typeof(BlueTuskPolygon), "polygon" },
        { typeof(BlueTuskCircle), "circle" },
        { typeof(BlueTuskMoney), "money" },
        { typeof(BlueTuskTextSearchVector), "tsvector" },
        { typeof(BlueTuskTextSearchQuery), "tsquery" },
        { typeof(BlueTuskJsonPath), "jsonpath" },
        { typeof(BlueTuskRegProc), "regproc" },
        { typeof(BlueTuskRegProcedure), "regprocedure" },
        { typeof(BlueTuskRegOper), "regoper" },
        { typeof(BlueTuskRegOperator), "regoperator" },
        { typeof(BlueTuskRegClass), "regclass" },
        { typeof(BlueTuskRegType), "regtype" },
        { typeof(BlueTuskRegConfig), "regconfig" },
        { typeof(BlueTuskRegDictionary), "regdictionary" },
        { typeof(BlueTuskRegNamespace), "regnamespace" },
        { typeof(BlueTuskRegRole), "regrole" },
        { typeof(BlueTuskRegCollation), "regcollation" },
        { typeof(BlueTuskRegDatabase), "regdatabase" },
        { typeof(BlueTuskTransactionId), "xid" },
        { typeof(BlueTuskCommandId), "cid" },
        { typeof(BlueTuskFullTransactionId), "xid8" },
        { typeof(BlueTuskTransactionSnapshot), "pg_snapshot" },
        { typeof(BlueTuskRefCursor), "refcursor" },
        { typeof(BlueTuskNodeTree), "pg_node_tree" },
        { typeof(BlueTuskInternalChar), "\"char\"" },
        { typeof(BlueTuskAccessControlItem), "aclitem" },
        { typeof(BlueTuskGistTextSearchVector), "gtsvector" },
        { typeof(BlueTuskInt16Vector), "int2vector" },
        { typeof(BlueTuskObjectIdentifierVector), "oidvector" },
        { typeof(BlueTuskNDistinctStatistics), "pg_ndistinct" },
        { typeof(BlueTuskDependencyStatistics), "pg_dependencies" },
        { typeof(BlueTuskMostCommonValueStatistics), "pg_mcv_list" },
        { typeof(BlueTuskBrinBloomSummary), "pg_brin_bloom_summary" },
        { typeof(BlueTuskBrinMinMaxMultiSummary), "pg_brin_minmax_multi_summary" },
        { typeof(BlueTuskRecord), "record" },
        { typeof(BlueTuskRange<int>), "int4range" },
        { typeof(BlueTuskRange<BlueTuskNumeric>), "numrange" },
        { typeof(BlueTuskRange<DateTime>), "tsrange" },
        { typeof(BlueTuskRange<DateTimeOffset>), "tstzrange" },
        { typeof(BlueTuskRange<DateOnly>), "daterange" },
        { typeof(BlueTuskRange<long>), "int8range" },
        { typeof(BlueTuskMultirange<int>), "int4multirange" },
        { typeof(BlueTuskMultirange<BlueTuskNumeric>), "nummultirange" },
        { typeof(BlueTuskMultirange<DateTime>), "tsmultirange" },
        { typeof(BlueTuskMultirange<DateTimeOffset>), "tstzmultirange" },
        { typeof(BlueTuskMultirange<DateOnly>), "datemultirange" },
        { typeof(BlueTuskMultirange<long>), "int8multirange" },
    };

    public static TheoryData<string, Type> ExplicitNativeStoreMappings => new()
    {
        { "json", typeof(string) },
        { "jsonb", typeof(string) },
        { "xml", typeof(string) },
        { "cidr", typeof(BlueTuskNetworkAddress) },
        { "bit", typeof(BlueTuskBitString) },
        { "txid_snapshot", typeof(BlueTuskTransactionSnapshot) },
        { "oid8", typeof(BlueTuskObjectIdentifier64) },
        { "regdatabase", typeof(BlueTuskRegDatabase) },
    };

    public static TheoryData<Type, string, Type> ArrayMappings => new()
    {
        { typeof(bool[]), "boolean[]", typeof(bool) },
        { typeof(short[]), "smallint[]", typeof(short) },
        { typeof(int[]), "integer[]", typeof(int) },
        { typeof(int[,]), "integer[]", typeof(int) },
        { typeof(string[]), "text[]", typeof(string) },
        { typeof(byte[][]), "bytea[]", typeof(byte[]) },
        { typeof(Guid[]), "uuid[]", typeof(Guid) },
        { typeof(DateOnly[]), "date[]", typeof(DateOnly) },
        { typeof(BlueTuskNetworkAddress[]), "inet[]", typeof(BlueTuskNetworkAddress) },
        { typeof(BlueTuskPoint[]), "point[]", typeof(BlueTuskPoint) },
        { typeof(BlueTuskNumeric[]), "numeric[]", typeof(BlueTuskNumeric) },
        { typeof(BlueTuskObjectIdentifier64[]), "oid8[]", typeof(BlueTuskObjectIdentifier64) },
        { typeof(BlueTuskRegDatabase[]), "regdatabase[]", typeof(BlueTuskRegDatabase) },
        { typeof(BlueTuskTextSearchVector[]), "tsvector[]", typeof(BlueTuskTextSearchVector) },
        { typeof(BlueTuskRange<int>[]), "int4range[]", typeof(BlueTuskRange<int>) },
        { typeof(BlueTuskMultirange<int>[]), "int4multirange[]", typeof(BlueTuskMultirange<int>) },
        { typeof(BlueTuskRecord[]), "record[]", typeof(BlueTuskRecord) },
    };

    [Theory]
    [MemberData(nameof(NativeClrMappings))]
    public void PostgreSQL_native_CLR_types_have_provider_mappings(Type clrType, string expectedStoreType)
    {
        using var context = CreateContext();
        var mapping = context.GetService<IRelationalTypeMappingSource>().FindMapping(clrType);

        Assert.NotNull(mapping);
        Assert.Equal(expectedStoreType, mapping.StoreType);
    }

    [Theory]
    [MemberData(nameof(ExplicitNativeStoreMappings))]
    public void Explicit_PostgreSQL_store_types_select_their_wire_CLR_type(string storeType, Type expectedClrType)
    {
        using var context = CreateContext();
        var mapping = context.GetService<IRelationalTypeMappingSource>().FindMapping(storeType);

        Assert.NotNull(mapping);
        Assert.Equal(expectedClrType, mapping.ClrType);
    }

    [Theory]
    [MemberData(nameof(ArrayMappings))]
    public void CLR_arrays_have_structural_PostgreSQL_array_mappings(
        Type arrayType,
        string expectedStoreType,
        Type expectedElementType)
    {
        using var context = CreateContext();
        var mapping = context.GetService<IRelationalTypeMappingSource>().FindMapping(arrayType);

        Assert.NotNull(mapping);
        Assert.Equal(expectedStoreType, mapping.StoreType);
        Assert.Equal(expectedElementType, mapping.ElementTypeMapping!.ClrType);
        Assert.NotNull(mapping.Comparer);
    }

    [Fact]
    public void Core_model_uses_PostgreSQL_store_types()
    {
        using var context = CreateContext();
        var entityType = context.Model.FindEntityType(typeof(Blog))!;

        Assert.Equal("integer", entityType.FindProperty(nameof(Blog.Id))!.GetColumnType());
        Assert.Equal("character varying(64)", entityType.FindProperty(nameof(Blog.Name))!.GetColumnType());
        Assert.Equal("boolean", entityType.FindProperty(nameof(Blog.IsActive))!.GetColumnType());
        Assert.Equal("smallint", entityType.FindProperty(nameof(Blog.SmallNumber))!.GetColumnType());
        Assert.Equal("bigint", entityType.FindProperty(nameof(Blog.LargeNumber))!.GetColumnType());
        Assert.Equal("real", entityType.FindProperty(nameof(Blog.Ratio))!.GetColumnType());
        Assert.Equal("double precision", entityType.FindProperty(nameof(Blog.Measurement))!.GetColumnType());
        Assert.Equal("numeric(18,4)", entityType.FindProperty(nameof(Blog.Amount))!.GetColumnType());
        Assert.Equal("bytea", entityType.FindProperty(nameof(Blog.Payload))!.GetColumnType());
        Assert.Equal("uuid", entityType.FindProperty(nameof(Blog.Token))!.GetColumnType());
        Assert.Equal("timestamp without time zone", entityType.FindProperty(nameof(Blog.CreatedAt))!.GetColumnType());
        Assert.Equal("timestamp with time zone", entityType.FindProperty(nameof(Blog.PublishedAt))!.GetColumnType());
        Assert.Equal("date", entityType.FindProperty(nameof(Blog.PublishDate))!.GetColumnType());
        Assert.Equal("time without time zone", entityType.FindProperty(nameof(Blog.PublishTime))!.GetColumnType());
        Assert.Equal("interval", entityType.FindProperty(nameof(Blog.Duration))!.GetColumnType());
    }

    [Fact]
    public void Core_query_translates_to_PostgreSQL_SQL()
    {
        using var context = CreateContext();

        var sql = context.Blogs
            .Where(blog => blog.IsActive && blog.Name.StartsWith("Blue"))
            .OrderBy(blog => blog.Id)
            .Select(blog => new { blog.Id, blog.Name })
            .Take(5)
            .ToQueryString();

        Assert.Contains("SELECT", sql, StringComparison.Ordinal);
        Assert.Contains("FROM \"Blogs\" AS \"b\"", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY \"b\".\"Id\"", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Parameterised_string_operations_translate_to_PostgreSQL_functions()
    {
        using var context = CreateContext();
        var prefix = "Blue%";
        var fragment = "Tusk_";

        var sql = context.Blogs
            .Where(blog =>
                blog.Name.StartsWith(prefix)
                && blog.Name.Contains(fragment)
                && blog.Name.Length > 3
                && blog.Name.ToLowerInvariant().Replace("tusk", "db").Substring(0, 2) == "bl")
            .ToQueryString();

        Assert.Contains("left(", sql, StringComparison.Ordinal);
        Assert.Contains("strpos(", sql, StringComparison.Ordinal);
        Assert.Contains("char_length(", sql, StringComparison.Ordinal);
        Assert.Contains("lower(", sql, StringComparison.Ordinal);
        Assert.Contains("replace(", sql, StringComparison.Ordinal);
        Assert.Contains("substring(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Joins_grouping_aggregates_and_paging_translate()
    {
        using var context = CreateContext();

        var groupedSql = context.Blogs
            .GroupBy(blog => blog.IsActive)
            .Select(group => new
            {
                group.Key,
                Count = group.Count(),
                MaximumId = group.Max(blog => blog.Id),
            })
            .ToQueryString();

        var joinSql = context.Blogs
            .Join(
                context.Blogs,
                left => left.Id,
                right => right.Id,
                (left, right) => new { left.Id, RightName = right.Name })
            .Skip(2)
            .Take(3)
            .ToQueryString();

        Assert.Contains("GROUP BY", groupedSql, StringComparison.Ordinal);
        Assert.Contains("count(*)", groupedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max(", groupedSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INNER JOIN", joinSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", joinSql, StringComparison.Ordinal);
        Assert.Contains("OFFSET", joinSql, StringComparison.Ordinal);
    }

    private static BlogContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<BlogContext>()
            .UseBlueTusk(ConnectionString)
            .Options;
        return new BlogContext(options);
    }

    private sealed class BlogContext(DbContextOptions<BlogContext> options) : DbContext(options)
    {
        public DbSet<Blog> Blogs => Set<Blog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Blog>().Property(blog => blog.Name).HasMaxLength(64);
            modelBuilder.Entity<Blog>().Property(blog => blog.Amount).HasPrecision(18, 4);
        }
    }

    private sealed class Blog
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool IsActive { get; set; }

        public short SmallNumber { get; set; }

        public long LargeNumber { get; set; }

        public float Ratio { get; set; }

        public double Measurement { get; set; }

        public decimal Amount { get; set; }

        public byte[] Payload { get; set; } = [];

        public Guid Token { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTimeOffset PublishedAt { get; set; }

        public DateOnly PublishDate { get; set; }

        public TimeOnly PublishTime { get; set; }

        public TimeSpan Duration { get; set; }
    }
}
