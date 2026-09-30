namespace BlueTusk.Sync.Kafka;

public static class KafkaSyncProtocol
{
    public const int CurrentFormatVersion = 1;

    public const string EventHeader = "bluetusk.event";

    public const string DeliveryIdHeader = "bluetusk.delivery-id";

    public const string FormatVersionHeader = "bluetusk.format-version";

    public const string TransformFingerprintHeader = "bluetusk.transform-fingerprint";
}

public class KafkaSyncException : Exception
{
    public KafkaSyncException(string message)
        : base(message)
    {
    }

    public KafkaSyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class KafkaSyncConfigurationException : KafkaSyncException
{
    public KafkaSyncConfigurationException(string message)
        : base(message)
    {
    }
}

public sealed class KafkaSyncDeliveryException : KafkaSyncException
{
    public KafkaSyncDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class KafkaSyncEnvelopeException : KafkaSyncException
{
    public KafkaSyncEnvelopeException(string message)
        : base(message)
    {
    }
}
