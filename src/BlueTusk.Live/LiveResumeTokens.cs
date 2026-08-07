using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Live;

public sealed class LiveResumeTokenKey
{
    private readonly byte[] _secret;

    public LiveResumeTokenKey(string keyId, ReadOnlySpan<byte> secret, bool isPrimary = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (Encoding.UTF8.GetByteCount(keyId) > byte.MaxValue)
        {
            throw new ArgumentException("A resume-token key ID cannot exceed 255 UTF-8 bytes.", nameof(keyId));
        }

        if (secret.Length < 32)
        {
            throw new ArgumentException("A resume-token signing key must contain at least 32 bytes.", nameof(secret));
        }

        KeyId = keyId;
        _secret = secret.ToArray();
        IsPrimary = isPrimary;
    }

    public string KeyId { get; }

    public bool IsPrimary { get; }

    internal ReadOnlySpan<byte> Secret => _secret;
}

public enum LiveResumeTokenValidationStatus
{
    Valid,
    Malformed,
    UnsupportedVersion,
    UnknownKey,
    InvalidSignature,
    Expired,
    IdentityMismatch,
}

public sealed record LiveResumePosition(long Sequence, DateTimeOffset ExpiresAt);

public sealed record LiveResumeTokenValidationResult(
    LiveResumeTokenValidationStatus Status,
    LiveResumePosition? Position);

public sealed class LiveResumeTokenProtector
{
    public const int CurrentFormatVersion = 1;

    public const int MinimumSupportedFormatVersion = 1;

    private const int IdentityLength = 32;
    private const int SignatureLength = 32;
    private const int MaximumTokenLength = 2_048;
    private readonly Dictionary<string, LiveResumeTokenKey> _keys;
    private readonly LiveResumeTokenKey _primary;
    private readonly TimeProvider _timeProvider;

    public LiveResumeTokenProtector(
        IEnumerable<LiveResumeTokenKey> keys,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var keyArray = keys.ToArray();
        if (keyArray.Length == 0 || keyArray.Select(key => key.KeyId).Distinct(StringComparer.Ordinal).Count() != keyArray.Length)
        {
            throw new ArgumentException("Resume-token keys must be non-empty and uniquely identified.", nameof(keys));
        }

        var primaries = keyArray.Where(key => key.IsPrimary).ToArray();
        if (primaries.Length != 1)
        {
            throw new ArgumentException("Exactly one resume-token signing key must be primary.", nameof(keys));
        }

        _keys = keyArray.ToDictionary(key => key.KeyId, StringComparer.Ordinal);
        _primary = primaries[0];
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Protect(
        LiveSubscriptionIdentity identity,
        long sequence,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        var keyId = Encoding.UTF8.GetBytes(_primary.KeyId);
        var identityBytes = Convert.FromHexString(identity.Fingerprint);
        var expiresAt = _timeProvider.GetUtcNow().Add(lifetime);
        var payload = new byte[1 + 1 + keyId.Length + IdentityLength + sizeof(long) + sizeof(long)];
        payload[0] = CurrentFormatVersion;
        payload[1] = checked((byte)keyId.Length);
        keyId.CopyTo(payload.AsSpan(2));
        var offset = 2 + keyId.Length;
        identityBytes.CopyTo(payload.AsSpan(offset, IdentityLength));
        offset += IdentityLength;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(offset, sizeof(long)), sequence);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(offset, sizeof(long)), expiresAt.UtcTicks);
        var signature = HMACSHA256.HashData(_primary.Secret, payload);
        return $"bt1.{Base64UrlEncode(payload)}.{Base64UrlEncode(signature)}";
    }

    public LiveResumeTokenValidationResult Validate(
        string token,
        LiveSubscriptionIdentity expectedIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        if (token.Length > MaximumTokenLength)
        {
            return new LiveResumeTokenValidationResult(LiveResumeTokenValidationStatus.Malformed, null);
        }

        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || !string.Equals(parts[0], "bt1", StringComparison.Ordinal) ||
            !TryBase64UrlDecode(parts[1], out var payload) ||
            !TryBase64UrlDecode(parts[2], out var signature) ||
            signature.Length != SignatureLength ||
            payload.Length < 1 + 1 + IdentityLength + sizeof(long) + sizeof(long))
        {
            return new LiveResumeTokenValidationResult(LiveResumeTokenValidationStatus.Malformed, null);
        }

        if (payload[0] != CurrentFormatVersion)
        {
            return new LiveResumeTokenValidationResult(LiveResumeTokenValidationStatus.UnsupportedVersion, null);
        }

        var keyIdLength = payload[1];
        var requiredLength = 1 + 1 + keyIdLength + IdentityLength + sizeof(long) + sizeof(long);
        if (payload.Length != requiredLength)
        {
            return new LiveResumeTokenValidationResult(LiveResumeTokenValidationStatus.Malformed, null);
        }

        var keyId = Encoding.UTF8.GetString(payload, 2, keyIdLength);
        if (!_keys.TryGetValue(keyId, out var key))
        {
            return new LiveResumeTokenValidationResult(LiveResumeTokenValidationStatus.UnknownKey, null);
        }

        var expectedSignature = HMACSHA256.HashData(key.Secret, payload);
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, signature))
        {
            return new LiveResumeTokenValidationResult(LiveResumeTokenValidationStatus.InvalidSignature, null);
        }

        var offset = 2 + keyIdLength;
        var expectedIdentityBytes = Convert.FromHexString(expectedIdentity.Fingerprint);
        if (!CryptographicOperations.FixedTimeEquals(
                payload.AsSpan(offset, IdentityLength),
                expectedIdentityBytes))
        {
            return new LiveResumeTokenValidationResult(LiveResumeTokenValidationStatus.IdentityMismatch, null);
        }

        offset += IdentityLength;
        var sequence = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(offset, sizeof(long)));
        offset += sizeof(long);
        var expiryTicks = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(offset, sizeof(long)));
        if (sequence < 0 || expiryTicks < DateTimeOffset.MinValue.UtcTicks || expiryTicks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return new LiveResumeTokenValidationResult(LiveResumeTokenValidationStatus.Malformed, null);
        }

        var expiry = new DateTimeOffset(expiryTicks, TimeSpan.Zero);
        if (expiry <= _timeProvider.GetUtcNow())
        {
            return new LiveResumeTokenValidationResult(LiveResumeTokenValidationStatus.Expired, null);
        }

        return new LiveResumeTokenValidationResult(
            LiveResumeTokenValidationStatus.Valid,
            new LiveResumePosition(sequence, expiry));
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] decoded)
    {
        decoded = [];
        if (value.Length == 0 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        try
        {
            decoded = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
