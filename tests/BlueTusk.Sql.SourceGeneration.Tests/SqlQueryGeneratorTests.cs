using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace BlueTusk.Sql.SourceGeneration.Tests;

public sealed class SqlQueryGeneratorTests
{
    private const string Valid = """
        -- bluetusk-query: Example.FindOrders
        -- bluetusk-param: tenant text required
        -- bluetusk-result: Id int8 required
        -- bluetusk-result: Label text nullable
        -- bluetusk-max-rows: 50
        SELECT id AS "Id", label AS "Label" FROM orders WHERE tenant_id = $1;
        """;

    [Fact]
    public void Typed_SQL_generation_is_deterministic_and_reflection_free()
    {
        var first = Run(new SqlText("orders.sql", Valid));
        var second = Run(new SqlText("orders.sql", Valid));
        Assert.Empty(first.Diagnostics);
        var generated = Assert.Single(Assert.Single(first.Results).GeneratedSources).SourceText.ToString();
        Assert.Equal(generated, Assert.Single(Assert.Single(second.Results).GeneratedSources).SourceText.ToString());
        Assert.Contains("BlueTuskParameter<string>", generated, StringComparison.Ordinal);
        Assert.Contains("GetFieldValue<long>", generated, StringComparison.Ordinal);
        Assert.Contains("maximumRows: 50", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("Reflection", generated, StringComparison.Ordinal);
        Assert.DoesNotContain(CSharpSyntaxTree.ParseText(generated).GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("-- bluetusk-param: tenant text required", "-- bluetusk-param: tenant unsafe_type required")]
    [InlineData("-- bluetusk-param: tenant text required", "-- bluetusk-param: class text required")]
    [InlineData("-- bluetusk-result: Id int8 required", "-- bluetusk-result: Equals int8 required")]
    [InlineData("-- bluetusk-result: Id int8 required", "-- bluetusk-result: Row int8 required")]
    [InlineData("-- bluetusk-max-rows: 50", "-- bluetusk-max-rows: 0")]
    [InlineData("-- bluetusk-max-rows: 50", "-- bluetusk-max-rows: 1000001")]
    [InlineData("-- bluetusk-result: Label text nullable", "-- bluetusk-result: Label text unknown")]
    [InlineData("Example.FindOrders", "Example.class")]
    public void Invalid_contracts_fail_at_compile_time(string from, string to)
    {
        var result = Run(new SqlText("invalid.sql", Valid.Replace(from, to, StringComparison.Ordinal)));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "BTS001" && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Empty(Assert.Single(result.Results).GeneratedSources);
    }

    [Fact]
    public void Duplicate_query_identities_are_rejected_instead_of_overwriting_generated_source()
    {
        var result = Run(new SqlText("first.sql", Valid), new SqlText("second.sql", Valid));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "BTS002");
        Assert.Empty(Assert.Single(result.Results).GeneratedSources);
    }

    [Fact]
    public void Directives_inside_SQL_body_are_rejected_instead_of_changing_SQL_literal_contents()
    {
        var sql = Valid + "\nSELECT $body$\n-- bluetusk-result: Hidden text nullable\n$body$;";
        Assert.Contains(Run(new SqlText("body.sql", sql)).Diagnostics, diagnostic => diagnostic.Id == "BTS001");
    }

    [Fact]
    public void Unannotated_SQL_files_are_ignored()
    {
        var result = Run(new SqlText("migration.sql", "CREATE TABLE orders(id bigint)"));
        Assert.Empty(result.Diagnostics);
        Assert.Empty(Assert.Single(result.Results).GeneratedSources);
    }

    [Fact]
    public void Duplicate_members_and_oversized_SQL_are_rejected()
    {
        Assert.Contains(Run(new SqlText("duplicate.sql", Valid.Replace("-- bluetusk-result: Label text nullable",
            "-- bluetusk-result: Id text nullable", StringComparison.Ordinal))).Diagnostics, diagnostic => diagnostic.Id == "BTS001");
        Assert.Contains(Run(new SqlText("large.sql", Valid + new string(' ', 1024 * 1024))).Diagnostics, diagnostic => diagnostic.Id == "BTS001");
    }

    [Theory]
    [InlineData("SELECT $2 AS \"Id\"")]
    [InlineData("SELECT $0 AS \"Id\"")]
    [InlineData("SELECT '$1' AS \"Id\"")]
    [InlineData("SELECT $$ $1 $$ AS \"Id\"")]
    [InlineData("SELECT $tag$ $1 $tag$ AS \"Id\"")]
    [InlineData("SELECT 1 AS \"Id\" /* nested /* $1 */ comment */")]
    [InlineData("SELECT 1 AS \"$1\"")]
    [InlineData("SELECT 1 AS \"Id\" -- $1")]
    [InlineData("SELECT $1unexpected AS \"Id\"")]
    [InlineData("SELECT $999999999 AS \"Id\"")]
    public void Declared_parameters_must_match_actual_positional_references(string body)
    {
        var sql = "-- bluetusk-query: Example.Count\n-- bluetusk-param: value text required\n-- bluetusk-result: Id int8 required\n" + body;
        Assert.Contains(Run(new SqlText("count.sql", sql)).Diagnostics, diagnostic => diagnostic.Id == "BTS001");
    }

    [Fact]
    public void Repeated_contiguous_parameters_and_literal_comment_dollars_are_admitted()
    {
        var sql = "-- bluetusk-query: Example.Count\n-- bluetusk-param: first text required\n-- bluetusk-param: second text nullable\n-- bluetusk-result: Id text required\n" +
            "SELECT $1 || $1 || $2 || '$99' || $body$ $88 $body$ || E'quoted\\\'$77' AS \"Id\" /* $66 /* $55 */ */ -- $44";
        Assert.Empty(Run(new SqlText("count.sql", sql)).Diagnostics);
    }

    [Theory]
    [InlineData("enum:status")]
    [InlineData("enum:app.Status")]
    [InlineData("enum:app.status?[]")]
    [InlineData("domain:app.amount:geometry")]
    [InlineData("domain:app.amount:int4?[]")]
    [InlineData("domain:app.amount:time[]")]
    public void Unsupported_or_ambiguous_catalogue_type_annotations_fail_explicitly(string annotation)
    {
        var sql = Valid.Replace("-- bluetusk-result: Id int8 required",
            "-- bluetusk-result: Id " + annotation + " required", StringComparison.Ordinal);
        var result = Run(new SqlText("unsupported.sql", sql));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "BTS001" &&
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("supported catalogue types", StringComparison.Ordinal));
        Assert.Empty(Assert.Single(result.Results).GeneratedSources);
    }

