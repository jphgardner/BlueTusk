#if !NET7_0_OR_GREATER
using System.Net;
using System.Reflection;
using System.Security.Authentication;

namespace BlueTusk.Security;

internal enum LegacyNegotiateStatus
{
    Completed,
    ContinueNeeded,
    InvalidCredentials,
    GenericFailure,
}

// .NET 5/6 expose the OS security context through NTAuthentication. This is the
// same runtime bridge used by ASP.NET Core 6's Negotiate handler. Restrict it to
// these two frozen runtime lines; .NET 7+ uses the public NegotiateAuthentication API.
internal sealed class LegacyNegotiateAuthentication : IBlueTuskNegotiateAuthentication
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type AuthenticationType = typeof(AuthenticationException).Assembly.GetType("System.Net.NTAuthentication", throwOnError: true)!;
    private static readonly ConstructorInfo Constructor = AuthenticationType.GetConstructors(InstanceMembers).Single();
    private static readonly MethodInfo GetBlob = AuthenticationType.GetMethods(InstanceMembers)
        .Single(method => method.Name == "GetOutgoingBlob" && method.GetParameters().Length == 3);
    private static readonly PropertyInfo Completed = AuthenticationType.GetProperty("IsCompleted", InstanceMembers)!;
    private static readonly PropertyInfo Mutual = AuthenticationType.GetProperty("IsMutualAuthFlag", InstanceMembers)!;
    private static readonly MethodInfo Close = AuthenticationType.GetMethod("CloseContext", InstanceMembers)!;
    private static readonly FieldInfo ErrorCode = typeof(AuthenticationException).Assembly
        .GetType("System.Net.SecurityStatusPal", throwOnError: true)!.GetField("ErrorCode")!;
    private readonly object _context;

    public LegacyNegotiateAuthentication(string host, string service, NetworkCredential credential, bool useSspi)
    {
        var flagsType = Constructor.GetParameters()[4].ParameterType;
        // Match mutual authentication and identification without requesting
        // per-message signing/encryption, as required by PostgreSQL's exchange.
        var flags = Enum.Parse(flagsType, "MutualAuth, InitIdentify, Connection");
        _context = Constructor.Invoke([false, useSspi ? "Negotiate" : "Kerberos", credential, $"{service}/{host}", flags, null]);
    }

    public bool IsAuthenticated => (bool)Completed.GetValue(_context)!;

    public bool IsMutuallyAuthenticated => (bool)Mutual.GetValue(_context)!;

    public byte[]? GetOutgoingBlob(ReadOnlySpan<byte> incomingBlob, out LegacyNegotiateStatus statusCode)
    {
        var incoming = incomingBlob.IsEmpty ? null : incomingBlob.ToArray();
        try
        {
            object?[] arguments = [incoming, false, null];
            var outgoing = (byte[]?)GetBlob.Invoke(_context, arguments);
            statusCode = ErrorCode.GetValue(arguments[2])!.ToString() switch
            {
                "OK" => LegacyNegotiateStatus.Completed,
                "ContinueNeeded" => LegacyNegotiateStatus.ContinueNeeded,
                _ => LegacyNegotiateStatus.GenericFailure,
            };
            return outgoing;
        }
        finally
        {
            BlueTuskSensitiveBuffer.Clear(incoming);
        }
    }

    public void Dispose() => Close.Invoke(_context, null);
}
#endif
