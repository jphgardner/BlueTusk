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
        [ConditionalFact]
        public override void Can_add_shadow_primitive_collections_when_they_have_been_ignored()
            => base.Can_add_shadow_primitive_collections_when_they_have_been_ignored();

        [ConditionalFact]
        public override void Can_add_shadow_properties_when_they_have_been_ignored()
            => base.Can_add_shadow_properties_when_they_have_been_ignored();

        [ConditionalFact]
        public override void Can_set_custom_value_generator_for_primitive_collections()
            => base.Can_set_custom_value_generator_for_primitive_collections();

        [ConditionalFact]
        public override void Can_set_custom_value_generator_for_properties()
            => base.Can_set_custom_value_generator_for_properties();

        [ConditionalFact]
        public override void Can_set_max_length_for_primitive_collections()
            => base.Can_set_max_length_for_primitive_collections();

        [ConditionalFact]
        public override void Can_set_max_length_for_properties()
            => base.Can_set_max_length_for_properties();

        [ConditionalFact]
        public override void Can_set_max_length_for_property_type()
            => base.Can_set_max_length_for_property_type();

        [ConditionalFact]
        public override void Can_set_precision_and_scale_for_properties()
            => base.Can_set_precision_and_scale_for_properties();

        [ConditionalFact]
        public override void Can_set_precision_and_scale_for_property_type()
            => base.Can_set_precision_and_scale_for_property_type();

        [ConditionalFact]
        public override void Can_set_primitive_collection_annotation_when_no_clr_property()
            => base.Can_set_primitive_collection_annotation_when_no_clr_property();

        [ConditionalFact]
        public override void Can_set_sentinel_for_primitive_collections()
            => base.Can_set_sentinel_for_primitive_collections();

        [ConditionalFact]
        public override void Can_set_sentinel_for_properties()
            => base.Can_set_sentinel_for_properties();

        [ConditionalFact]
        public override void Can_set_sentinel_for_property_type()
            => base.Can_set_sentinel_for_property_type();

        [ConditionalFact]
        public override void Can_set_unbounded_max_length_for_property_type()
            => base.Can_set_unbounded_max_length_for_property_type();

        [ConditionalFact]
        public override void Can_set_unicode_for_primitive_collections()
            => base.Can_set_unicode_for_primitive_collections();

        [ConditionalFact]
        public override void Can_set_unicode_for_properties()
            => base.Can_set_unicode_for_properties();

        [ConditionalFact]
        public override void Can_set_unicode_for_property_type()
            => base.Can_set_unicode_for_property_type();

        [ConditionalFact]
        public override void Can_specify_discriminator_value()
            => base.Can_specify_discriminator_value();

        [ConditionalFact]
        public override void Non_nullable_properties_cannot_be_made_optional()
            => base.Non_nullable_properties_cannot_be_made_optional();

        [ConditionalFact]
        public override void Primitive_collections_are_required_by_default_only_if_CLR_type_is_nullable()
            => base.Primitive_collections_are_required_by_default_only_if_CLR_type_is_nullable();

        [ConditionalFact]
        public override void Primitive_collections_can_be_made_concurrency_tokens()
            => base.Primitive_collections_can_be_made_concurrency_tokens();

        [ConditionalFact]
        public override void Primitive_collections_can_be_made_optional()
            => base.Primitive_collections_can_be_made_optional();

        [ConditionalFact]
        public override void Primitive_collections_can_be_made_required()
            => base.Primitive_collections_can_be_made_required();

        [ConditionalFact]
        public override void Primitive_collections_can_be_set_to_generate_values_on_Add()
            => base.Primitive_collections_can_be_set_to_generate_values_on_Add();

        [ConditionalFact]
        public override void Primitive_collections_specified_by_string_are_shadow_properties_unless_already_known_to_be_CLR_properties()
            => base.Primitive_collections_specified_by_string_are_shadow_properties_unless_already_known_to_be_CLR_properties();

        [ConditionalFact]
        public override void Properties_are_required_by_default_only_if_CLR_type_is_nullable()
            => base.Properties_are_required_by_default_only_if_CLR_type_is_nullable();

        [ConditionalFact]
        public override void Properties_can_be_made_concurrency_tokens()
            => base.Properties_can_be_made_concurrency_tokens();

        [ConditionalFact]
        public override void Properties_can_be_made_optional()
            => base.Properties_can_be_made_optional();

        [ConditionalFact]
        public override void Properties_can_be_made_required()
            => base.Properties_can_be_made_required();

        [ConditionalFact]
        public override void Properties_can_be_set_to_generate_values_on_Add()
            => base.Properties_can_be_set_to_generate_values_on_Add();

        [ConditionalFact]
        public override void Properties_can_have_access_mode_set()
            => base.Properties_can_have_access_mode_set();

        [ConditionalFact]
        public override void Properties_can_have_custom_type_value_converter_type_set()
            => base.Properties_can_have_custom_type_value_converter_type_set();

        [ConditionalFact]
        public override void Properties_can_have_non_generic_value_converter_set()
            => base.Properties_can_have_non_generic_value_converter_set();

        [ConditionalFact]
        public override void Properties_can_have_provider_type_set()
            => base.Properties_can_have_provider_type_set();

        [ConditionalFact]
        public override void Properties_can_have_provider_type_set_for_type()
            => base.Properties_can_have_provider_type_set_for_type();

        [ConditionalFact]
        public override void Properties_can_have_value_converter_set()
            => base.Properties_can_have_value_converter_set();

        [ConditionalFact]
        public override void Properties_can_have_value_converter_set_inline()
            => base.Properties_can_have_value_converter_set_inline();

        [ConditionalFact]
        public override void Properties_can_set_row_version()
            => base.Properties_can_set_row_version();

        [ConditionalFact]
        public override void Properties_specified_by_string_are_shadow_properties_unless_already_known_to_be_CLR_properties()
            => base.Properties_specified_by_string_are_shadow_properties_unless_already_known_to_be_CLR_properties();

        [ConditionalFact]
        public override void Value_converter_configured_on_non_nullable_type_is_applied()
            => base.Value_converter_configured_on_non_nullable_type_is_applied();

        [ConditionalFact]
        public override void Value_converter_configured_on_nullable_type_overrides_non_nullable()
            => base.Value_converter_configured_on_nullable_type_overrides_non_nullable();

        protected override TestModelBuilder CreateModelBuilder(
            Action<ModelConfigurationBuilder>? configure)
            => new GenericTestModelBuilder(Fixture, configure);
    }

    public sealed class BlueTuskGenericComplexCollectionTests(BlueTuskModelBuilderFixture fixture)
        : RelationalComplexCollectionTestBase(fixture), IClassFixture<BlueTuskModelBuilderFixture>
    {
        [ConditionalFact]
        public override void Can_add_shadow_properties_when_they_have_been_ignored()
            => base.Can_add_shadow_properties_when_they_have_been_ignored();

        [ConditionalFact]
        public override void Can_set_custom_value_generator_for_properties()
            => base.Can_set_custom_value_generator_for_properties();

        [ConditionalFact]
        public override void Can_set_max_length_for_property_type()
            => base.Can_set_max_length_for_property_type();

        [ConditionalFact]
        public override void Can_set_precision_and_scale_for_property_type()
            => base.Can_set_precision_and_scale_for_property_type();

        [ConditionalFact]
        public override void Can_set_sentinel_for_properties()
            => base.Can_set_sentinel_for_properties();

        [ConditionalFact]
        public override void Can_set_sentinel_for_property_type()
            => base.Can_set_sentinel_for_property_type();

        [ConditionalFact]
        public override void Can_set_unbounded_max_length_for_property_type()
            => base.Can_set_unbounded_max_length_for_property_type();

        [ConditionalFact]
        public override void Can_set_unicode_for_properties()
            => base.Can_set_unicode_for_properties();

        [ConditionalFact]
        public override void Can_set_unicode_for_property_type()
            => base.Can_set_unicode_for_property_type();

        [ConditionalFact]
        public override void Non_nullable_properties_cannot_be_made_optional()
            => base.Non_nullable_properties_cannot_be_made_optional();

        [ConditionalFact]
        public override void Properties_are_required_by_default_only_if_CLR_type_is_nullable()
            => base.Properties_are_required_by_default_only_if_CLR_type_is_nullable();

        [ConditionalFact]
        public override void Properties_can_be_made_optional()
            => base.Properties_can_be_made_optional();

        [ConditionalFact]
        public override void Properties_can_be_made_required()
            => base.Properties_can_be_made_required();

        [ConditionalFact]
        public override void Properties_can_have_access_mode_set()
            => base.Properties_can_have_access_mode_set();

        [ConditionalFact]
        public override void Properties_can_have_custom_type_value_converter_type_set()
            => base.Properties_can_have_custom_type_value_converter_type_set();

        [ConditionalFact]
        public override void Properties_can_have_non_generic_value_converter_set()
            => base.Properties_can_have_non_generic_value_converter_set();

        [ConditionalFact]
        public override void Properties_can_have_provider_type_set()
            => base.Properties_can_have_provider_type_set();

        [ConditionalFact]
        public override void Properties_can_have_provider_type_set_for_type()
            => base.Properties_can_have_provider_type_set_for_type();

        [ConditionalFact]
        public override void Properties_can_have_value_converter_set()
            => base.Properties_can_have_value_converter_set();

        [ConditionalFact]
        public override void Properties_can_have_value_converter_set_inline()
            => base.Properties_can_have_value_converter_set_inline();

        [ConditionalFact]
        public override void Properties_specified_by_string_are_shadow_properties_unless_already_known_to_be_CLR_properties()
            => base.Properties_specified_by_string_are_shadow_properties_unless_already_known_to_be_CLR_properties();

        [ConditionalFact]
        public override void Value_converter_configured_on_non_nullable_type_is_applied()
            => base.Value_converter_configured_on_non_nullable_type_is_applied();

        [ConditionalFact]
        public override void Value_converter_configured_on_nullable_type_overrides_non_nullable()
            => base.Value_converter_configured_on_nullable_type_overrides_non_nullable();

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
