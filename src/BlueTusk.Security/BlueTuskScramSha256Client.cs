using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Security;

/// <summary>Performs one SCRAM-SHA-256 or SCRAM-SHA-256-PLUS client exchange.</summary>
public sealed class BlueTuskScramSha256Client : IDisposable
{
    public const string MechanismName = "SCRAM-SHA-256";
    public const string PlusMechanismName = "SCRAM-SHA-256-PLUS";
    private const int DerivedKeyLength = 32;
    private const int MaximumIterationCount = 1_000_000;

    private readonly byte[] _passwordBytes;
    private readonly byte[]? _channelBindingData;
    private readonly string _gs2Header;
    private readonly string _clientFirstBare;
    private byte[]? _expectedServerSignature;
    private bool _clientFinalCreated;
    private bool _verified;
    private bool _disposed;

    public BlueTuskScramSha256Client(
        string username,
        string password,
        string? clientNonce = null,
        ReadOnlyMemory<byte>? channelBindingData = null)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        ClientNonce = clientNonce ?? CreateNonce();
        ValidateNonce(ClientNonce);

        _channelBindingData = channelBindingData?.ToArray();
        Mechanism = _channelBindingData is null ? MechanismName : PlusMechanismName;
        _gs2Header = _channelBindingData is null ? "n,," : "p=tls-server-end-point,,";
        _clientFirstBare = $"n={EscapeUsername(username)},r={ClientNonce}";
        ClientFirstMessage = _gs2Header + _clientFirstBare;
        _passwordBytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormKC));
    }

    public string Mechanism { get; }

    public string ClientNonce { get; }

    public string ClientFirstMessage { get; }

    public string CreateClientFinalMessage(string serverFirstMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverFirstMessage);
        if (_clientFinalCreated)
        {
            throw new InvalidOperationException("The SCRAM client-final message has already been created.");
        }

        var attributes = ParseAttributes(serverFirstMessage);
        if (attributes.ContainsKey('m'))
        {
            throw new BlueTuskAuthenticationException("The server requested an unsupported mandatory SCRAM extension.");
        }

        var serverNonce = GetRequiredAttribute(attributes, 'r');
        if (!serverNonce.StartsWith(ClientNonce, StringComparison.Ordinal) || serverNonce.Length <= ClientNonce.Length)
        {
            throw new BlueTuskAuthenticationException("The SCRAM server nonce does not extend the client nonce.");
        }

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(GetRequiredAttribute(attributes, 's'));
        }
        catch (FormatException exception)
        {
            throw new BlueTuskAuthenticationException("The SCRAM server salt is not valid Base64.", exception);
        }

        if (!int.TryParse(
                GetRequiredAttribute(attributes, 'i'),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var iterationCount) ||
            iterationCount <= 0 ||
            iterationCount > MaximumIterationCount)
        {
            BlueTuskSensitiveBuffer.Clear(salt);
            throw new BlueTuskAuthenticationException("The SCRAM iteration count is outside the supported range.");
        }

        var channelBindingInput = BuildChannelBindingInput();
        var clientFinalWithoutProof = $"c={Convert.ToBase64String(channelBindingInput)},r={serverNonce}";
        var authenticationMessage = $"{_clientFirstBare},{serverFirstMessage},{clientFinalWithoutProof}";
        var authenticationBytes = Encoding.UTF8.GetBytes(authenticationMessage);

        byte[]? saltedPassword = null;
        byte[]? clientKey = null;
        byte[]? storedKey = null;
        byte[]? clientSignature = null;
        byte[]? serverKey = null;
        byte[]? proof = null;
        try
        {
            saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
                _passwordBytes,
                salt,
                iterationCount,
                HashAlgorithmName.SHA256,
                DerivedKeyLength);
            clientKey = Hmac(saltedPassword, "Client Key"u8);
            storedKey = SHA256.HashData(clientKey);
            clientSignature = Hmac(storedKey, authenticationBytes);

            proof = new byte[DerivedKeyLength];
            for (var index = 0; index < proof.Length; index++)
            {
                proof[index] = (byte)(clientKey[index] ^ clientSignature[index]);
            }

            serverKey = Hmac(saltedPassword, "Server Key"u8);
            _expectedServerSignature = Hmac(serverKey, authenticationBytes);
            _clientFinalCreated = true;
            return $"{clientFinalWithoutProof},p={Convert.ToBase64String(proof)}";
        }
        finally
        {
            BlueTuskSensitiveBuffer.Clear(salt);
            BlueTuskSensitiveBuffer.Clear(channelBindingInput);
            BlueTuskSensitiveBuffer.Clear(authenticationBytes);
            BlueTuskSensitiveBuffer.Clear(saltedPassword);
            BlueTuskSensitiveBuffer.Clear(clientKey);
            BlueTuskSensitiveBuffer.Clear(storedKey);
            BlueTuskSensitiveBuffer.Clear(clientSignature);
            BlueTuskSensitiveBuffer.Clear(serverKey);
            BlueTuskSensitiveBuffer.Clear(proof);
        }
    }

    public void VerifyServerFinalMessage(string serverFinalMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverFinalMessage);
        if (!_clientFinalCreated || _expectedServerSignature is null)
        {
            throw new InvalidOperationException("A SCRAM client-final message must be created before verifying the server.");
        }

        var attributes = ParseAttributes(serverFinalMessage);
        if (attributes.TryGetValue('e', out _))
        {
            throw new BlueTuskAuthenticationException("The PostgreSQL server rejected SCRAM authentication.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(GetRequiredAttribute(attributes, 'v'));
        }
        catch (FormatException exception)
        {
            throw new BlueTuskAuthenticationException("The SCRAM server signature is not valid Base64.", exception);
        }

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(signature, _expectedServerSignature))
            {
                throw new BlueTuskAuthenticationException("The SCRAM server signature could not be verified.");
            }

            _verified = true;
        }
        finally
        {
            BlueTuskSensitiveBuffer.Clear(signature);
        }
    }

    public void EnsureVerified()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_verified)
        {
            throw new BlueTuskAuthenticationException("The SCRAM exchange did not produce a verified server signature.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        BlueTuskSensitiveBuffer.Clear(_passwordBytes);
        BlueTuskSensitiveBuffer.Clear(_channelBindingData);
        BlueTuskSensitiveBuffer.Clear(_expectedServerSignature);
        _expectedServerSignature = null;
    }

    private byte[] BuildChannelBindingInput()
    {
        var header = Encoding.UTF8.GetBytes(_gs2Header);
        if (_channelBindingData is null)
        {
            return header;
        }

        var result = new byte[header.Length + _channelBindingData.Length];
        header.CopyTo(result, 0);
        _channelBindingData.CopyTo(result, header.Length);
        BlueTuskSensitiveBuffer.Clear(header);
        return result;
    }

    private static Dictionary<char, string> ParseAttributes(string message)
    {
        var attributes = new Dictionary<char, string>();
        foreach (var part in message.Split(','))
        {
            if (part.Length < 3 || part[1] != '=')
            {
                throw new BlueTuskAuthenticationException("The server sent a malformed SCRAM attribute.");
            }

            if (!attributes.TryAdd(part[0], part[2..]))
            {
                throw new BlueTuskAuthenticationException("The server sent a duplicate SCRAM attribute.");
            }
        }

        return attributes;
    }

    private static string GetRequiredAttribute(Dictionary<char, string> attributes, char name) =>
        attributes.TryGetValue(name, out var value) && value.Length > 0
            ? value
            : throw new BlueTuskAuthenticationException($"The SCRAM server message omitted the '{name}' attribute.");

    private static byte[] Hmac(byte[] key, ReadOnlySpan<byte> data)
        => HMACSHA256.HashData(key, data);

    private static string EscapeUsername(string username)
    {
        if (username.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A SCRAM username cannot contain a null character.", nameof(username));
        }

        return username.Replace("=", "=3D", StringComparison.Ordinal).Replace(",", "=2C", StringComparison.Ordinal);
    }

    private static string CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static void ValidateNonce(string nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        if (nonce.Contains(',', StringComparison.Ordinal))
        {
            throw new ArgumentException("A SCRAM nonce cannot contain a comma.", nameof(nonce));
        }
    }
}
