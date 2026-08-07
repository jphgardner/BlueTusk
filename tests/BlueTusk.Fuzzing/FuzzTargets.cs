using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Data.Copy;
using BlueTusk.Live;
using BlueTusk.Protocol;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Security;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Fuzzing;

public static class FuzzTargets
{
    public const int MaximumInputBytes = 64 * 1024;
    public const int MaximumMessagesPerInput = 512;

    private const int RangeTypeOid = 90_410;
    private const int MultirangeTypeOid = 90_411;
    private const int CompositeTypeOid = 90_420;
    private static readonly byte[] ResumeSecret = Enumerable.Repeat((byte)0x2a, 32).ToArray();
    private static readonly LiveSubscriptionIdentity ResumeIdentity = new(
        "fuzz-database",
        new string('a', 64),
        new string('b', 64),
        "fuzz-scope",
        "policy:v1",
        128);
    private static readonly LiveResumeTokenProtector ResumeProtector = new(
        [new LiveResumeTokenKey("fuzz", ResumeSecret, isPrimary: true)]);
    private static readonly BlueTuskTypeDescriptor Int4ArrayType = new()
    {
        Id = new BlueTuskTypeId(1007),
        Schema = "pg_catalog",
        Name = "_int4",
        Kind = BlueTuskTypeKind.Array,
        ElementType = BlueTuskBuiltInTypes.Int4.Id,
    };
    private static readonly BlueTuskArrayCodec Int4ArrayCodec =
        new(BlueTuskBuiltInTypes.Int4, new BlueTuskInt32Codec());
    private static readonly BlueTuskTypeRegistry BuiltInRegistry =
        BlueTuskBuiltInTypes.CreateRegistry();
    private static readonly BlueTuskTypeRegistry StructuredRegistry =
        CreateStructuredRegistry();

    private static readonly IReadOnlyDictionary<string, Action<ReadOnlyMemory<byte>>> Targets =
        new Dictionary<string, Action<ReadOnlyMemory<byte>>>(StringComparer.Ordinal)
        {
            ["protocol-frames"] = ProtocolFrames,
            ["authentication"] = Authentication,
            ["pgoutput"] = PgOutput,
            ["binary-copy"] = BinaryCopy,
            ["array-codec"] = ArrayCodec,
            ["range-codec"] = RangeCodec,
            ["composite-codec"] = CompositeCodec,
            ["streams-envelope"] = StreamsEnvelope,
            ["live-resume-token"] = LiveResumeToken,
        };

    public static IReadOnlyList<string> Names { get; } =
        Targets.Keys.Order(StringComparer.Ordinal).ToArray();

    public static void Run(string target, ReadOnlyMemory<byte> input)
    {
        if (input.Length > MaximumInputBytes)
        {
            return;
        }

        if (!Targets.TryGetValue(target, out var action))
        {
            throw new ArgumentException($"Unknown fuzz target '{target}'.", nameof(target));
        }

        try
        {
            action(input);
        }
        catch (Exception exception) when (IsExpectedMalformedInput(exception))
        {
        }
    }

    private static void ProtocolFrames(ReadOnlyMemory<byte> input)
    {
        var buffer = CreateSegmentedSequence(input);
        var parser = new BlueTuskBackendMessageParser(MaximumInputBytes);
        var decoded = 0;
        while (parser.TryParse(ref buffer, out var message))
        {
            if (++decoded > MaximumMessagesPerInput)
            {
                throw new InvalidDataException(
                    $"A fuzz input produced more than {MaximumMessagesPerInput} protocol messages.");
            }

            DecodeBackendMessage(message);
        }
    }

    private static void Authentication(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var selector = input.Span[0];
        var payload = input[1..];
        if ((selector & 1) == 0)
        {
            _ = BlueTuskBackendMessageDecoder.DecodeAuthentication(
                new BlueTuskBackendMessage(
                    (byte)'R',
                    new ReadOnlySequence<byte>(payload)));
            return;
        }

        var serverMessage = Encoding.UTF8.GetString(payload.Span);
        using var client = new BlueTuskScramSha256Client(
            "fuzz-user",
            "fuzz-password",
            "fuzz-client-nonce");
        if (!string.IsNullOrWhiteSpace(serverMessage))
        {
            _ = client.CreateClientFinalMessage(serverMessage);
        }
    }

    private static void PgOutput(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var selector = input.Span[0];
        var decoder = new BlueTuskPgOutputDecoder(
            new BlueTuskPgOutputDecoderOptions
            {
                ProtocolVersion = 4,
                StreamingMode = (selector & 1) == 0
                    ? BlueTuskPgOutputStreamingMode.Off
                    : BlueTuskPgOutputStreamingMode.Parallel,
                TwoPhase = (selector & 2) != 0,
            });
        _ = decoder.Decode(input[1..]);
    }

