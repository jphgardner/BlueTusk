namespace BlueTusk.Replication.PgOutput;

/// <summary>Statefully decodes PostgreSQL pgoutput messages.</summary>
public sealed class BlueTuskPgOutputDecoder
{
    private const int MaximumCollectionCount = 4096;
    private static readonly DateTimeOffset PostgreSqlEpoch =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly BlueTuskPgOutputDecoderOptions _options;
    private bool _insideStreamSegment;

    public BlueTuskPgOutputDecoder()
        : this(new BlueTuskPgOutputDecoderOptions())
    {
    }

    public BlueTuskPgOutputDecoder(BlueTuskPgOutputDecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public BlueTuskPgOutputDecoderOptions Options => _options;

    public bool IsInsideStreamSegment => _insideStreamSegment;

    /// <summary>Decodes one pgoutput message and retains its WAL envelope.</summary>
    public BlueTuskPgOutputEnvelope Decode(BlueTuskXLogData xLogData)
    {
        ArgumentNullException.ThrowIfNull(xLogData);
        var message = Decode(xLogData.Data);
        return xLogData.OwnsData
            ? BlueTuskPgOutputEnvelope.CreateOwned(xLogData, message)
            : new BlueTuskPgOutputEnvelope(xLogData, message);
    }

    /// <summary>Decodes one complete pgoutput message payload.</summary>
    public BlueTuskPgOutputMessage Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A pgoutput message cannot be empty.");
        }

        var reader = new BlueTuskPgOutputPayloadReader(payload);
        var code = (BlueTuskPgOutputMessageCode)reader.ReadByte();
        BlueTuskPgOutputMessage message = code switch
        {
            BlueTuskPgOutputMessageCode.Begin => DecodeBegin(ref reader),
            BlueTuskPgOutputMessageCode.Commit => DecodeCommit(ref reader),
            BlueTuskPgOutputMessageCode.Origin => DecodeOrigin(ref reader),
            BlueTuskPgOutputMessageCode.Relation => DecodeRelation(ref reader),
            BlueTuskPgOutputMessageCode.Type => DecodeType(ref reader),
            BlueTuskPgOutputMessageCode.Insert => DecodeInsert(ref reader),
            BlueTuskPgOutputMessageCode.Update => DecodeUpdate(ref reader),
            BlueTuskPgOutputMessageCode.Delete => DecodeDelete(ref reader),
            BlueTuskPgOutputMessageCode.Truncate => DecodeTruncate(ref reader),
            BlueTuskPgOutputMessageCode.Message => DecodeLogicalMessage(ref reader),
            BlueTuskPgOutputMessageCode.StreamStart => DecodeStreamStart(ref reader),
            BlueTuskPgOutputMessageCode.StreamStop => DecodeStreamStop(ref reader),
            BlueTuskPgOutputMessageCode.StreamCommit => DecodeStreamCommit(ref reader),
            BlueTuskPgOutputMessageCode.StreamAbort => DecodeStreamAbort(ref reader),
            BlueTuskPgOutputMessageCode.BeginPrepare => DecodeBeginPrepare(ref reader),
            BlueTuskPgOutputMessageCode.Prepare => DecodePrepare(ref reader),
            BlueTuskPgOutputMessageCode.CommitPrepared => DecodeCommitPrepared(ref reader),
            BlueTuskPgOutputMessageCode.RollbackPrepared => DecodeRollbackPrepared(ref reader),
            BlueTuskPgOutputMessageCode.StreamPrepare => DecodeStreamPrepare(ref reader),
            _ => throw new BlueTuskPgOutputProtocolException(
                $"Unknown pgoutput message code 0x{(byte)code:X2}."),
        };
        reader.EnsureConsumed();
        if (message is BlueTuskPgOutputStreamStart)
        {
            _insideStreamSegment = true;
        }
        else if (message is BlueTuskPgOutputStreamStop)
        {
            _insideStreamSegment = false;
        }

