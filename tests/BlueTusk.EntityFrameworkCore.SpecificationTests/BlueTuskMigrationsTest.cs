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
