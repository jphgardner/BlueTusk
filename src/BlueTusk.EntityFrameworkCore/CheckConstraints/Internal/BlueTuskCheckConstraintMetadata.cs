using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BlueTusk.EntityFrameworkCore.CheckConstraints.Internal;

internal static class BlueTuskCheckConstraintMetadata
{
    public const string ScaffoldAnnotationName = "BlueTusk:CheckConstraints";
    public const string Prefix = "BlueTusk:CheckConstraint:";
    public const string NotValidAnnotationName = Prefix + "NotValid";
    public const string NoInheritAnnotationName = Prefix + "NoInherit";
    public const string NotEnforcedAnnotationName = Prefix + "NotEnforced";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(IEnumerable<BlueTuskCheckConstraintDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var normalized = definitions
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();
        Validate(normalized);
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    public static IReadOnlyList<BlueTuskCheckConstraintDefinition> Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var definitions = JsonSerializer.Deserialize<BlueTuskCheckConstraintDefinition[]>(json, SerializerOptions)
            ?? throw new ArgumentException("The CHECK-constraint definition set is empty.", nameof(json));
        Validate(definitions);
        return definitions.OrderBy(definition => definition.Name, StringComparer.Ordinal).ToArray();
    }

    public static bool IsNotValid(IReadOnlyAnnotatable annotatable) =>
        annotatable.FindAnnotation(NotValidAnnotationName)?.Value as bool? == true;

    public static bool HasNoInherit(IReadOnlyAnnotatable annotatable) =>
        annotatable.FindAnnotation(NoInheritAnnotationName)?.Value as bool? == true;

    public static bool IsNotEnforced(IReadOnlyAnnotatable annotatable) =>
        annotatable.FindAnnotation(NotEnforcedAnnotationName)?.Value as bool? == true;

    private static void Validate(IEnumerable<BlueTuskCheckConstraintDefinition> definitions)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Sql);
            if (!names.Add(definition.Name))
            {
                throw new ArgumentException(
                    $"CHECK constraint '{definition.Name}' is configured more than once.",
                    nameof(definitions));
            }
        }
    }
}
