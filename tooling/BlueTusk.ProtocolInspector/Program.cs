using System.Globalization;
using BlueTusk.Protocol.Capture;

return Run(args);

static int Run(string[] arguments)
{
    if (arguments.Length == 0 || arguments.Contains("--help", StringComparer.Ordinal))
    {
        WriteUsage(arguments.Length == 0 ? Console.Error : Console.Out);
        return arguments.Length == 0 ? 2 : 0;
    }

    if (arguments.Length > 2 || (arguments.Length == 2 && arguments[1] != "--hex"))
    {
        Console.Error.WriteLine("Unknown argument.");
        WriteUsage(Console.Error);
        return 2;
    }

    try
    {
        using var stream = new FileStream(arguments[0], FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new BlueTuskProtocolCaptureReader(stream);
        Console.WriteLine($"BlueTusk protocol capture created {reader.CreatedAt:O}");
        Console.WriteLine("Index  Elapsed       Flow  Message                         Bytes  Attributes");

        var index = 0;
        long totalBytes = 0;
        var frontendRecords = 0;
        var backendRecords = 0;
        while (reader.ReadRecord() is { } record)
        {
            var flow = record.Direction == BlueTuskCaptureDirection.Frontend ? "C->S" : "S->C";
            var message = GetMessageName(record);
            var attributes = record.Attributes == BlueTuskCaptureRecordAttributes.None
                ? "-"
                : record.Attributes.ToString();
            Console.WriteLine(
                $"{index,5}  {record.Elapsed.TotalMilliseconds,9:F3} ms  {flow}  {message,-30} {record.Payload.Length,6}  {attributes}");
            if (arguments.Length == 2)
            {
                Console.WriteLine(record.Attributes.HasFlag(BlueTuskCaptureRecordAttributes.Redacted)
                    ? "       [payload redacted by capture producer]"
                    : $"       {Convert.ToHexString(record.Payload.Span)}");
            }

            totalBytes = checked(totalBytes + record.Payload.Length);
            if (record.Direction == BlueTuskCaptureDirection.Frontend)
            {
                frontendRecords++;
            }
            else
            {
                backendRecords++;
            }

            index++;
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{index} records ({frontendRecords} frontend, {backendRecords} backend), {totalBytes} payload bytes"));
        return 0;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine($"Could not inspect capture: {exception.Message}");
        return 1;
    }
    catch (UnauthorizedAccessException exception)
    {
        Console.Error.WriteLine($"Could not inspect capture: {exception.Message}");
        return 1;
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine($"Could not inspect capture: {exception.Message}");
        return 1;
    }
}

static string GetMessageName(BlueTuskProtocolCaptureRecord record)
{
    if (record.Attributes.HasFlag(BlueTuskCaptureRecordAttributes.Redacted))
    {
        return "Redacted";
    }

    if (record.Attributes.HasFlag(BlueTuskCaptureRecordAttributes.Encrypted))
    {
        return "Encrypted bytes";
    }

    if (record.Payload.IsEmpty)
    {
        return "Empty payload";
    }

    var identifier = (char)record.Payload.Span[0];
    var name = record.Direction == BlueTuskCaptureDirection.Frontend
        ? GetFrontendMessageName(identifier)
        : GetBackendMessageName(identifier);
    return name is null
        ? $"Unknown 0x{(byte)identifier:X2}"
        : $"{name} ({identifier})";
}

static string? GetFrontendMessageName(char identifier) => identifier switch
{
    'B' => "Bind",
    'C' => "Close",
    'D' => "Describe",
    'E' => "Execute",
    'F' => "FunctionCall",
    'H' => "Flush",
    'P' => "Parse",
    'Q' => "Query",
    'S' => "Sync",
    'X' => "Terminate",
    'c' => "CopyDone",
    'd' => "CopyData",
    'f' => "CopyFail",
    'p' => "Password/SASL response",
    _ when identifier == '\0' => "Startup request",
    _ => null,
};

static string? GetBackendMessageName(char identifier) => identifier switch
{
    '1' => "ParseComplete",
    '2' => "BindComplete",
    '3' => "CloseComplete",
    'A' => "NotificationResponse",
    'C' => "CommandComplete",
    'D' => "DataRow",
    'E' => "ErrorResponse",
    'G' => "CopyInResponse",
    'H' => "CopyOutResponse",
    'I' => "EmptyQueryResponse",
    'K' => "BackendKeyData",
    'N' => "NoticeResponse",
    'R' => "Authentication",
    'S' => "ParameterStatus",
    'T' => "RowDescription",
    'V' => "FunctionCallResponse",
    'W' => "CopyBothResponse",
    'Z' => "ReadyForQuery",
    'c' => "CopyDone",
    'd' => "CopyData",
    'n' => "NoData",
    's' => "PortalSuspended",
    't' => "ParameterDescription",
    'v' => "NegotiateProtocolVersion",
    _ => null,
};

static void WriteUsage(TextWriter output)
{
    output.WriteLine("Usage: BlueTusk.ProtocolInspector <capture.btpc> [--hex]");
    output.WriteLine("Summarizes a versioned BlueTusk protocol capture. Payload bytes are hidden unless --hex is supplied.");
}
