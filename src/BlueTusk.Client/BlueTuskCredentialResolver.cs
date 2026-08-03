using BlueTusk.Security;

namespace BlueTusk.Client;

internal static class BlueTuskCredentialResolver
{
    internal static string Resolve(BlueTuskClientOptions options)
    {
        var request = CreateRequest(options);
        if (options.AccessTokenProvider is not null)
        {
            return Invoke(() => options.AccessTokenProvider(request), "access-token");
        }

        if (options.AccessTokenProviderAsync is not null)
        {
            throw new InvalidOperationException(
                "Synchronous connection opening requires a synchronous access-token provider.");
        }

        if (options.PasswordProvider is not null)
        {
            return Invoke(() => options.PasswordProvider(request), "password");
        }

        if (options.PasswordProviderAsync is not null)
        {
            throw new InvalidOperationException(
                "Synchronous connection opening requires a synchronous password provider.");
        }

        return ResolveStatic(options);
    }

    internal static async ValueTask<string> ResolveAsync(
        BlueTuskClientOptions options,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(options);
        if (options.AccessTokenProviderAsync is not null)
        {
            return await InvokeAsync(
                () => options.AccessTokenProviderAsync(request, cancellationToken),
                "access-token",
                cancellationToken).ConfigureAwait(false);
        }

        if (options.AccessTokenProvider is not null)
        {
            return Invoke(() => options.AccessTokenProvider(request), "access-token");
        }

        if (options.PasswordProviderAsync is not null)
        {
            return await InvokeAsync(
                () => options.PasswordProviderAsync(request, cancellationToken),
                "password",
                cancellationToken).ConfigureAwait(false);
        }

        if (options.PasswordProvider is not null)
        {
            return Invoke(() => options.PasswordProvider(request), "password");
        }

        return await ResolveStaticAsync(options, cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveStatic(BlueTuskClientOptions options)
    {
        if (options.Password is not null)
        {
            return options.Password;
        }

        var passfile = options.Passfile ?? BlueTuskPasswordFile.GetDefaultPath();
        var passwordFileDatabase = GetPasswordFileDatabase(options);
        var password = string.IsNullOrEmpty(passfile)
            ? null
            : BlueTuskPasswordFile.Resolve(
                passfile,
                options.Host,
                options.Port,
                passwordFileDatabase,
                options.Username);
        return password ?? throw new BlueTuskAuthenticationException(
            "PostgreSQL requested password authentication, but no configured credential source produced a value.");
    }

    private static async ValueTask<string> ResolveStaticAsync(
        BlueTuskClientOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Password is not null)
        {
            return options.Password;
        }

        var passfile = options.Passfile ?? BlueTuskPasswordFile.GetDefaultPath();
        var passwordFileDatabase = GetPasswordFileDatabase(options);
        var password = string.IsNullOrEmpty(passfile)
            ? null
            : await BlueTuskPasswordFile.ResolveAsync(
                passfile,
                options.Host,
                options.Port,
                passwordFileDatabase,
                options.Username,
                cancellationToken).ConfigureAwait(false);
        return password ?? throw new BlueTuskAuthenticationException(
            "PostgreSQL requested password authentication, but no configured credential source produced a value.");
    }

    private static BlueTuskCredentialRequest CreateRequest(BlueTuskClientOptions options) => new(
        options.Host,
        options.Port,
        options.Database,
        options.Username);

    private static string GetPasswordFileDatabase(BlueTuskClientOptions options) =>
        options.ReplicationMode == BlueTuskReplicationMode.Physical
            ? "replication"
            : options.Database;

    private static string Invoke(Func<string> callback, string source)
    {
        try
        {
            return callback() ?? throw new BlueTuskAuthenticationException(
                $"The configured {source} provider returned no credential.");
        }
        catch (BlueTuskAuthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new BlueTuskAuthenticationException(
                $"The configured {source} provider failed without producing a credential.");
        }
    }

    private static async ValueTask<string> InvokeAsync(
        Func<ValueTask<string>> callback,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            return await callback().ConfigureAwait(false) ?? throw new BlueTuskAuthenticationException(
                $"The configured {source} provider returned no credential.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BlueTuskAuthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new BlueTuskAuthenticationException(
                $"The configured {source} provider failed without producing a credential.");
        }
    }
}
