using System.Net;
using System.Net.Sockets;

namespace BlueTusk.Transport;

/// <summary>Classifies a failure while establishing a PostgreSQL transport connection.</summary>
public enum BlueTuskTransportFailureKind
{
    NameResolution,
    Timeout,
    ConnectionRefused,
    NetworkUnreachable,
    HostUnreachable,
    AddressUnavailable,
    SocketFailure,
}

/// <summary>Describes one resolved address that could not be connected.</summary>
public sealed record BlueTuskAddressFailure(IPAddress Address, SocketError SocketErrorCode);

/// <summary>A classified failure while establishing a PostgreSQL transport connection.</summary>
public sealed class BlueTuskTransportException : IOException
{
    internal BlueTuskTransportException(
        BlueTuskTransportFailureKind failureKind,
        BlueTuskEndpoint endpoint,
        IReadOnlyList<BlueTuskAddressFailure> addressFailures,
        Exception innerException)
        : base(CreateMessage(failureKind, endpoint, addressFailures.Count), innerException)
    {
        FailureKind = failureKind;
        Endpoint = endpoint;
        AddressFailures = addressFailures.ToArray();
    }

    /// <summary>Gets the stable category for the connection failure.</summary>
    public BlueTuskTransportFailureKind FailureKind { get; }

    /// <summary>Gets the logical endpoint that was being connected.</summary>
    public BlueTuskEndpoint Endpoint { get; }

    /// <summary>Gets the failed resolved-address attempts, in attempt order.</summary>
    public IReadOnlyList<BlueTuskAddressFailure> AddressFailures { get; }

    internal static BlueTuskTransportException ForNameResolution(
        BlueTuskEndpoint.Tcp endpoint,
        Exception innerException) =>
        new(
            BlueTuskTransportFailureKind.NameResolution,
            endpoint,
            [],
            innerException);

    internal static BlueTuskTransportException ForTimeout(
        BlueTuskEndpoint endpoint,
        TimeSpan timeout,
        Exception innerException) =>
        new(
            BlueTuskTransportFailureKind.Timeout,
            endpoint,
            [],
            new TimeoutException($"Connecting to PostgreSQL exceeded the {timeout} timeout.", innerException));

    internal static BlueTuskTransportException ForSocket(
        BlueTuskEndpoint endpoint,
        IReadOnlyList<BlueTuskAddressFailure> failures,
        SocketException innerException) =>
        new(
            Classify(innerException.SocketErrorCode),
            endpoint,
            failures,
            innerException);

    private static BlueTuskTransportFailureKind Classify(SocketError socketError) =>
        socketError switch
        {
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                BlueTuskTransportFailureKind.NameResolution,
            SocketError.TimedOut => BlueTuskTransportFailureKind.Timeout,
            SocketError.ConnectionRefused => BlueTuskTransportFailureKind.ConnectionRefused,
            SocketError.NetworkDown or SocketError.NetworkUnreachable =>
                BlueTuskTransportFailureKind.NetworkUnreachable,
            SocketError.HostDown or SocketError.HostUnreachable =>
                BlueTuskTransportFailureKind.HostUnreachable,
            SocketError.AddressAlreadyInUse or SocketError.AddressNotAvailable or
                SocketError.AddressFamilyNotSupported => BlueTuskTransportFailureKind.AddressUnavailable,
            _ => BlueTuskTransportFailureKind.SocketFailure,
        };

    private static string CreateMessage(
        BlueTuskTransportFailureKind failureKind,
        BlueTuskEndpoint endpoint,
        int attemptCount)
    {
        var endpointText = endpoint switch
        {
            BlueTuskEndpoint.Tcp tcp => $"{tcp.Host}:{tcp.Port}",
            BlueTuskEndpoint.UnixSocket unix => unix.Path,
            _ => endpoint.ToString() ?? endpoint.GetType().Name,
        };
        var attempts = attemptCount == 0 ? string.Empty : $" after {attemptCount} address attempt(s)";
        return $"Could not connect to PostgreSQL endpoint {endpointText}{attempts} ({failureKind}).";
    }
}
