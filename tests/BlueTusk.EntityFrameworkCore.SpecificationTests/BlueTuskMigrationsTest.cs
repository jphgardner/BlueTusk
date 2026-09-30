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

    public override Task Create_table_with_computed_column(bool? stored)
        => AssertGeneratedColumnMigration(
            () => base.Create_table_with_computed_column(stored), stored == false ? 180000 : 0);

    public override Task Alter_column_make_computed(bool? stored)
        => AssertGeneratedColumnMigration(
            () => base.Alter_column_make_computed(stored), stored == false ? 180000 : 0);

    public override Task Alter_column_change_computed_recreates_indexes()
        => AssertGeneratedColumnMigration(base.Alter_column_change_computed_recreates_indexes, 170000);

    public override Task Alter_column_change_computed()
        => AssertGeneratedColumnMigration(base.Alter_column_change_computed, 170000);

    public override Task Add_column_computed_with_collation(bool stored)
        => AssertGeneratedColumnMigration(
            () => base.Add_column_computed_with_collation(stored), stored ? 0 : 180000);

    public override Task Add_column_with_computedSql(bool? stored)
        => AssertGeneratedColumnMigration(
            () => base.Add_column_with_computedSql(stored), stored == false ? 180000 : 0);

    public override Task Alter_column_change_computed_type()
        => AssertGeneratedColumnMigration(base.Alter_column_change_computed_type, 180000);

    private static async Task AssertGeneratedColumnMigration(Func<Task> test, int minimumVersion)
    {
        if (minimumVersion != 0)
        {
            var settings = new BlueTuskConnectionStringBuilder(
                Environment.GetEnvironmentVariable(BlueTuskTestStore.ConnectionStringEnvironmentVariable)!)
            {
                Database = "postgres",
                Pooling = false,
            };
            await using var dataSource = BlueTuskDataSource.Create(settings.ConnectionString);
            await using var command = dataSource.CreateCommand(
                "SELECT current_setting('server_version_num')::int4");
            var serverVersion = await command.ExecuteScalarAsync<int>(CancellationToken.None);
            if (serverVersion < minimumVersion)
            {
                // Execute the migration on older servers too: its explicit
                // capability rejection is part of the provider contract.
                var exception = await Assert.ThrowsAsync<BlueTuskException>(test);
                Assert.Equal("0A000", exception.SqlState);
                Assert.Contains(
                    minimumVersion == 180000
                        ? "BlueTusk virtual generated columns require PostgreSQL 18 or later."
                        : "BlueTusk generated-column expression changes require PostgreSQL 17 or later.",
                    exception.Message,
                    StringComparison.Ordinal);
                return;
            }
        }

        await test();
    }

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
