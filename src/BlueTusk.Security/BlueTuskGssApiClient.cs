using System.Net;
using System.Net.Security;

namespace BlueTusk.Security;

/// <summary>Runs a PostgreSQL GSSAPI or SSPI authentication exchange.</summary>
/// <remarks>
/// The context uses the operating system's Kerberos/Negotiate implementation, requires mutual
/// authentication, and does not request message signing or encryption because PostgreSQL uses
/// GSSAPI only for authentication in this protocol exchange.
/// </remarks>
public sealed class BlueTuskGssApiClient : IDisposable
{
    private readonly IBlueTuskNegotiateAuthentication _authentication;
    private bool _complete;
    private bool _disposed;

    /// <summary>Creates an operating-system-backed security context.</summary>
    /// <param name="host">The PostgreSQL server host used in the service principal name.</param>
    /// <param name="kerberosServiceName">The Kerberos service name. PostgreSQL defaults to <c>postgres</c>.</param>
    /// <param name="credential">An explicit credential, or null to use the process identity or credential cache.</param>
    /// <param name="useSspi">True for a PostgreSQL SSPI request; false for GSSAPI/Kerberos.</param>
    public BlueTuskGssApiClient(
        string host,
        string kerberosServiceName = "postgres",
        NetworkCredential? credential = null,
        bool useSspi = false)
        : this(CreateAuthentication(host, kerberosServiceName, credential, useSspi))
    {
    }

    internal BlueTuskGssApiClient(IBlueTuskNegotiateAuthentication authentication)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
    }

    /// <summary>
    /// Processes a server token and returns the next caller-owned token, or null when no response
    /// is required. The caller must clear a returned token after sending it.
    /// </summary>
    public byte[]? GetOutgoingBlob(ReadOnlySpan<byte> incomingBlob)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_complete)
        {
            throw new BlueTuskAuthenticationException(
                "PostgreSQL sent another GSSAPI challenge after the security context completed.");
        }

        try
        {
            var outgoingBlob = _authentication.GetOutgoingBlob(incomingBlob, out var statusCode);
            switch (statusCode)
            {
                case NegotiateAuthenticationStatusCode.Completed:
                    _complete = true;
                    return outgoingBlob is { Length: > 0 } ? outgoingBlob : null;
                case NegotiateAuthenticationStatusCode.ContinueNeeded:
                    if (outgoingBlob is not { Length: > 0 })
                    {
                        throw new BlueTuskAuthenticationException(
                            "The operating-system GSSAPI provider requested continuation without producing a token.");
                    }

                    return outgoingBlob;
                default:
                    BlueTuskSensitiveBuffer.Clear(outgoingBlob);
                    throw new BlueTuskAuthenticationException(
                        $"The operating-system GSSAPI provider rejected authentication with status {statusCode}.");
            }
        }
        catch (BlueTuskAuthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new BlueTuskAuthenticationException(
                "The operating-system GSSAPI provider failed without completing authentication.");
        }
    }

    /// <summary>Verifies that negotiation completed with mutual authentication.</summary>
    public void EnsureComplete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_complete || !_authentication.IsAuthenticated)
        {
            throw new BlueTuskAuthenticationException(
                "PostgreSQL completed authentication before the GSSAPI security context was established.");
        }

        if (!_authentication.IsMutuallyAuthenticated)
        {
            throw new BlueTuskAuthenticationException(
                "The GSSAPI security context did not mutually authenticate the PostgreSQL server.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _authentication.Dispose();
        _disposed = true;
    }

    private static SystemNegotiateAuthentication CreateAuthentication(
        string host,
        string kerberosServiceName,
        NetworkCredential? credential,
        bool useSspi)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(kerberosServiceName);
        if (kerberosServiceName.IndexOfAny(['/', '@', '\0']) >= 0)
        {
            throw new ArgumentException(
                "A Kerberos service name cannot contain '/', '@', or a null character.",
                nameof(kerberosServiceName));
        }

        try
        {
            return new SystemNegotiateAuthentication(
                new NegotiateAuthenticationClientOptions
                {
                    Package = useSspi ? "Negotiate" : "Kerberos",
                    TargetName = $"{kerberosServiceName}/{host}",
                    Credential = credential ?? CredentialCache.DefaultNetworkCredentials,
                    RequireMutualAuthentication = true,
                    RequiredProtectionLevel = ProtectionLevel.None,
                });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new BlueTuskAuthenticationException(
                "The operating-system GSSAPI provider could not create an authentication context.");
        }
    }

    private sealed class SystemNegotiateAuthentication : IBlueTuskNegotiateAuthentication
    {
        private readonly NegotiateAuthentication _authentication;

        internal SystemNegotiateAuthentication(NegotiateAuthenticationClientOptions options)
        {
            _authentication = new NegotiateAuthentication(options);
        }

        public bool IsAuthenticated => _authentication.IsAuthenticated;

        public bool IsMutuallyAuthenticated => _authentication.IsMutuallyAuthenticated;

        public byte[]? GetOutgoingBlob(
            ReadOnlySpan<byte> incomingBlob,
            out NegotiateAuthenticationStatusCode statusCode) =>
            _authentication.GetOutgoingBlob(incomingBlob, out statusCode);

        public void Dispose() => _authentication.Dispose();
    }
}

internal interface IBlueTuskNegotiateAuthentication : IDisposable
{
    bool IsAuthenticated { get; }

    bool IsMutuallyAuthenticated { get; }

    byte[]? GetOutgoingBlob(
        ReadOnlySpan<byte> incomingBlob,
        out NegotiateAuthenticationStatusCode statusCode);
}
