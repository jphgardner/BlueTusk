namespace BlueTusk.Sync.Webhooks;

/// <summary>Defines the versioned request and acknowledgement contract for Sync webhooks.</summary>
public static class WebhookSyncProtocol
{
    public const int CurrentFormatVersion = 1;

    public const string EventHeader = "BlueTusk-Event";

    public const string DeliveryIdHeader = "BlueTusk-Delivery-Id";

    public const string TimestampHeader = "BlueTusk-Timestamp";

    public const string SignatureHeader = "BlueTusk-Signature";

    public const string KeyIdHeader = "BlueTusk-Key-Id";

    public const string DeliveryStatusHeader = "BlueTusk-Delivery-Status";

    public const string TransformFingerprintHeader = "BlueTusk-Transform-Fingerprint";

    public const string AppliedStatus = "applied";

    public const string DuplicateStatus = "duplicate";
}
