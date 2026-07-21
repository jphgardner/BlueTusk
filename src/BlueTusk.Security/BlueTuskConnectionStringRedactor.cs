using System.Data.Common;

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
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (var key in builder.Keys.Cast<string>().Where(SensitiveKeys.Contains).ToArray())
            {
                builder[key] = RedactedValue;
            }

            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return "<redacted connection string>";
        }
    }
}

