using System.Data;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit.Sdk;

namespace Microsoft.EntityFrameworkCore.Query;

#nullable disable

[BlueTuskLiveCondition]
public sealed class BlueTuskAdHocJsonQueryTest(NonSharedFixture fixture)
    : AdHocJsonQueryRelationalTestBase(fixture)
{
    protected override ITestStoreFactory TestStoreFactory
        => BlueTuskTestStoreFactory.Instance;

    protected override string JsonColumnType
        => "jsonb";

    protected override async Task Seed21006(Context21006 context)
    {
        await base.Seed21006(context);
        await ExecuteLiteralSqlAsync(
            context,
            """
            INSERT INTO "Entities" ("Id", "Name", "OptionalReference", "RequiredReference", "Collection") VALUES
            (2, 'missing top-level scalars',
             '{"Text":"optional","NestedRequiredReference":{"DoB":"2000-01-01T00:00:00","Text":"required"},"NestedCollection":[]}',
             '{"Text":"required","NestedRequiredReference":{"DoB":"2000-01-01T00:00:00","Text":"required"},"NestedCollection":[]}',
             '[{"Text":"collection","NestedRequiredReference":{"DoB":"2000-01-01T00:00:00","Text":"required"},"NestedCollection":[]} ]'),
            (3, 'missing nested scalars',
             '{"Number":3,"Text":"optional","NestedOptionalReference":{"Text":"optional"},"NestedRequiredReference":{"Text":"required"},"NestedCollection":[{"Text":"collection"}]}',
             '{"Number":3,"Text":"required","NestedOptionalReference":{"Text":"optional"},"NestedRequiredReference":{"Text":"required"},"NestedCollection":[{"Text":"collection"}]}',
             '[{"Number":3,"Text":"collection","NestedOptionalReference":{"Text":"optional"},"NestedRequiredReference":{"Text":"required"},"NestedCollection":[{"Text":"collection"}]}]'),
            (4, 'null required scalar',
             '{"Number":4,"Text":"optional","NestedRequiredReference":{"DoB":"2000-01-01T00:00:00","Text":"required"},"NestedCollection":[]}',
             '{"Number":null,"Text":"required","NestedRequiredReference":{"DoB":"2000-01-01T00:00:00","Text":"required"},"NestedCollection":[]}',
             '[]'),
            (5, 'missing required navigation',
             '{"Number":5,"Text":"optional","NestedCollection":[]}',
             '{"Number":5,"Text":"required","NestedCollection":[]}',
             '[{"Number":5,"Text":"collection","NestedCollection":[]}]'),
            (6, 'null required navigation',
             '{"Number":6,"Text":"optional","NestedRequiredReference":null,"NestedCollection":[]}',
             '{"Number":6,"Text":"required","NestedRequiredReference":null,"NestedCollection":[]}',
             '[{"Number":6,"Text":"collection","NestedRequiredReference":null,"NestedCollection":[]}]')
            """);
    }

    protected override async Task Seed29219(DbContext ctx)
    {
        ctx.AddRange(
            new Context29219.MyEntity
            {
                Id = 1,
                Reference = new Context29219.MyJsonEntity { NonNullableScalar = 10, NullableScalar = 11 },
                Collection =
                [
                    new() { NonNullableScalar = 100, NullableScalar = 101 },
                    new() { NonNullableScalar = 200, NullableScalar = 201 },
                    new() { NonNullableScalar = 300, NullableScalar = null },
                ],
            },
            new Context29219.MyEntity
            {
                Id = 2,
                Reference = new Context29219.MyJsonEntity { NonNullableScalar = 20, NullableScalar = null },
                Collection = [new() { NonNullableScalar = 1001, NullableScalar = null }],
            });
        await ctx.SaveChangesAsync();

        await ExecuteLiteralSqlAsync(ctx,
            """
            INSERT INTO "Entities" ("Id", "Reference", "Collection")
            VALUES (3, '{ "NonNullableScalar": 30 }', '[{ "NonNullableScalar": 10001 }]')
            """);
    }

    protected override async Task Seed30028(DbContext ctx)
    {
        await ExecuteLiteralSqlAsync(ctx,
            """
            INSERT INTO "Entities" ("Id", "Json") VALUES
            (1, '{"RootName":"e1","Collection":[{"BranchName":"e1 c1","Nested":{"LeafName":"e1 c1 l"}},{"BranchName":"e1 c2","Nested":{"LeafName":"e1 c2 l"}}],"OptionalReference":{"BranchName":"e1 or","Nested":{"LeafName":"e1 or l"}},"RequiredReference":{"BranchName":"e1 rr","Nested":{"LeafName":"e1 rr l"}}}'),
            (2, '{"RootName":"e2","OptionalReference":{"BranchName":"e2 or","Nested":{"LeafName":"e2 or l"}},"RequiredReference":{"BranchName":"e2 rr","Nested":{"LeafName":"e2 rr l"}}}'),
            (3, '{"RootName":"e3","Collection":[{"BranchName":"e3 c1","Nested":{"LeafName":"e3 c1 l"}},{"BranchName":"e3 c2","Nested":{"LeafName":"e3 c2 l"}}],"RequiredReference":{"BranchName":"e3 rr","Nested":{"LeafName":"e3 rr l"}}}'),
            (4, '{"RootName":"e4","Collection":[{"BranchName":"e4 c1","Nested":{"LeafName":"e4 c1 l"}},{"BranchName":"e4 c2","Nested":{"LeafName":"e4 c2 l"}}],"OptionalReference":{"BranchName":"e4 or","Nested":{"LeafName":"e4 or l"}}}')
            """);
    }

    protected override Task Seed33046(DbContext ctx)
        => ExecuteLiteralSqlAsync(ctx,
            """
            INSERT INTO "Reviews" ("Rounds", "Id")
            VALUES ('[{"RoundNumber":11,"SubRounds":[{"SubRoundNumber":111},{"SubRoundNumber":112}]}]', 1)
            """);

    protected override Task SeedJunkInJson(DbContext ctx)
        => ExecuteLiteralSqlAsync(ctx,
            """
            INSERT INTO "Entities" ("Collection", "CollectionWithCtor", "Reference", "ReferenceWithCtor", "Id") VALUES (
            '[{"JunkReference":{"Something":"SomeValue"},"Name":"c11","JunkProperty1":50,"Number":11.5,"JunkCollection1":[],"JunkCollection2":[{"Foo":"junk value"}],"NestedCollection":[{"DoB":"2002-04-01T00:00:00","DummyProp":"Dummy value"},{"DoB":"2002-04-02T00:00:00","DummyReference":{"Foo":5}}],"NestedReference":{"DoB":"2002-03-01T00:00:00"}},{"Name":"c12","Number":12.5,"NestedCollection":[{"DoB":"2002-06-01T00:00:00"},{"DoB":"2002-06-02T00:00:00"}],"NestedDummy":59,"NestedReference":{"DoB":"2002-05-01T00:00:00"}}]',
            '[{"MyBool":true,"Name":"c11 ctor","JunkReference":{"Something":"SomeValue","JunkCollection":[{"Foo":"junk value"}]},"NestedCollection":[{"DoB":"2002-08-01T00:00:00"},{"DoB":"2002-08-02T00:00:00"}],"NestedReference":{"DoB":"2002-07-01T00:00:00"}},{"MyBool":false,"Name":"c12 ctor","NestedCollection":[{"DoB":"2002-10-01T00:00:00"},{"DoB":"2002-10-02T00:00:00"}],"JunkCollection":[{"Foo":"junk value"}],"NestedReference":{"DoB":"2002-09-01T00:00:00"}}]',
            '{"Name":"r1","JunkCollection":[{"Foo":"junk value"}],"JunkReference":{"Something":"SomeValue"},"Number":1.5,"NestedCollection":[{"DoB":"2000-02-01T00:00:00","JunkReference":{"Something":"SomeValue"}},{"DoB":"2000-02-02T00:00:00"}],"NestedReference":{"DoB":"2000-01-01T00:00:00"}}',
            '{"MyBool":true,"JunkCollection":[{"Foo":"junk value"}],"Name":"r1 ctor","JunkReference":{"Something":"SomeValue"},"NestedCollection":[{"DoB":"2001-02-01T00:00:00"},{"DoB":"2001-02-02T00:00:00"}],"NestedReference":{"JunkCollection":[{"Foo":"junk value"}],"DoB":"2001-01-01T00:00:00"}}',
            1)
            """);

    protected override Task SeedTrickyBuffering(DbContext ctx)
        => ExecuteLiteralSqlAsync(ctx,
            """
            INSERT INTO "Entities" ("Reference", "Id") VALUES (
            '{"Name":"r1","Number":7,"JunkReference":{"Something":"SomeValue"},"JunkCollection":[{"Foo":"junk value"}],"NestedReference":{"DoB":"2000-01-01T00:00:00Z"},"NestedCollection":[{"DoB":"2000-02-01T00:00:00Z","JunkReference":{"Something":"SomeValue"}},{"DoB":"2000-02-02T00:00:00Z"}]}', 1)
            """);

    protected override Task SeedShadowProperties(DbContext ctx)
        => ExecuteLiteralSqlAsync(ctx,
            """
            INSERT INTO "Entities" ("Collection", "CollectionWithCtor", "Reference", "ReferenceWithCtor", "Id", "Name") VALUES (
            '[{"Name":"e1_c1","ShadowDouble":5.5},{"ShadowDouble":20.5,"Name":"e1_c2"}]',
            '[{"Name":"e1_c1 ctor","ShadowNullableByte":6},{"ShadowNullableByte":null,"Name":"e1_c2 ctor"}]',
            '{"Name":"e1_r","ShadowString":"Foo"}',
            '{"ShadowInt":143,"Name":"e1_r ctor"}',
            1, 'e1')
            """);

    protected override async Task SeedNotICollection(DbContext ctx)
    {
        await ExecuteLiteralSqlAsync(ctx,
            """
            INSERT INTO "Entities" ("Json", "Id") VALUES
            ('{"Collection":[{"Bar":11,"Foo":"c11"},{"Bar":12,"Foo":"c12"},{"Bar":13,"Foo":"c13"}]}', 1),
            ('{"Collection":[{"Bar":21,"Foo":"c21"},{"Bar":22,"Foo":"c22"}]}', 2)
            """);
    }

    protected override async Task Seed34960(Context34960 ctx)
    {
        await base.Seed34960(ctx);
        await ExecuteLiteralSqlAsync(
            ctx,
            """
            INSERT INTO "Junk" ("Id", "Reference", "Collection") VALUES
            (1, '{"Name":"reference","Number":1}', '{"Name":"object instead of collection","Number":1}'),
            (2, '[{"Name":"array instead of reference","Number":2}]', '[{"Name":"collection","Number":2}]')
            """);
    }

    public override async Task Bad_json_properties_duplicated_navigations(bool noTracking)
    {
        if (noTracking)
        {
            await Assert.ThrowsAsync<NotSupportedException>(
                () => base.Bad_json_properties_duplicated_navigations(noTracking: true));
        }
        else
        {
            await base.Bad_json_properties_duplicated_navigations(noTracking: false);
        }
    }

    public override Task Bad_json_properties_duplicated_scalars(bool noTracking)
        => Assert.ThrowsAsync<NotSupportedException>(() => base.Bad_json_properties_duplicated_scalars(noTracking));

    public override Task Bad_json_properties_empty_navigations(bool noTracking)
        => Assert.ThrowsAsync<NotSupportedException>(() => base.Bad_json_properties_empty_navigations(noTracking));

    public override Task Bad_json_properties_empty_scalars(bool noTracking)
        => Assert.ThrowsAsync<NotSupportedException>(() => base.Bad_json_properties_empty_scalars(noTracking));

    public override Task Bad_json_properties_null_navigations(bool noTracking)
        => Assert.ThrowsAsync<ThrowsAnyException>(() => base.Bad_json_properties_null_navigations(noTracking));

    public override Task Bad_json_properties_null_scalars(bool noTracking)
        => Assert.ThrowsAsync<ThrowsAnyException>(() => base.Bad_json_properties_null_scalars(noTracking));

    protected override Task SeedBadJsonProperties(ContextBadJsonProperties ctx)
        => throw new NotSupportedException("PostgreSQL jsonb rejects malformed JSON documents.");

    private static async Task ExecuteLiteralSqlAsync(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        var closeConnection = connection.State == ConnectionState.Closed;
        if (closeConnection)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            _ = await command.ExecuteNonQueryAsync();
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }
}