    private static void BinaryCopy(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        uint[] typeOids =
        [
            BlueTuskBuiltInTypes.Boolean.Id.Oid,
            BlueTuskBuiltInTypes.Bytea.Id.Oid,
            BlueTuskBuiltInTypes.Int2.Id.Oid,
            BlueTuskBuiltInTypes.Int4.Id.Oid,
            BlueTuskBuiltInTypes.Int8.Id.Oid,
            BlueTuskBuiltInTypes.Numeric.Id.Oid,
            BlueTuskBuiltInTypes.Text.Id.Oid,
            BlueTuskBuiltInTypes.Uuid.Id.Oid,
            BlueTuskBuiltInTypes.Timestamp.Id.Oid,
            BlueTuskBuiltInTypes.TimestampWithTimeZone.Id.Oid,
        ];
        var oid = typeOids[input.Span[0] % typeOids.Length];
        _ = BlueTuskBinaryCopyCodec.Decode<object>(
            input[1..],
            oid,
            BuiltInRegistry);
    }

    private static void ArrayCodec(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var reader = new BlueTuskReader(input.Span[1..]);
        _ = Int4ArrayCodec.Read(
            ref reader,
            (input.Span[0] & 1) == 0
                ? BlueTuskDataFormat.Binary
                : BlueTuskDataFormat.Text,
            Int4ArrayType);
        EnsureConsumed(reader.Remaining, "array");
    }

    private static void RangeCodec(ReadOnlyMemory<byte> input)
    {
        DecodeStructured(
            input,
            (input.IsEmpty || (input.Span[0] & 2) == 0)
                ? RangeTypeOid
                : MultirangeTypeOid,
            "range");
    }

    private static void CompositeCodec(ReadOnlyMemory<byte> input) =>
        DecodeStructured(input, CompositeTypeOid, "composite");

    private static void StreamsEnvelope(ReadOnlyMemory<byte> input)
    {
        var content = input.ToArray();
        var data = new byte[content.Length + SHA256.HashSizeInBytes];
        content.CopyTo(data, 0);
        _ = SHA256.HashData(
            content,
            data.AsSpan(content.Length, SHA256.HashSizeInBytes));
        var format = content.Length >= sizeof(uint) + sizeof(int)
            ? BinaryPrimitives.ReadInt32LittleEndian(content.AsSpan(sizeof(uint), sizeof(int)))
            : ChangeTransactionEnvelope.CurrentFormatVersion;
        var envelope = new ChangeTransactionEnvelope(data, format);
        _ = ChangeTransactionEnvelopeCodec.Decode(
            envelope,
            new ChangeTransactionEnvelopeOptions
            {
                MaxEnvelopeBytes = MaximumInputBytes + SHA256.HashSizeInBytes,
                MaxChanges = MaximumMessagesPerInput,
                MaxTables = 128,
                MaxColumnsPerTable = 256,
                MaxStringBytes = 4096,
            });
    }

    private static void LiveResumeToken(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        string token;
        switch (input.Span[0] % 3)
        {
            case 0:
                token = Encoding.UTF8.GetString(input.Span[1..]);
                if (string.IsNullOrWhiteSpace(token))
                {
                    return;
                }

                break;
            case 1:
                token = SignResumePayload(input.Span[1..]);
                break;
            default:
                token = BuildStructuredResumeToken(input.Span[1..]);
                break;
        }

        var result = ResumeProtector.Validate(token, ResumeIdentity);
        if (result.Status == LiveResumeTokenValidationStatus.Valid &&
            result.Position is not { Sequence: >= 0 })
        {
            throw new InvalidDataException("A valid resume token returned an invalid position.");
        }
    }

    private static void DecodeBackendMessage(BlueTuskBackendMessage message)
    {
        switch (message.Identifier)
        {
            case 'R':
                _ = BlueTuskBackendMessageDecoder.DecodeAuthentication(message);
                break;
            case 'S':
                _ = BlueTuskBackendMessageDecoder.DecodeParameterStatus(message);
                break;
            case 'K':
                _ = BlueTuskBackendMessageDecoder.DecodeBackendKeyData(message);
                break;
            case 'A':
                _ = BlueTuskBackendMessageDecoder.DecodeNotificationResponse(message);
                break;
            case 'Z':
                _ = BlueTuskBackendMessageDecoder.DecodeReadyForQuery(message);
                break;
            case 'T':
                _ = BlueTuskBackendMessageDecoder.DecodeRowDescription(message);
                break;
            case 'D':
                _ = BlueTuskBackendMessageDecoder.DecodeDataRow(message);
                break;
            case 'C':
                _ = BlueTuskBackendMessageDecoder.DecodeCommandComplete(message);
                break;
            case 'G':
            case 'H':
            case 'W':
                _ = BlueTuskBackendMessageDecoder.DecodeCopyResponse(message);
                break;
            case 'd':
                _ = BlueTuskBackendMessageDecoder.DecodeCopyData(message);
                break;
            case 'E':
            case 'N':
                _ = BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(message);
                break;
            default:
                _ = message.ToPayloadArray();
                break;
        }
    }

    private static void DecodeStructured(
        ReadOnlyMemory<byte> input,
        int typeOid,
        string description)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var typeId = new BlueTuskTypeId(checked((uint)typeOid));
        var type = StructuredRegistry.Types.Single(candidate => candidate.Id == typeId);
        if (!StructuredRegistry.TryGetCodec(typeId, out var codec) || codec is null)
        {
            throw new InvalidDataException($"The fuzz {description} codec was not registered.");
        }

