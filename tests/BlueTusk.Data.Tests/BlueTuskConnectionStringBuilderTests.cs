using BlueTusk.Security;

namespace BlueTusk.Data.Tests;

public sealed class BlueTuskConnectionStringBuilderTests
{
    [Fact]
    public void Uses_safe_defaults()
    {
        var builder = new BlueTuskConnectionStringBuilder();

        Assert.Equal("localhost", builder.Host);
        Assert.Equal(5432, builder.Port);
        Assert.True(builder.Pooling);
        Assert.Equal(100, builder.MaximumPoolSize);
    }

    [Fact]
    public void Redactor_removes_passwords_and_tokens()
    {
        const string connectionString =
            "Host=db.example;Username=app;Password=top-secret;Access Token=also-secret";

        var redacted = BlueTuskConnectionStringRedactor.Redact(connectionString);

        Assert.DoesNotContain("top-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("also-secret", redacted, StringComparison.Ordinal);
        Assert.Contains("db.example", redacted, StringComparison.Ordinal);
        Assert.Equal(2, redacted.Split("<redacted>", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Rejects_invalid_ports()
    {
        var builder = new BlueTuskConnectionStringBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Port = 65_536);
    }
}

