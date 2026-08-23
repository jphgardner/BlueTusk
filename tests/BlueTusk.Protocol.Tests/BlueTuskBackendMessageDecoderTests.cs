using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace BlueTusk.Protocol.Tests;

public sealed class BlueTuskBackendMessageDecoderTests
{
    [Fact]
    public void Decodes_sasl_mechanisms()
    {
        var payload = new ArrayBufferWriter<byte>();
        WriteInt32(payload, 10);
        WriteCString(payload, "SCRAM-SHA-256-PLUS");
        WriteCString(payload, "SCRAM-SHA-256");
        WriteByte(payload, 0);

        var request = Assert.IsType<BlueTuskAuthenticationRequest.Sasl>(
            BlueTuskBackendMessageDecoder.DecodeAuthentication(Message('R', payload)));

        Assert.Equal(["SCRAM-SHA-256-PLUS", "SCRAM-SHA-256"], request.Mechanisms);
    }

    [Fact]
    public void Decodes_gssapi_and_sspi_authentication_requests()
    {
        var gss = new ArrayBufferWriter<byte>();
        WriteInt32(gss, 7);
        var continuation = new ArrayBufferWriter<byte>();
        WriteInt32(continuation, 8);
        WriteBytes(continuation, new byte[] { 0, 1, 255 });
        var sspi = new ArrayBufferWriter<byte>();
        WriteInt32(sspi, 9);

        Assert.IsType<BlueTuskAuthenticationRequest.Gss>(
            BlueTuskBackendMessageDecoder.DecodeAuthentication(Message('R', gss)));
        var decodedContinuation = Assert.IsType<BlueTuskAuthenticationRequest.GssContinue>(
            BlueTuskBackendMessageDecoder.DecodeAuthentication(Message('R', continuation)));
        Assert.Equal(new byte[] { 0, 1, 255 }, decodedContinuation.Data.ToArray());
        Assert.IsType<BlueTuskAuthenticationRequest.Sspi>(
            BlueTuskBackendMessageDecoder.DecodeAuthentication(Message('R', sspi)));
    }

    [Fact]
    public void Decodes_startup_metadata()
    {
        var parameter = new ArrayBufferWriter<byte>();
        WriteCString(parameter, "server_version");
        WriteCString(parameter, "19beta3");
        var keyData = new ArrayBufferWriter<byte>();
        WriteInt32(keyData, 123);
        WriteInt32(keyData, 456);

        Assert.Equal(
            new BlueTuskParameterStatus("server_version", "19beta3"),
            BlueTuskBackendMessageDecoder.DecodeParameterStatus(Message('S', parameter)));
        Assert.Equal(
            new BlueTuskBackendKeyData(123, 456),
            BlueTuskBackendMessageDecoder.DecodeBackendKeyData(Message('K', keyData)));
        Assert.Equal(
            BlueTuskTransactionStatus.Idle,
            BlueTuskBackendMessageDecoder.DecodeReadyForQuery(Message('Z', new byte[] { (byte)'I' })));
    }

    [Fact]
    public void Decodes_notification_responses()
    {
        var notification = new ArrayBufferWriter<byte>();
        WriteInt32(notification, 1234);
        WriteCString(notification, "order-events");
        WriteCString(notification, "created \U0001F9A3");

        Assert.Equal(
            new BlueTuskNotificationResponse(1234, "order-events", "created \U0001F9A3"),
            BlueTuskBackendMessageDecoder.DecodeNotificationResponse(Message('A', notification)));
    }

    [Fact]
    public void Rejects_malformed_notification_responses()
    {
        var missingPayload = new ArrayBufferWriter<byte>();
        WriteInt32(missingPayload, 1234);
        WriteCString(missingPayload, "orders");

        var trailingData = new ArrayBufferWriter<byte>();
        WriteInt32(trailingData, 1234);
        WriteCString(trailingData, "orders");
        WriteCString(trailingData, "ready");
        WriteByte(trailingData, 1);

        Assert.Throws<BlueTuskProtocolException>(
            () => BlueTuskBackendMessageDecoder.DecodeNotificationResponse(
                Message('A', missingPayload)));
        Assert.Throws<BlueTuskProtocolException>(
            () => BlueTuskBackendMessageDecoder.DecodeNotificationResponse(
                Message('A', trailingData)));
    }

    [Fact]
    public void Decodes_row_description_and_data_row()
    {
        var description = new ArrayBufferWriter<byte>();
        WriteInt16(description, 2);
        WriteField(description, "answer", typeOid: 23, typeSize: 4);
        WriteField(description, "note", typeOid: 25, typeSize: -1);
        var row = new ArrayBufferWriter<byte>();
        WriteInt16(row, 2);
        WriteInt32(row, 2);
        WriteBytes(row, "42"u8);
        WriteInt32(row, -1);

        var fields = BlueTuskBackendMessageDecoder.DecodeRowDescription(Message('T', description));
        var values = BlueTuskBackendMessageDecoder.DecodeDataRow(Message('D', row), fields.Count);

        Assert.Equal("answer", fields[0].Name);
        Assert.Equal((uint)23, fields[0].TypeOid);
        Assert.Equal("42"u8.ToArray(), values.Values[0]!.Value.ToArray());
        Assert.Null(values.Values[1]);
    }

