using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using static BlueTusk.Sql.SourceGeneration.SqlContractModel;

namespace BlueTusk.Sql.SourceGeneration;

[Generator(LanguageNames.CSharp)]
public sealed class SqlQueryGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidContract = new("BTS001", "Invalid typed SQL contract",
        "Typed SQL contract is invalid: {0}", "BlueTusk.Sql", DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor DuplicateQuery = new("BTS002", "Duplicate typed query identity",
        "Query '{0}' is declared by more than one SQL file", "BlueTusk.Sql", DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor InvalidValidation = new("BTS003", "Missing or stale SQL schema validation",
        "Typed SQL validation is missing, stale or invalid: {0}", "BlueTusk.Sql", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var sources = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ||
                file.Path.EndsWith(".bluetusk-sql.xml", StringComparison.OrdinalIgnoreCase) ||
                IsSnapshot(file.Path))
            .Select(static (file, cancellationToken) => new SqlSource(file.Path, file.GetText(cancellationToken)?.ToString() ?? string.Empty))
            .Collect();
        context.RegisterSourceOutput(sources, static (output, files) => Generate(output, files));
    }

    private static void Generate(SourceProductionContext output, ImmutableArray<SqlSource> files)
    {
        if (files.Length > 1017 || files.Count(file => file.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) > 1000 ||
            files.Where(file => file.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).Sum(file => (long)Encoding.UTF8.GetByteCount(file.Text)) > 16 * 1024 * 1024)
        {
            output.ReportDiagnostic(Diagnostic.Create(InvalidContract, Location.None, "at most 1000 SQL files are supported per compilation"));
            return;
        }

        Dictionary<string, SqlValidationDocument.Entry> validations;
        try { validations = SqlValidationDocument.Read(files.Where(file => file.Path.EndsWith(".bluetusk-sql.xml", StringComparison.OrdinalIgnoreCase))); }
        catch (Exception exception) when (exception is FormatException or System.Xml.XmlException or ArgumentException)
        {
            output.ReportDiagnostic(Diagnostic.Create(InvalidValidation, Location.None, "validation document contract"));
            return;
        }
        var queries = new List<Query>();
        var snapshots = files.Where(file => IsSnapshot(file.Path)).ToArray();
        if (snapshots.Length > 1 || snapshots.Any(file => file.Text.Length > 64 * 1024 * 1024 || Encoding.UTF8.GetByteCount(file.Text) > 64 * 1024 * 1024))
        {
            output.ReportDiagnostic(Diagnostic.Create(InvalidValidation, Location.None, "one bounded schema snapshot is supported"));
            return;
        }
        var snapshotFingerprint = snapshots.Length == 1 ? DocumentFingerprint(snapshots[0].Text) : null;
        foreach (var file in files.Where(file => file.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            // Other SQL files in a project need not be typed queries.
            if (file.Text.IndexOf("-- bluetusk-query:", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            var query = Parse(file, out var error);
            if (query is null)
            {
                output.ReportDiagnostic(Diagnostic.Create(InvalidContract, Location.Create(file.Path,
                    new TextSpan(0, 0), new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0))), error));
                continue;
            }

            queries.Add(query);
        }

        foreach (var group in queries.GroupBy(query => query.Identity, StringComparer.Ordinal))
        {
            if (group.Count() != 1)
            {
                output.ReportDiagnostic(Diagnostic.Create(DuplicateQuery, Location.None, group.Key));
                continue;
            }

            var query = group.Single();
            if (query.UsesCatalogueTypes && (!query.RequiresValidation || snapshots.Length != 1 ||
                    !snapshots[0].Path.EndsWith(".bluetusk-catalog.json", StringComparison.OrdinalIgnoreCase)))
            {
                output.ReportDiagnostic(Diagnostic.Create(InvalidContract, Location.None,
                    "domain and enum bindings require 'bluetusk-validation: required' and one attested catalogue snapshot"));
                continue;
            }
            validations.TryGetValue(query.Identity, out var validation);
            var matches = validation is not null && validation.QueryFingerprint == Fingerprint(query) &&
                validation.SnapshotFingerprint == snapshotFingerprint;
            if (query.RequiresValidation && !matches)
            {
                output.ReportDiagnostic(Diagnostic.Create(InvalidValidation, Location.None, query.Identity));
                continue;
            }
            output.AddSource(query.Identity + ".g.cs", SourceText.From(Emit(query,
                matches ? validation!.SchemaFingerprint : null), Encoding.UTF8));
        }
    }

    private static bool IsSnapshot(string path) => path.EndsWith(".bluetusk-schema.json", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bluetusk-catalog.json", StringComparison.OrdinalIgnoreCase);

    private static string Emit(Query query, string? schemaFingerprint)
    {
        var dot = query.Identity.LastIndexOf('.');
        var name = dot < 0 ? query.Identity : query.Identity.Substring(dot + 1);
        var source = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (dot >= 0)
        {
            source.Append("namespace ").Append(query.Identity.Substring(0, dot)).Append(";\n");
        }

        source.Append("public static class ").Append(name).Append("\n{\n");
        source.Append("    public const string? ValidatedSchemaFingerprint = ").Append(schemaFingerprint is null ? "null" : Literal(schemaFingerprint)).Append(";\n");
        source.Append("    public sealed record Arguments(").Append(string.Join(", ", query.Parameters.Select(member => member.ClrType + " " + member.Name))).Append(");\n");
        source.Append("    public sealed record Row(").Append(string.Join(", ", query.Columns.Select(member => member.ClrType + " " + member.Name))).Append(");\n");
        source.Append("    public static global::BlueTusk.Sql.SqlQuery<Arguments, Row> Definition { get; } = new(\n        ")
            .Append(Literal(query.Identity)).Append(",\n        ").Append(Literal(query.Sql)).Append(",\n        new global::BlueTusk.Sql.SqlResultColumn[] {\n");
        foreach (var column in query.Columns)
        {
            source.Append("            new(").Append(Literal(column.Name)).Append(", ").Append(Literal(column.Type.PostgresName))
                .Append(", ").Append(column.Nullable ? "true" : "false").Append("),\n");
        }

        source.Append("        },\n        static (command, arguments) =>\n        {\n");
        source.Append("            global::System.ArgumentNullException.ThrowIfNull(arguments);\n");
        foreach (var parameter in query.Parameters)
        {
            if (!parameter.Nullable && (parameter.Type.ClrName == "string" || parameter.Type.ClrName.EndsWith("]", StringComparison.Ordinal)))
            {
                source.Append("            if (arguments.").Append(parameter.Name).Append(" is null) { throw new global::System.ArgumentException(\"Required typed SQL arguments cannot be null.\", nameof(arguments)); }\n");
            }
            source.Append("            command.Parameters.Add(new global::BlueTusk.Data.BlueTuskParameter<")
                .Append(parameter.ClrType).Append(">(arguments.").Append(parameter.Name)
                .Append(") { ");
            if (parameter.Type.QualifiedName is { })
            {
                source.Append("PostgreSqlTypeName = ").Append(Literal(parameter.Type.ParameterTypeName));
            }
            else
            {
                source.Append("PostgreSqlTypeOid = ").Append(parameter.Type.Oid.ToString(CultureInfo.InvariantCulture)).Append('u');
            }
            source.Append(" });\n");
        }

        source.Append("        },\n        static reader => new Row(\n");
        for (var index = 0; index < query.Columns.Count; index++)
        {
            var column = query.Columns[index];
            source.Append("            ");
            if (column.Nullable)
            {
                source.Append("reader.IsDBNull(").Append(index.ToString(CultureInfo.InvariantCulture)).Append(") ? (")
                    .Append(column.ClrType).Append(")null : ");
            }

            source.Append("reader.GetFieldValue<").Append(column.Type.ClrName).Append(">(").Append(index.ToString(CultureInfo.InvariantCulture)).Append(')')
                .Append(index == query.Columns.Count - 1 ? "),\n" : ",\n");
        }

        source.Append("        maximumRows: ").Append(query.MaximumRows.ToString(CultureInfo.InvariantCulture)).Append(");\n}\n");
        return source.ToString();
    }

    private static string Literal(string text) => SymbolDisplay.FormatLiteral(text, quote: true);

}