    [Fact]
    public void Catalogue_bindings_require_required_validation_and_a_catalogue_snapshot()
    {
        var sql = Valid.Replace("-- bluetusk-result: Id int8 required",
            "-- bluetusk-result: Id enum:app.status required", StringComparison.Ordinal);
        Assert.Contains(Run(new SqlText("enum.sql", sql)).Diagnostics,
            diagnostic => diagnostic.Id == "BTS001" && diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("attested catalogue", StringComparison.Ordinal));
        sql = sql.Replace("-- bluetusk-max-rows: 50", "-- bluetusk-validation: required\n-- bluetusk-max-rows: 50", StringComparison.Ordinal);
        Assert.Contains(Run(new SqlText("enum.sql", sql), new SqlText("app.bluetusk-schema.json", "{}"))
            .Diagnostics, diagnostic => diagnostic.Id == "BTS001");
        Assert.Contains(Run(new SqlText("enum.sql", sql), new SqlText("app.bluetusk-catalog.json", "{}"))
            .Diagnostics, diagnostic => diagnostic.Id == "BTS003");
    }

    private static GeneratorDriverRunResult Run(params AdditionalText[] files)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new SqlQueryGenerator().AsSourceGenerator()], files,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        driver = driver.RunGenerators(CSharpCompilation.Create("GenerationTests"));
        return driver.GetRunResult();
    }

    private sealed class SqlText(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text, Encoding.UTF8);
    }
}
