using System.Text;

namespace BlueTusk.EntityFrameworkCore.Storage;

/// <summary>Provides PostgreSQL identifier delimiting for provider extensions.</summary>
public static class BlueTuskSqlIdentifier
{
    /// <summary>Delimits one PostgreSQL identifier.</summary>
    public static string Delimit(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>Delimits a possibly schema-qualified PostgreSQL identifier.</summary>
    public static string Delimit(string identifier, string? schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return schema is null ? Delimit(identifier) : $"{Delimit(schema)}.{Delimit(identifier)}";
    }

    internal static void Append(StringBuilder builder, string identifier)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append(Delimit(identifier));
    }

    internal static void Append(StringBuilder builder, string identifier, string? schema)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append(Delimit(identifier, schema));
    }
}