        return message;
    }

    private BlueTuskPgOutputBegin DecodeBegin(ref BlueTuskPgOutputPayloadReader reader)
    {
        RejectInsideStreamSegment(BlueTuskPgOutputMessageCode.Begin);
        return new BlueTuskPgOutputBegin(
            ReadPosition(ref reader),
            ReadTimestamp(ref reader),
            reader.ReadUInt32());
    }

    private BlueTuskPgOutputCommit DecodeCommit(ref BlueTuskPgOutputPayloadReader reader)
    {
        RejectInsideStreamSegment(BlueTuskPgOutputMessageCode.Commit);
        RequireZeroFlags(ref reader);
        return new BlueTuskPgOutputCommit(
            ReadPosition(ref reader),
            ReadPosition(ref reader),
            ReadTimestamp(ref reader));
    }

    private static BlueTuskPgOutputOrigin DecodeOrigin(
        ref BlueTuskPgOutputPayloadReader reader) =>
        new(ReadPosition(ref reader), reader.ReadCString());

    private BlueTuskPgOutputRelation DecodeRelation(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        var transactionId = ReadStreamingTransactionId(ref reader);
        var relationId = reader.ReadUInt32();
        var namespaceName = reader.ReadCString();
        var relationName = reader.ReadCString();
        var replicaIdentity = (char)reader.ReadByte();
        var count = ReadNonNegativeInt16(ref reader, "relation column");
        if (count > MaximumCollectionCount || count > reader.Remaining)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A relation column count exceeds the bounded message capacity.");
        }

        var columns = new BlueTuskPgOutputRelationColumn[count];
        for (var index = 0; index < columns.Length; index++)
        {
            var flags = (BlueTuskPgOutputRelationColumnOptions)reader.ReadByte();
            if ((flags & ~BlueTuskPgOutputRelationColumnOptions.Key) != 0)
            {
                throw new BlueTuskPgOutputProtocolException(
                    $"A relation column contained unsupported flags 0x{(byte)flags:X2}.");
            }

            columns[index] = new BlueTuskPgOutputRelationColumn(
                flags,
                reader.ReadCString(),
                reader.ReadUInt32(),
                reader.ReadInt32());
        }

        return new BlueTuskPgOutputRelation(
            transactionId,
            relationId,
            namespaceName,
            relationName,
            replicaIdentity,
            columns);
    }

    private BlueTuskPgOutputType DecodeType(ref BlueTuskPgOutputPayloadReader reader) =>
        new(
            ReadStreamingTransactionId(ref reader),
            reader.ReadUInt32(),
            reader.ReadCString(),
            reader.ReadCString());

    private BlueTuskPgOutputInsert DecodeInsert(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        var transactionId = ReadStreamingTransactionId(ref reader);
        var relationId = reader.ReadUInt32();
        RequireMarker(ref reader, 'N', "insert new row");
        return new BlueTuskPgOutputInsert(
            transactionId,
            relationId,
            DecodeTuple(ref reader));
    }

    private BlueTuskPgOutputUpdate DecodeUpdate(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        var transactionId = ReadStreamingTransactionId(ref reader);
        var relationId = reader.ReadUInt32();
        var marker = (char)reader.ReadByte();
        BlueTuskPgOutputOldRowKind? oldRowKind = null;
        BlueTuskPgOutputTuple? oldRow = null;
        if (marker is 'K' or 'O')
        {
            oldRowKind = marker == 'K'
                ? BlueTuskPgOutputOldRowKind.Key
                : BlueTuskPgOutputOldRowKind.Full;
            oldRow = DecodeTuple(ref reader);
            marker = (char)reader.ReadByte();
        }

        if (marker != 'N')
        {
            throw new BlueTuskPgOutputProtocolException(
                $"An update new row marker must be 'N', but was '{marker}'.");
        }

        return new BlueTuskPgOutputUpdate(
            transactionId,
            relationId,
            oldRowKind,
            oldRow,
            DecodeTuple(ref reader));
    }

    private BlueTuskPgOutputDelete DecodeDelete(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        var transactionId = ReadStreamingTransactionId(ref reader);
        var relationId = reader.ReadUInt32();
        var marker = (char)reader.ReadByte();
        var kind = marker switch
        {
            'K' => BlueTuskPgOutputOldRowKind.Key,
            'O' => BlueTuskPgOutputOldRowKind.Full,
            _ => throw new BlueTuskPgOutputProtocolException(
                $"A delete old row marker must be 'K' or 'O', but was '{marker}'."),
        };
        return new BlueTuskPgOutputDelete(
            transactionId,
            relationId,
            kind,
            DecodeTuple(ref reader));
    }

    private BlueTuskPgOutputTruncate DecodeTruncate(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        var transactionId = ReadStreamingTransactionId(ref reader);
        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A truncate message declared a negative relation count.");
        }

        if (count > MaximumCollectionCount ||
            count > (reader.Remaining - sizeof(byte)) / sizeof(uint))
        {
            throw new BlueTuskPgOutputProtocolException(
                "A truncate relation count exceeds the bounded message capacity.");
        }

        var options = (BlueTuskPgOutputTruncateOptions)reader.ReadByte();
        const BlueTuskPgOutputTruncateOptions supportedOptions =
            BlueTuskPgOutputTruncateOptions.Cascade |
            BlueTuskPgOutputTruncateOptions.RestartIdentity;
        if ((options & ~supportedOptions) != 0)
        {
            throw new BlueTuskPgOutputProtocolException(
                $"A truncate message contained unsupported options 0x{(byte)options:X2}.");
        }

        var relationIds = new uint[count];
        for (var index = 0; index < relationIds.Length; index++)
        {
            relationIds[index] = reader.ReadUInt32();
        }

        return new BlueTuskPgOutputTruncate(transactionId, options, relationIds);
    }

    private BlueTuskPgOutputLogicalMessage DecodeLogicalMessage(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        var transactionId = ReadStreamingTransactionId(ref reader);
        var transactional = ReadBoolean(ref reader, "logical message transactional flag");
        var position = ReadPosition(ref reader);
        var prefix = reader.ReadCString();
        var contentLength = reader.ReadInt32();
        if (contentLength < 0)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A logical message declared a negative content length.");
        }

        return new BlueTuskPgOutputLogicalMessage(
            transactionId,
            transactional,
            position,
            prefix,
            reader.ReadBytes(contentLength));
    }

    private BlueTuskPgOutputStreamStart DecodeStreamStart(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        RequireProtocolVersion(2, BlueTuskPgOutputMessageCode.StreamStart);
        if (_options.StreamingMode == BlueTuskPgOutputStreamingMode.Off)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A stream-start message was received when streaming was not negotiated.");
        }

        if (_insideStreamSegment)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A stream-start message cannot be nested inside a stream segment.");
        }

        var result = new BlueTuskPgOutputStreamStart(
            reader.ReadUInt32(),
            ReadBoolean(ref reader, "first stream segment flag"));
        return result;
    }

    private BlueTuskPgOutputStreamStop DecodeStreamStop(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        RequireProtocolVersion(2, BlueTuskPgOutputMessageCode.StreamStop);
        if (!_insideStreamSegment)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A stream-stop message was received outside a stream segment.");
        }

        return new BlueTuskPgOutputStreamStop();
    }

    private BlueTuskPgOutputStreamCommit DecodeStreamCommit(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        RequireOutsideStreamSegment(BlueTuskPgOutputMessageCode.StreamCommit);
        RequireProtocolVersion(2, BlueTuskPgOutputMessageCode.StreamCommit);
        var transactionId = reader.ReadUInt32();
        RequireZeroFlags(ref reader);
        return new BlueTuskPgOutputStreamCommit(
            transactionId,
            ReadPosition(ref reader),
            ReadPosition(ref reader),
            ReadTimestamp(ref reader));
    }

    private BlueTuskPgOutputStreamAbort DecodeStreamAbort(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        RequireOutsideStreamSegment(BlueTuskPgOutputMessageCode.StreamAbort);
        RequireProtocolVersion(2, BlueTuskPgOutputMessageCode.StreamAbort);
        var transactionId = reader.ReadUInt32();
        var subtransactionId = reader.ReadUInt32();
        if (_options.StreamingMode == BlueTuskPgOutputStreamingMode.Parallel)
        {
            return new BlueTuskPgOutputStreamAbort(
                transactionId,
                subtransactionId,
                ReadPosition(ref reader),
                ReadTimestamp(ref reader));
        }

        return new BlueTuskPgOutputStreamAbort(
            transactionId,
            subtransactionId,
            AbortPosition: null,
            AbortTimestamp: null);
    }

    private BlueTuskPgOutputBeginPrepare DecodeBeginPrepare(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        RequireTwoPhase(BlueTuskPgOutputMessageCode.BeginPrepare);
        return new BlueTuskPgOutputBeginPrepare(
            ReadPosition(ref reader),
            ReadPosition(ref reader),
            ReadTimestamp(ref reader),
            reader.ReadUInt32(),
            reader.ReadCString());
    }

    private BlueTuskPgOutputPrepare DecodePrepare(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        RequireTwoPhase(BlueTuskPgOutputMessageCode.Prepare);
        RequireZeroFlags(ref reader);
        return new BlueTuskPgOutputPrepare(
            ReadPosition(ref reader),
            ReadPosition(ref reader),
            ReadTimestamp(ref reader),
            reader.ReadUInt32(),
            reader.ReadCString());
    }

    private BlueTuskPgOutputCommitPrepared DecodeCommitPrepared(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        RequireTwoPhase(BlueTuskPgOutputMessageCode.CommitPrepared);
        RequireZeroFlags(ref reader);
        return new BlueTuskPgOutputCommitPrepared(
            ReadPosition(ref reader),
            ReadPosition(ref reader),
            ReadTimestamp(ref reader),
            reader.ReadUInt32(),
            reader.ReadCString());
    }

    private BlueTuskPgOutputRollbackPrepared DecodeRollbackPrepared(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        RequireTwoPhase(BlueTuskPgOutputMessageCode.RollbackPrepared);
        RequireZeroFlags(ref reader);
        return new BlueTuskPgOutputRollbackPrepared(
            ReadPosition(ref reader),
            ReadPosition(ref reader),
            ReadTimestamp(ref reader),
            ReadTimestamp(ref reader),
            reader.ReadUInt32(),
            reader.ReadCString());
    }

    private BlueTuskPgOutputStreamPrepare DecodeStreamPrepare(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        RequireOutsideStreamSegment(BlueTuskPgOutputMessageCode.StreamPrepare);
        RequireTwoPhase(BlueTuskPgOutputMessageCode.StreamPrepare);
        RequireZeroFlags(ref reader);
        return new BlueTuskPgOutputStreamPrepare(
            ReadPosition(ref reader),
            ReadPosition(ref reader),
            ReadTimestamp(ref reader),
            reader.ReadUInt32(),
            reader.ReadCString());
    }

    private uint? ReadStreamingTransactionId(ref BlueTuskPgOutputPayloadReader reader) =>
        _insideStreamSegment
            ? reader.ReadUInt32()
            : null;

    private static BlueTuskPgOutputTuple DecodeTuple(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        var count = ReadNonNegativeInt16(ref reader, "tuple column");
        if (count > MaximumCollectionCount || count > reader.Remaining)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A tuple column count exceeds the bounded message capacity.");
        }

        var values = new BlueTuskPgOutputTupleValue[count];
        for (var index = 0; index < values.Length; index++)
        {
            var kind = (BlueTuskPgOutputTupleValueKind)reader.ReadByte();
            values[index] = kind switch
            {
                BlueTuskPgOutputTupleValueKind.Null =>
                    new BlueTuskPgOutputTupleValue(kind, ReadOnlyMemory<byte>.Empty),
                BlueTuskPgOutputTupleValueKind.UnchangedToast =>
                    new BlueTuskPgOutputTupleValue(kind, ReadOnlyMemory<byte>.Empty),
                BlueTuskPgOutputTupleValueKind.Text =>
                    new BlueTuskPgOutputTupleValue(kind, ReadColumnBytes(ref reader)),
                BlueTuskPgOutputTupleValueKind.Binary =>
                    new BlueTuskPgOutputTupleValue(kind, ReadColumnBytes(ref reader)),
                _ => throw new BlueTuskPgOutputProtocolException(
                    $"A tuple column contained unknown value kind 0x{(byte)kind:X2}."),
            };
        }

        return new BlueTuskPgOutputTuple(values);
    }

    private static ReadOnlyMemory<byte> ReadColumnBytes(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A tuple column declared a negative value length.");
        }

        return reader.ReadBytes(length);
    }

    private static int ReadNonNegativeInt16(
        ref BlueTuskPgOutputPayloadReader reader,
        string description)
    {
        var count = reader.ReadInt16();
        return count >= 0
            ? count
            : throw new BlueTuskPgOutputProtocolException(
                $"A pgoutput message declared a negative {description} count.");
    }

    private static BlueTuskLogSequenceNumber ReadPosition(
        ref BlueTuskPgOutputPayloadReader reader) =>
        new(reader.ReadUInt64());

    private static DateTimeOffset ReadTimestamp(
        ref BlueTuskPgOutputPayloadReader reader)
    {
        var microseconds = reader.ReadInt64();
        try
        {
            return PostgreSqlEpoch.AddTicks(
                checked(microseconds * TimeSpan.TicksPerMicrosecond));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A pgoutput timestamp is outside the supported DateTimeOffset range.",
                exception);
        }
        catch (OverflowException exception)
        {
            throw new BlueTuskPgOutputProtocolException(
                "A pgoutput timestamp is outside the supported DateTimeOffset range.",
                exception);
        }
    }

    private static bool ReadBoolean(
        ref BlueTuskPgOutputPayloadReader reader,
        string description) =>
        reader.ReadByte() switch
        {
            0 => false,
            1 => true,
            var value => throw new BlueTuskPgOutputProtocolException(
                $"The {description} must be 0 or 1, but was {value}."),
        };

    private static void RequireZeroFlags(ref BlueTuskPgOutputPayloadReader reader)
    {
        var flags = reader.ReadByte();
        if (flags != 0)
        {
            throw new BlueTuskPgOutputProtocolException(
                $"A reserved pgoutput flags byte must be zero, but was 0x{flags:X2}.");
        }
    }

    private static void RequireMarker(
        ref BlueTuskPgOutputPayloadReader reader,
        char expected,
        string description)
    {
        var marker = (char)reader.ReadByte();
        if (marker != expected)
        {
            throw new BlueTuskPgOutputProtocolException(
                $"The {description} marker must be '{expected}', but was '{marker}'.");
        }
    }

    private void RequireProtocolVersion(
        int minimumVersion,
        BlueTuskPgOutputMessageCode code)
    {
        if (_options.ProtocolVersion < minimumVersion)
        {
            throw new BlueTuskPgOutputProtocolException(
                $"{code} requires pgoutput protocol version {minimumVersion} or later.");
        }
    }

    private void RequireTwoPhase(BlueTuskPgOutputMessageCode code)
    {
        RequireOutsideStreamSegment(code);
        RequireProtocolVersion(3, code);
        if (!_options.TwoPhase)
        {
            throw new BlueTuskPgOutputProtocolException(
                $"{code} was received when two-phase decoding was not negotiated.");
        }
    }

    private void RequireOutsideStreamSegment(BlueTuskPgOutputMessageCode code)
    {
        if (_insideStreamSegment)
        {
            throw new BlueTuskPgOutputProtocolException(
                $"{code} cannot be decoded before the current stream segment stops.");
        }
    }

    private void RejectInsideStreamSegment(BlueTuskPgOutputMessageCode code) =>
        RequireOutsideStreamSegment(code);
}
