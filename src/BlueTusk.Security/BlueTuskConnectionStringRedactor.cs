using System.Text;

namespace BlueTusk.Security;

/// <summary>Produces a diagnostic-safe representation of a connection string.</summary>
public static class BlueTuskConnectionStringRedactor
{
    private const string RedactedValue = "<redacted>";

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password",
        "Pwd",
        "Passfile",
        "Access Token",
        "AccessToken",
        "Token",
        "Client Certificate Key",
        "ClientCertificateKey",
    };

    public static string Redact(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        try
        {
            return string.Join(';', Parse(connectionString).Select(setting =>
                $"{setting.Key}={FormatValue(SensitiveKeys.Contains(setting.Key) ? RedactedValue : setting.Value)}"));
        }
        catch (ArgumentException)
        {
            return "<redacted connection string>";
        }
    }

    private static Dictionary<string, string> Parse(string connectionString)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < connectionString.Length)
        {
            while (index < connectionString.Length
                   && (connectionString[index] == ';' || char.IsWhiteSpace(connectionString[index])))
            {
                index++;
            }

            if (index == connectionString.Length)
            {
                break;
            }

            var keyStart = index;
            while (index < connectionString.Length && connectionString[index] is not ('=' or ';'))
            {
                index++;
            }

            if (index == connectionString.Length || connectionString[index] != '=')
            {
                throw new ArgumentException("Invalid connection string.", nameof(connectionString));
            }

            var key = connectionString[keyStart..index].Trim();
            if (key.Length == 0)
            {
                throw new ArgumentException("Invalid connection string.", nameof(connectionString));
            }

            index++;
            while (index < connectionString.Length && char.IsWhiteSpace(connectionString[index]))
            {
                index++;
            }

            settings[key] = index < connectionString.Length && connectionString[index] is ('\'' or '"')
                ? ReadQuotedValue(connectionString, ref index)
                : ReadUnquotedValue(connectionString, ref index);
        }

        return settings;
    }

    private static string ReadQuotedValue(string connectionString, ref int index)
    {
        var quote = connectionString[index++];
        var value = new StringBuilder();
        while (index < connectionString.Length)
        {
            var character = connectionString[index++];
            if (character != quote)
            {
                value.Append(character);
                continue;
            }

            if (index < connectionString.Length && connectionString[index] == quote)
            {
                value.Append(quote);
                index++;
                continue;
            }

            while (index < connectionString.Length && char.IsWhiteSpace(connectionString[index]))
            {
                index++;
            }

            if (index < connectionString.Length && connectionString[index] != ';')
            {
                throw new ArgumentException("Invalid connection string.", nameof(connectionString));
            }

            if (index < connectionString.Length)
            {
                index++;
            }

            return value.ToString();
        }

        throw new ArgumentException("Invalid connection string.", nameof(connectionString));
    }

    private static string ReadUnquotedValue(string connectionString, ref int index)
    {
        var valueStart = index;
        while (index < connectionString.Length && connectionString[index] != ';')
        {
            index++;
        }

        var value = connectionString[valueStart..index].Trim();
        if (index < connectionString.Length)
        {
            index++;
        }

        return value;
    }

    private static string FormatValue(string value)
    {
        if (value.Length == 0
            || char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || value.IndexOfAny([';', '\'', '"']) >= 0)
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }
}
