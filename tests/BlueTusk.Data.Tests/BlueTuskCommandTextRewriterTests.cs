namespace BlueTusk.Data.Tests;

public sealed class BlueTuskCommandTextRewriterTests
{
    [Theory]
    [InlineData("SELECT $1::int4", false)]
    [InlineData("SELECT value::text", false)]
    [InlineData("SELECT @value", true)]
    [InlineData("SELECT :value", true)]
    [InlineData("SELECT ':conservative'", true)]
    public void MightContainNamedParametersConservativelyIdentifiesMarkers(
        string sql,
        bool expected)
    {
        Assert.Equal(expected, BlueTuskCommandTextRewriter.MightContainNamedParameters(sql));
    }

    [Fact]
    public void Reuses_parameterless_plans_for_the_same_command_text()
    {
        const string sql = "SELECT 42";

        var first = BlueTuskCommandTextRewriter.Rewrite(sql, new BlueTuskParameterCollection());
        var second = BlueTuskCommandTextRewriter.Rewrite(sql, new BlueTuskParameterCollection());

        Assert.Equal(first, second);
        Assert.Empty(first.Parameters);
    }

    [Fact]
    public void Rewrites_named_parameters_in_first_use_order_and_reuses_ordinals()
    {
        var parameters = new BlueTuskParameterCollection();
        var right = parameters.Add(new BlueTuskParameter<int>(22) { ParameterName = ":right" });
        var left = parameters.Add(new BlueTuskParameter<int>(20) { ParameterName = "@left" });

        var plan = BlueTuskCommandTextRewriter.Rewrite(
            "SELECT @left + :right + @LEFT",
            parameters);

        Assert.Equal("SELECT $1 + $2 + $1", plan.Sql);
        Assert.Equal([left, right], plan.Parameters);
        Assert.True(plan.UsesNamedParameters);
    }

    [Fact]
    public void Reuses_named_templates_for_equal_command_text_instances()
    {
        const string commandText = "SELECT @left + @right";
        var firstText = new string(commandText.ToCharArray());
        var secondText = new string(commandText.ToCharArray());
        var firstParameters = new BlueTuskParameterCollection();
        firstParameters.Add(new BlueTuskParameter<int>(20) { ParameterName = "left" });
        firstParameters.Add(new BlueTuskParameter<int>(22) { ParameterName = "right" });
        var secondParameters = new BlueTuskParameterCollection();
        var secondLeft = secondParameters.Add(
            new BlueTuskParameter<int>(40) { ParameterName = "left" });
        var secondRight = secondParameters.Add(
            new BlueTuskParameter<int>(2) { ParameterName = "right" });

        var firstPlan = BlueTuskCommandTextRewriter.Rewrite(firstText, firstParameters);
        var secondPlan = BlueTuskCommandTextRewriter.Rewrite(secondText, secondParameters);

        Assert.Same(firstPlan.Sql, secondPlan.Sql);
        Assert.Equal([secondLeft, secondRight], secondPlan.Parameters);
    }

    [Fact]
    public void Ignores_parameter_shaped_text_in_postgresql_lexical_regions()
    {
        var parameters = new BlueTuskParameterCollection();
        var actual = parameters.Add(new BlueTuskParameter<int>(42) { ParameterName = "actual" });
        const string sql = """
            SELECT '@ignored', E'\\@ignored', "@ignored", $$:ignored$$,
                   $body$@ignored$body$, value::text, data @> '{}', 1 /* :ignored /* @ignored */ */
            -- @ignored
            WHERE value = :actual
            """;

        var plan = BlueTuskCommandTextRewriter.Rewrite(sql, parameters);

        Assert.EndsWith("WHERE value = $1", plan.Sql, StringComparison.Ordinal);
        Assert.Contains("value::text", plan.Sql, StringComparison.Ordinal);
        Assert.Contains("data @> '{}'", plan.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(":actual", plan.Sql, StringComparison.Ordinal);
        Assert.Same(actual, Assert.Single(plan.Parameters));
    }

    [Theory]
    [InlineData("SELECT $1, @value")]
    [InlineData("SELECT @value, $1")]
    public void Rejects_mixed_positional_and_named_parameters(string sql)
    {
        var parameters = new BlueTuskParameterCollection();
        parameters.Add(new BlueTuskParameter<int>(42) { ParameterName = "value" });

        var exception = Assert.Throws<InvalidOperationException>(
            () => BlueTuskCommandTextRewriter.Rewrite(sql, parameters));

        Assert.Contains("cannot mix", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_missing_and_duplicate_named_parameters()
    {
        var missing = new BlueTuskParameterCollection();
        var duplicate = new BlueTuskParameterCollection();
        duplicate.Add(new BlueTuskParameter<int>(1) { ParameterName = "value" });
        duplicate.Add(new BlueTuskParameter<int>(2) { ParameterName = "@VALUE" });

        Assert.Throws<InvalidOperationException>(
            () => BlueTuskCommandTextRewriter.Rewrite("SELECT @missing", missing));
        Assert.Throws<InvalidOperationException>(
            () => BlueTuskCommandTextRewriter.Rewrite("SELECT @value", duplicate));
    }

    [Fact]
    public void Leaves_positional_commands_and_parameter_order_unchanged()
    {
        var parameters = new BlueTuskParameterCollection();
        var first = parameters.Add(new BlueTuskParameter<int>(20));
        var second = parameters.Add(new BlueTuskParameter<int>(22));

        var plan = BlueTuskCommandTextRewriter.Rewrite("SELECT $1 + $2", parameters);

        Assert.Equal("SELECT $1 + $2", plan.Sql);
        Assert.Equal([first, second], plan.Parameters);
        Assert.False(plan.UsesNamedParameters);
    }

    [Fact]
    public void Cached_positional_template_binds_each_parameter_collection_independently()
    {
        const string sql = "SELECT $1::int4";
        var firstParameters = new BlueTuskParameterCollection();
        var first = firstParameters.Add(new BlueTuskParameter<int>(41));
        var secondParameters = new BlueTuskParameterCollection();
        var second = secondParameters.Add(new BlueTuskParameter<int>(42));

        var firstPlan = BlueTuskCommandTextRewriter.Rewrite(sql, firstParameters);
        var secondPlan = BlueTuskCommandTextRewriter.Rewrite(sql, secondParameters);

        Assert.Same(first, Assert.Single(firstPlan.Parameters));
        Assert.Same(second, Assert.Single(secondPlan.Parameters));
        Assert.NotSame(firstPlan.Parameters, secondPlan.Parameters);
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT ';'::text")]
    [InlineData("SELECT $$;$$::text")]
    [InlineData("SELECT 1 /* ; SELECT 2 */; -- trailing comment")]
    public void Single_statements_can_use_the_extended_protocol(string sql) =>
        Assert.True(BlueTuskCommandTextRewriter.CanUseExtendedProtocol(sql));

    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1; /* separator */ SELECT 2")]
    [InlineData("; SELECT 1")]
    public void Multiple_statements_stay_on_the_buffered_simple_query_path(string sql) =>
        Assert.False(BlueTuskCommandTextRewriter.CanUseExtendedProtocol(sql));
}
