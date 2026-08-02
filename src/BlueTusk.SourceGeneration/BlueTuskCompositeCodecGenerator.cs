using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace BlueTusk.SourceGeneration;

[Generator(LanguageNames.CSharp)]
public sealed class BlueTuskCompositeCodecGenerator : IIncrementalGenerator
{
    private const string CompositeAttributeName = "BlueTusk.TypeSystem.BlueTuskCompositeAttribute";
    private const string NameAttributeName = "BlueTusk.TypeSystem.BlueTuskNameAttribute";

    private static readonly DiagnosticDescriptor PartialTypeRequired = new(
        "BTG001",
        "Composite type must be partial",
        "Type '{0}' must be partial so BlueTusk can emit its generated codec registration",
        "BlueTusk.SourceGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedTypeShape = new(
        "BTG002",
        "Composite type shape is unsupported",
        "Type '{0}' must be a non-abstract, top-level, non-generic class, record, or struct",
        "BlueTusk.SourceGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ConstructionUnavailable = new(
        "BTG003",
        "Composite type cannot be constructed",
        "Type '{0}' requires exactly one constructor matching all mapped members, or a parameterless constructor and assignable members",
        "BlueTusk.SourceGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateMemberName = new(
        "BTG004",
        "Composite members map to the same PostgreSQL name",
        "Type '{0}' maps multiple public members to PostgreSQL field '{1}'",
        "BlueTusk.SourceGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoMappedMembers = new(
        "BTG005",
        "Composite type has no mapped members",
        "Type '{0}' must expose at least one public readable instance property or field",
        "BlueTusk.SourceGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ReservedMemberName = new(
        "BTG006",
        "Generated member name is already declared",
        "Type '{0}' already declares reserved source-generation member '{1}'",
        "BlueTusk.SourceGeneration",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly SymbolDisplayFormat TypeDisplayFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var composites = context.SyntaxProvider.ForAttributeWithMetadataName(
            CompositeAttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (attributeContext, _) => (INamedTypeSymbol)attributeContext.TargetSymbol);

        context.RegisterSourceOutput(composites, static (productionContext, type) =>
            Generate(productionContext, type));
    }

