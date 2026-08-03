using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit.Sdk;

namespace Microsoft.EntityFrameworkCore.ModelBuilding;

public sealed class BlueTuskModelBuilderGenericTest : RelationalModelBuilderTest
{
    public sealed class BlueTuskGenericNonRelationship(BlueTuskModelBuilderFixture fixture)
        : RelationalNonRelationshipTestBase(fixture), IClassFixture<BlueTuskModelBuilderFixture>
    {
        protected override void Mapping_throws_for_non_ignored_three_dimensional_array()
            => Assert.Throws<ThrowsException>(
                () => base.Mapping_throws_for_non_ignored_three_dimensional_array());

        protected override TestModelBuilder CreateModelBuilder(
            Action<ModelConfigurationBuilder>? configure)
            => new GenericTestModelBuilder(Fixture, configure);
    }

    public sealed class BlueTuskGenericComplexType(BlueTuskModelBuilderFixture fixture)
        : RelationalComplexTypeTestBase(fixture), IClassFixture<BlueTuskModelBuilderFixture>
    {
        protected override TestModelBuilder CreateModelBuilder(
            Action<ModelConfigurationBuilder>? configure)
            => new GenericTestModelBuilder(Fixture, configure);
    }

    public sealed class BlueTuskGenericComplexCollectionTests(BlueTuskModelBuilderFixture fixture)
        : RelationalComplexCollectionTestBase(fixture), IClassFixture<BlueTuskModelBuilderFixture>
    {
        protected override TestModelBuilder CreateModelBuilder(
            Action<ModelConfigurationBuilder>? configure)
            => new GenericTestModelBuilder(Fixture, configure);
    }

    public sealed class BlueTuskGenericInheritance(BlueTuskModelBuilderFixture fixture)
        : RelationalInheritanceTestBase(fixture), IClassFixture<BlueTuskModelBuilderFixture>
    {
        protected override TestModelBuilder CreateModelBuilder(
            Action<ModelConfigurationBuilder>? configure)
            => new GenericTestModelBuilder(Fixture, configure);
    }

    public sealed class BlueTuskGenericOneToMany(BlueTuskModelBuilderFixture fixture)
        : RelationalOneToManyTestBase(fixture), IClassFixture<BlueTuskModelBuilderFixture>
    {
        protected override TestModelBuilder CreateModelBuilder(
            Action<ModelConfigurationBuilder>? configure)
            => new GenericTestModelBuilder(Fixture, configure);
    }

    public sealed class BlueTuskGenericManyToOne(BlueTuskModelBuilderFixture fixture)
        : RelationalManyToOneTestBase(fixture), IClassFixture<BlueTuskModelBuilderFixture>
    {
        protected override TestModelBuilder CreateModelBuilder(
            Action<ModelConfigurationBuilder>? configure)
            => new GenericTestModelBuilder(Fixture, configure);
    }

    public sealed class BlueTuskGenericOneToOne(BlueTuskModelBuilderFixture fixture)
        : RelationalOneToOneTestBase(fixture), IClassFixture<BlueTuskModelBuilderFixture>
    {
        protected override TestModelBuilder CreateModelBuilder(
            Action<ModelConfigurationBuilder>? configure)
            => new GenericTestModelBuilder(Fixture, configure);
    }

    public sealed class BlueTuskGenericManyToMany(BlueTuskModelBuilderFixture fixture)
        : RelationalManyToManyTestBase(fixture), IClassFixture<BlueTuskModelBuilderFixture>
    {
        protected override TestModelBuilder CreateModelBuilder(
            Action<ModelConfigurationBuilder>? configure)
            => new GenericTestModelBuilder(Fixture, configure);
    }

    public sealed class BlueTuskGenericOwnedTypes(BlueTuskModelBuilderFixture fixture)
        : RelationalOwnedTypesTestBase(fixture), IClassFixture<BlueTuskModelBuilderFixture>
    {
        protected override TestModelBuilder CreateModelBuilder(
            Action<ModelConfigurationBuilder>? configure)
            => new GenericTestModelBuilder(Fixture, configure);
    }

    public sealed class BlueTuskModelBuilderFixture : RelationalModelBuilderFixture
    {
        public override TestHelpers TestHelpers
            => BlueTuskTestHelpers.Instance;
    }
}
