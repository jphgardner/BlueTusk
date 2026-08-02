using BlueTusk.Data;
using BlueTusk.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

#pragma warning disable EF1001 // The provider specification gate intentionally exercises design-time infrastructure.

namespace Microsoft.EntityFrameworkCore.Migrations;

[BlueTuskLiveCondition]
public sealed class BlueTuskMigrationsTest
    : MigrationsTestBase<BlueTuskMigrationsTest.BlueTuskMigrationsFixture>
{
    public BlueTuskMigrationsTest(
        BlueTuskMigrationsFixture fixture,
        ITestOutputHelper testOutputHelper)
        : base(fixture)
    {
        Fixture.TestSqlLoggerFactory.Clear();
        Fixture.TestSqlLoggerFactory.SetTestOutputHelper(testOutputHelper);
    }

    public override Task Add_required_primitive_collection_with_custom_default_value_sql_to_existing_table()
        => Add_required_primitive_collection_with_custom_default_value_sql_to_existing_table_core("ARRAY[1,2,3]");

    public override Task Add_required_primitve_collection_with_custom_default_value_sql_to_existing_table()
        => Add_required_primitve_collection_with_custom_default_value_sql_to_existing_table_core("ARRAY[3,2,1]");

    [BlueTuskServerVersionCondition(180000, "Virtual generated-column cases")]
    public override Task Create_table_with_computed_column(bool? stored)
        => base.Create_table_with_computed_column(stored);

    [BlueTuskServerVersionCondition(180000, "Virtual generated-column cases")]
    public override Task Alter_column_make_computed(bool? stored)
        => base.Alter_column_make_computed(stored);

    [BlueTuskServerVersionCondition(170000, "Generated-column expression changes")]
    public override Task Alter_column_change_computed_recreates_indexes()
        => base.Alter_column_change_computed_recreates_indexes();

    [BlueTuskServerVersionCondition(170000, "Generated-column expression changes")]
    public override Task Alter_column_change_computed()
        => base.Alter_column_change_computed();

    [BlueTuskServerVersionCondition(180000, "Virtual generated-column cases")]
    public override Task Add_column_computed_with_collation(bool stored)
        => base.Add_column_computed_with_collation(stored);

    [BlueTuskServerVersionCondition(180000, "Virtual generated-column cases")]
    public override Task Add_column_with_computedSql(bool? stored)
        => base.Add_column_with_computedSql(stored);

    [BlueTuskServerVersionCondition(180000, "Virtual generated-column cases")]
    public override Task Alter_column_change_computed_type()
        => base.Alter_column_change_computed_type();

    protected override string NonDefaultCollation
        => "POSIX";

    public override Task Convert_string_column_to_a_json_column_containing_reference()
        => AssertJsonConversionFailure(base.Convert_string_column_to_a_json_column_containing_reference);

    public override Task Convert_string_column_to_a_json_column_containing_required_reference()
        => AssertJsonConversionFailure(base.Convert_string_column_to_a_json_column_containing_required_reference);

    public override Task Convert_string_column_to_a_json_column_containing_collection()
        => AssertJsonConversionFailure(base.Convert_string_column_to_a_json_column_containing_collection);

    private static async Task AssertJsonConversionFailure(Func<Task> test)
    {
        var exception = await Assert.ThrowsAsync<BlueTuskException>(test);
        Assert.Equal("42804", exception.SqlState);
    }

    public sealed class BlueTuskMigrationsFixture : MigrationsFixtureBase
    {
        protected override string StoreName
            => nameof(BlueTuskMigrationsTest);

        protected override ITestStoreFactory TestStoreFactory
            => BlueTuskTestStoreFactory.Instance;

        public override RelationalTestHelpers TestHelpers
            => BlueTuskTestHelpers.Instance;

        protected override IServiceCollection AddServices(IServiceCollection serviceCollection)
            => base.AddServices(serviceCollection)
                .AddScoped<IDatabaseModelFactory, BlueTuskDatabaseModelFactory>();

        public override Task DisposeAsync()
            => BlueTuskTestStore.IsConfigured ? base.DisposeAsync() : Task.CompletedTask;
    }
}