    private static void Generate(SourceProductionContext context, INamedTypeSymbol type)
    {
        var location = type.Locations.FirstOrDefault(static candidate => candidate.IsInSource);
        if (type.ContainingType is not null ||
            type.TypeParameters.Length != 0 ||
            type.IsAbstract ||
            type.IsRefLikeType ||
            type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                UnsupportedTypeShape,
                location,
                type.ToDisplayString()));
            return;
        }

        var isPartial = type.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is TypeDeclarationSyntax declaration &&
            declaration.Modifiers.Any(SyntaxKind.PartialKeyword));
        if (!isPartial)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                PartialTypeRequired,
                location,
                type.ToDisplayString()));
            return;
        }

        foreach (var reserved in new[] { "BlueTuskGeneratedCodec", "RegisterBlueTuskCodec" })
        {
            if (type.GetMembers(reserved).Length != 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ReservedMemberName,
                    location,
                    type.ToDisplayString(),
                    reserved));
                return;
            }
        }

        var attribute = type.GetAttributes().Single(candidate =>
            candidate.AttributeClass?.ToDisplayString() == CompositeAttributeName);
        var schema = (string)attribute.ConstructorArguments[0].Value!;
        var name = (string)attribute.ConstructorArguments[1].Value!;
        var members = GetMembers(type);
        if (members.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                NoMappedMembers,
                location,
                type.ToDisplayString()));
            return;
        }

        var duplicate = members
            .GroupBy(static member => member.PostgreSqlName, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DuplicateMemberName,
                location,
                type.ToDisplayString(),
                duplicate.Key));
            return;
        }

        if (members.Any(static member =>
                member.Type.IsRefLikeType ||
                member.Type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ConstructionUnavailable,
                location,
                type.ToDisplayString()));
            return;
        }

        var constructors = type.InstanceConstructors
            .Where(constructor => ConstructorMatches(constructor, members))
            .ToImmutableArray();
        string factory;
        if (constructors.Length == 1)
        {
            factory = BuildConstructorFactory(type, constructors[0], members);
        }
        else if (constructors.Length == 0 &&
                 type.InstanceConstructors.Any(static constructor => constructor.Parameters.Length == 0) &&
                 members.All(static member => member.CanAssign))
        {
            factory = BuildInitializerFactory(type, members);
        }
        else
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ConstructionUnavailable,
                location,
                type.ToDisplayString()));
            return;
        }

        var source = BuildSource(type, schema, name, members, factory);
        var hintName = string.Concat(
            type.ToDisplayString().Select(static character =>
                char.IsLetterOrDigit(character) ? character : '_')) + ".BlueTuskCompositeCodec.g.cs";
        context.AddSource(hintName, SourceText.From(source, Encoding.UTF8));
    }

    private static ImmutableArray<MappedMember> GetMembers(INamedTypeSymbol type) =>
        type.GetMembers()
            .Where(static member => member switch
            {
                IPropertySymbol property =>
                    !property.IsStatic &&
                    property.DeclaredAccessibility == Accessibility.Public &&
                    property.GetMethod is not null &&
                    property.Parameters.Length == 0,
                IFieldSymbol field =>
                    !field.IsStatic &&
                    !field.IsConst &&
                    field.DeclaredAccessibility == Accessibility.Public,
                _ => false,
            })
            .OrderBy(static member => member.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue)
            .Select(CreateMappedMember)
            .ToImmutableArray();

    private static MappedMember CreateMappedMember(ISymbol symbol)
    {
        var nameAttribute = symbol.GetAttributes().SingleOrDefault(candidate =>
            candidate.AttributeClass?.ToDisplayString() == NameAttributeName);
        var postgreSqlName = nameAttribute is null
            ? ToSnakeCase(symbol.Name)
            : (string)nameAttribute.ConstructorArguments[0].Value!;
        return symbol switch
        {
            IPropertySymbol property => new MappedMember(
                property.Name,
                postgreSqlName,
                property.Type,
                property.SetMethod is not null),
            IFieldSymbol field => new MappedMember(
                field.Name,
                postgreSqlName,
                field.Type,
                !field.IsReadOnly),
            _ => throw new InvalidOperationException("Unsupported generated composite member."),
        };
    }

    private static bool ConstructorMatches(
        IMethodSymbol constructor,
        ImmutableArray<MappedMember> members)
    {
        if (constructor.Parameters.Length != members.Length)
        {
            return false;
        }

        var memberLookup = members.ToDictionary(
            static member => member.PostgreSqlName,
            StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in constructor.Parameters)
        {
            var nameAttribute = parameter.GetAttributes().SingleOrDefault(candidate =>
                candidate.AttributeClass?.ToDisplayString() == NameAttributeName);
            var postgreSqlName = nameAttribute is null
                ? ToSnakeCase(parameter.Name)
                : (string)nameAttribute.ConstructorArguments[0].Value!;
            if (!memberLookup.TryGetValue(postgreSqlName, out var member) ||
                !used.Add(postgreSqlName) ||
                !SymbolEqualityComparer.Default.Equals(parameter.Type, member.Type))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildConstructorFactory(
        INamedTypeSymbol type,
        IMethodSymbol constructor,
        ImmutableArray<MappedMember> members)
    {
        var indexes = members
            .Select((member, index) => (member.PostgreSqlName, index))
            .ToDictionary(static item => item.PostgreSqlName, static item => item.index, StringComparer.Ordinal);
        var arguments = constructor.Parameters.Select(parameter =>
        {
            var nameAttribute = parameter.GetAttributes().SingleOrDefault(candidate =>
                candidate.AttributeClass?.ToDisplayString() == NameAttributeName);
            var postgreSqlName = nameAttribute is null
                ? ToSnakeCase(parameter.Name)
                : (string)nameAttribute.ConstructorArguments[0].Value!;
            return CastValue(parameter.Type, indexes[postgreSqlName]);
        });
        return $"static values => new {type.ToDisplayString(TypeDisplayFormat)}({string.Join(", ", arguments)})";
    }

    private static string BuildInitializerFactory(
        INamedTypeSymbol type,
        ImmutableArray<MappedMember> members)
    {
        var assignments = members.Select((member, index) =>
            $"{EscapeIdentifier(member.ClrName)} = {CastValue(member.Type, index)}");
        return $"static values => new {type.ToDisplayString(TypeDisplayFormat)} {{ {string.Join(", ", assignments)} }}";
    }

    private static string BuildSource(
        INamedTypeSymbol type,
        string schema,
        string name,
        ImmutableArray<MappedMember> members,
        string factory)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("#pragma warning disable CS1591");
        if (!type.ContainingNamespace.IsGlobalNamespace)
        {
            builder.Append("namespace ")
                .Append(type.ContainingNamespace.ToDisplayString())
                .AppendLine(";")
                .AppendLine();
        }

        builder.Append(GetAccessibility(type.DeclaredAccessibility)).Append(' ');
        if (type.IsReadOnly)
        {
            builder.Append("readonly ");
        }

        builder.Append("partial ")
            .Append(GetTypeKeyword(type))
            .Append(' ')
            .Append(EscapeIdentifier(type.Name))
            .AppendLine()
            .AppendLine("{")
            .Append("    public static global::BlueTusk.TypeSystem.IBlueTuskCodec<")
            .Append(type.ToDisplayString(TypeDisplayFormat))
            .AppendLine("> BlueTuskGeneratedCodec { get; } =")
            .Append("        new global::BlueTusk.TypeSystem.BlueTuskCompositeCodec<")
            .Append(type.ToDisplayString(TypeDisplayFormat))
            .AppendLine(">(")
            .Append("            new global::BlueTusk.TypeSystem.BlueTuskCompositeMapping<")
            .Append(type.ToDisplayString(TypeDisplayFormat))
            .AppendLine(">(")
            .AppendLine("                [");
        foreach (var member in members)
        {
            builder.Append("                    global::BlueTusk.TypeSystem.BlueTuskCompositeMember.Create<")
                .Append(type.ToDisplayString(TypeDisplayFormat))
                .Append(", ")
                .Append(member.Type.ToDisplayString(TypeDisplayFormat))
                .Append(">(")
                .Append(SymbolDisplay.FormatLiteral(member.PostgreSqlName, quote: true))
                .Append(", static value => value.")
                .Append(EscapeIdentifier(member.ClrName))
                .AppendLine("),");
        }

        builder.AppendLine("                ],")
            .Append("                ")
            .Append(factory)
            .AppendLine("));")
            .AppendLine()
            .AppendLine("    public static global::BlueTusk.TypeSystem.BlueTuskTypeRegistryBuilder RegisterBlueTuskCodec(")
            .AppendLine("        global::BlueTusk.TypeSystem.BlueTuskTypeRegistryBuilder types)")
            .AppendLine("    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(types);")
            .Append("        return types.Register(")
            .Append(SymbolDisplay.FormatLiteral(schema, quote: true))
            .Append(", ")
            .Append(SymbolDisplay.FormatLiteral(name, quote: true))
            .AppendLine(", BlueTuskGeneratedCodec);")
            .AppendLine("    }")
            .AppendLine("}");
        return builder.ToString();
    }

    private static string CastValue(ITypeSymbol type, int index) =>
        $"({type.ToDisplayString(TypeDisplayFormat)})values[{index}]!";

    private static string GetAccessibility(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        _ => "internal",
    };

    private static string GetTypeKeyword(INamedTypeSymbol type) =>
        type.IsRecord
            ? type.TypeKind == TypeKind.Struct ? "record struct" : "record"
            : type.TypeKind == TypeKind.Struct ? "struct" : "class";

    private static string EscapeIdentifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;

    private static string ToSnakeCase(string name)
    {
        var result = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character) && index != 0 &&
                (char.IsLower(name[index - 1]) ||
                 char.IsDigit(name[index - 1]) ||
                 index + 1 < name.Length && char.IsLower(name[index + 1])))
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }

    private sealed class MappedMember
    {
        public MappedMember(string clrName, string postgreSqlName, ITypeSymbol type, bool canAssign)
        {
            ClrName = clrName;
            PostgreSqlName = postgreSqlName;
            Type = type;
            CanAssign = canAssign;
        }

        public string ClrName { get; }

        public string PostgreSqlName { get; }

        public ITypeSymbol Type { get; }

        public bool CanAssign { get; }
    }
}