    [Fact]
    public void Decodes_structured_errors_without_losing_unknown_fields()
    {
        var payload = new ArrayBufferWriter<byte>();
        WriteByte(payload, (byte)'S');
        WriteCString(payload, "ERROR");
        WriteByte(payload, (byte)'C');
        WriteCString(payload, "42601");
        WriteByte(payload, (byte)'M');
        WriteCString(payload, "syntax error");
        WriteByte(payload, (byte)'Z');
        WriteCString(payload, "future field");
        WriteByte(payload, 0);

        var error = BlueTuskBackendMessageDecoder.DecodeErrorOrNotice(Message('E', payload));

        Assert.Equal("42601", error.SqlState);
        Assert.Equal("syntax error", error.Message);
        Assert.Equal("future field", error.Fields['Z']);
    }

    [Fact]
    public void Decodes_copy_responses_and_data()
    {
        var response = new ArrayBufferWriter<byte>();
        WriteByte(response, (byte)BlueTuskCopyFormat.Binary);
        WriteInt16(response, 3);
        WriteInt16(response, (short)BlueTuskCopyFormat.Binary);
        WriteInt16(response, (short)BlueTuskCopyFormat.Text);
        WriteInt16(response, (short)BlueTuskCopyFormat.Binary);

        var decoded = BlueTuskBackendMessageDecoder.DecodeCopyResponse(Message('H', response));
        Assert.Equal(BlueTuskCopyFormat.Binary, decoded.Format);
        Assert.Equal(
            [
                BlueTuskCopyFormat.Binary,
                BlueTuskCopyFormat.Text,
                BlueTuskCopyFormat.Binary,
            ],
            decoded.ColumnFormats);
        Assert.Equal(
            new byte[] { 0, 1, 2, 255 },
            BlueTuskBackendMessageDecoder.DecodeCopyData(
                Message('d', new byte[] { 0, 1, 2, 255 })));
    }

    [Fact]
    public void Rejects_invalid_copy_response_formats()
    {
        Assert.Throws<BlueTuskProtocolException>(
            () => BlueTuskBackendMessageDecoder.DecodeCopyResponse(
                Message('G', new byte[] { 2, 0, 0 })));
    }

    [Fact]
    public void Rejects_a_data_row_with_an_invalid_length()
    {
        var payload = new ArrayBufferWriter<byte>();
        WriteInt16(payload, 1);
        WriteInt32(payload, -2);

        Assert.Throws<BlueTuskProtocolException>(
            () => BlueTuskBackendMessageDecoder.DecodeDataRow(Message('D', payload)));
    }

    [Fact]
    public void Rejects_collection_counts_that_exceed_bounded_payload_capacity()
    {
        var rowDescription = new ArrayBufferWriter<byte>();
        WriteInt16(rowDescription, 4097);
        var dataRow = new ArrayBufferWriter<byte>();
        WriteInt16(dataRow, 4097);
        var copyResponse = new ArrayBufferWriter<byte>();
        WriteByte(copyResponse, (byte)BlueTuskCopyFormat.Binary);
        WriteInt16(copyResponse, 4097);

        Assert.Throws<BlueTuskProtocolException>(
            () => BlueTuskBackendMessageDecoder.DecodeRowDescription(
                Message('T', rowDescription)));
        Assert.Throws<BlueTuskProtocolException>(
            () => BlueTuskBackendMessageDecoder.DecodeDataRow(
                Message('D', dataRow)));
        Assert.Throws<BlueTuskProtocolException>(
            () => BlueTuskBackendMessageDecoder.DecodeCopyResponse(
                Message('H', copyResponse)));
    }

    private static BlueTuskBackendMessage Message(char code, ArrayBufferWriter<byte> payload) =>
        Message(code, payload.WrittenMemory);

    private static BlueTuskBackendMessage Message(char code, ReadOnlyMemory<byte> payload) =>
        new((byte)code, new ReadOnlySequence<byte>(payload));

    private static void WriteField(ArrayBufferWriter<byte> output, string name, uint typeOid, short typeSize)
    {
        WriteCString(output, name);
        WriteInt32(output, 0);
        WriteInt16(output, 0);
        WriteInt32(output, unchecked((int)typeOid));
        WriteInt16(output, typeSize);
        WriteInt32(output, -1);
        WriteInt16(output, 0);
    }

    private static void WriteCString(ArrayBufferWriter<byte> output, string value)
    {
        WriteBytes(output, Encoding.UTF8.GetBytes(value));
        WriteByte(output, 0);
    }

    private static void WriteInt16(ArrayBufferWriter<byte> output, short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(output.GetSpan(sizeof(short)), value);
        output.Advance(sizeof(short));
    }

    private static void WriteInt32(ArrayBufferWriter<byte> output, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(output.GetSpan(sizeof(int)), value);
        output.Advance(sizeof(int));
    }

    private static void WriteBytes(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> value)
    {
        value.CopyTo(output.GetSpan(value.Length));
        output.Advance(value.Length);
    }

    private static void WriteByte(ArrayBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }
}