        var reader = new BlueTuskReader(input.Span[1..]);
        _ = codec.Read(
            ref reader,
            (input.Span[0] & 1) == 0
                ? BlueTuskDataFormat.Binary
                : BlueTuskDataFormat.Text,
            type);
        EnsureConsumed(reader.Remaining, description);
    }

    private static BlueTuskTypeRegistry CreateStructuredRegistry() =>
        BlueTuskTypeCatalogue.BuildRegistry(
        [
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(RangeTypeOid),
                Schema = "fuzz",
                Name = "int_range",
                PostgreSqlKind = 'r',
                PostgreSqlCategory = 'R',
                RangeSubtype = BlueTuskBuiltInTypes.Int4.Id,
                RangeType = new BlueTuskTypeId(RangeTypeOid),
                MultirangeType = new BlueTuskTypeId(MultirangeTypeOid),
            },
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(MultirangeTypeOid),
                Schema = "fuzz",
                Name = "int_multirange",
                PostgreSqlKind = 'm',
                PostgreSqlCategory = 'R',
                RangeSubtype = BlueTuskBuiltInTypes.Int4.Id,
                RangeType = new BlueTuskTypeId(RangeTypeOid),
                MultirangeType = new BlueTuskTypeId(MultirangeTypeOid),
            },
            new BlueTuskCatalogueType
            {
                Id = new BlueTuskTypeId(CompositeTypeOid),
                Schema = "fuzz",
                Name = "record",
                PostgreSqlKind = 'c',
                PostgreSqlCategory = 'C',
                CompositeFields =
                [
                    new BlueTuskCompositeField
                    {
                        Position = 1,
                        Name = "id",
                        Type = BlueTuskBuiltInTypes.Int4.Id,
                    },
                    new BlueTuskCompositeField
                    {
                        Position = 2,
                        Name = "name",
                        Type = BlueTuskBuiltInTypes.Text.Id,
                    },
                ],
            },
        ]);

    private static ReadOnlySequence<byte> CreateSegmentedSequence(ReadOnlyMemory<byte> input)
    {
        if (input.Length < 3)
        {
            return new ReadOnlySequence<byte>(input);
        }

        var firstLength = Math.Max(1, input.Length / 3);
        var secondLength = Math.Max(1, (input.Length - firstLength) / 2);
        var first = new BufferSegment(input[..firstLength]);
        var second = first.Append(input.Slice(firstLength, secondLength));
        var third = second.Append(input[(firstLength + secondLength)..]);
        return new ReadOnlySequence<byte>(first, 0, third, third.Memory.Length);
    }

    private static string SignResumePayload(ReadOnlySpan<byte> payload)
    {
        var signature = HMACSHA256.HashData(ResumeSecret, payload);
        return $"bt1.{Base64UrlEncode(payload)}.{Base64UrlEncode(signature)}";
    }

    private static string BuildStructuredResumeToken(ReadOnlySpan<byte> mutations)
    {
        var keyId = "fuzz"u8;
        var payload = new byte[
            1 + 1 + keyId.Length + SHA256.HashSizeInBytes + sizeof(long) + sizeof(long)];
        payload[0] = (byte)(mutations.IsEmpty
            ? LiveResumeTokenProtector.CurrentFormatVersion
            : mutations[0]);
        payload[1] = checked((byte)keyId.Length);
        keyId.CopyTo(payload.AsSpan(2));
        var offset = 2 + keyId.Length;
        Convert.FromHexString(ResumeIdentity.Fingerprint)
            .CopyTo(payload.AsSpan(offset, SHA256.HashSizeInBytes));
        offset += SHA256.HashSizeInBytes;
        var sequence = mutations.Length >= 9
            ? BinaryPrimitives.ReadInt64BigEndian(mutations[1..9])
            : 0;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(offset, sizeof(long)), sequence);
        offset += sizeof(long);
        var expiry = mutations.Length >= 17
            ? BinaryPrimitives.ReadInt64BigEndian(mutations[9..17])
            : DateTimeOffset.MaxValue.UtcTicks;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(offset, sizeof(long)), expiry);
        return SignResumePayload(payload);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void EnsureConsumed(int remaining, string description)
    {
        if (remaining != 0)
        {
            throw new InvalidDataException(
                $"The fuzz {description} codec left {remaining} unread bytes.");
        }
    }

    private static bool IsExpectedMalformedInput(Exception exception) =>
        exception is BlueTuskProtocolException or
            BlueTuskPgOutputProtocolException or
            BlueTuskAuthenticationException or
            ChangeTransactionEnvelopeException or
            InvalidOperationException or
            ArgumentException or
            InvalidDataException or
            EndOfStreamException or
            DecoderFallbackException or
            FormatException or
            OverflowException or
            CryptographicException;

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new BufferSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length,
            };
            Next = segment;
            return segment;
        }
    }
}
