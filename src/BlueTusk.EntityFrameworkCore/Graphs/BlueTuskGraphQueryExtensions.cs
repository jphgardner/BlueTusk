namespace Microsoft.EntityFrameworkCore;

using BlueTusk.EntityFrameworkCore.Graphs;

/// <summary>Typed SQL/PGQ query roots for BlueTusk EF contexts.</summary>
public static class BlueTuskGraphQueryExtensions
{
    public static BlueTuskGraphQueryRoot PropertyGraph(
        this DbContext context,
        string name,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var matches = context.Model.GetBlueTuskPropertyGraphs()
            .Where(graph =>
                string.Equals(graph.Name, name, StringComparison.Ordinal) &&
                (schema is null || string.Equals(graph.Schema, schema, StringComparison.Ordinal)))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new BlueTuskGraphTranslationException(
                matches.Length == 0
                    ? $"Property graph '{name}' is not configured in the EF model."
                    : $"Property graph name '{name}' is ambiguous; specify its schema.");
        }

        return new BlueTuskGraphQueryRoot(context, matches[0]);
    }
}
