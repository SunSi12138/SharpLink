namespace SharpLink.Abstractions;

internal static class SharpLinkResourceExhaustion
{
    private const string ReasonDataKey = "SharpLink.ResourceExhaustionReason";
    private const char ServerCallCapacityWireCode = '\u0001';
    private const char PerConnectionCallCapacityWireCode = '\u0002';
    private const char AdmissionConcurrencyWireCode = '\u0003';
    private const char AdmissionQueueWireCode = '\u0004';
    private const char AdmissionRateWireCode = '\u0005';
    private const char AdmissionPartitionCapacityWireCode = '\u0006';
    private const char AdmissionOtherWireCode = '\u0007';
    private const char PendingRequestCapacityWireCode = '\u0008';
    private const char SendQueueCapacityWireCode = '\u0009';
    private static readonly string[] s_knownReasons =
    [
        ServerCallCapacity,
        PerConnectionCallCapacity,
        AdmissionConcurrency,
        AdmissionQueue,
        AdmissionRate,
        AdmissionPartitionCapacity,
        AdmissionOther,
        PendingRequestCapacity,
        SendQueueCapacity
    ];

    internal const string Unspecified = "unspecified";
    internal const string ServerCallCapacity = "server_call_capacity";
    internal const string PerConnectionCallCapacity = "per_connection_call_capacity";
    internal const string AdmissionConcurrency = "admission_concurrency";
    internal const string AdmissionQueue = "admission_queue";
    internal const string AdmissionRate = "admission_rate";
    internal const string AdmissionPartitionCapacity = "admission_partition_capacity";
    internal const string AdmissionOther = "admission_other";
    internal const string PendingRequestCapacity = "pending_request_capacity";
    internal const string SendQueueCapacity = "send_queue_capacity";

    internal static SharpLinkException Create(string reason, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var exception = new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, message);
        exception.Data[ReasonDataKey] = reason;
        return exception;
    }

    internal static SharpLinkException CreateWire(string reason, string message)
        => Create(reason, string.Concat(GetWireCode(reason), message));

    internal static SharpLinkException CreateRemote(
        SharpLinkErrorCode code,
        string message)
    {
        if (code != SharpLinkErrorCode.ResourceExhausted)
            return new SharpLinkException(code, message);

        if (message.Length > 0 && TryGetWireReason(message[0], out var wireReason))
            return Create(wireReason, message[1..]);

        foreach (var reason in s_knownReasons)
        {
            if (message.Contains(reason, StringComparison.Ordinal))
                return Create(reason, message);
        }
        return new SharpLinkException(code, message);
    }

    internal static string GetReason(Exception exception)
        => exception.Data[ReasonDataKey] as string ?? Unspecified;

    private static char GetWireCode(string reason)
        => reason switch
        {
            ServerCallCapacity => ServerCallCapacityWireCode,
            PerConnectionCallCapacity => PerConnectionCallCapacityWireCode,
            AdmissionConcurrency => AdmissionConcurrencyWireCode,
            AdmissionQueue => AdmissionQueueWireCode,
            AdmissionRate => AdmissionRateWireCode,
            AdmissionPartitionCapacity => AdmissionPartitionCapacityWireCode,
            AdmissionOther => AdmissionOtherWireCode,
            PendingRequestCapacity => PendingRequestCapacityWireCode,
            SendQueueCapacity => SendQueueCapacityWireCode,
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "A known resource exhaustion reason is required.")
        };

    private static bool TryGetWireReason(char code, out string reason)
    {
        reason = code switch
        {
            ServerCallCapacityWireCode => ServerCallCapacity,
            PerConnectionCallCapacityWireCode => PerConnectionCallCapacity,
            AdmissionConcurrencyWireCode => AdmissionConcurrency,
            AdmissionQueueWireCode => AdmissionQueue,
            AdmissionRateWireCode => AdmissionRate,
            AdmissionPartitionCapacityWireCode => AdmissionPartitionCapacity,
            AdmissionOtherWireCode => AdmissionOther,
            PendingRequestCapacityWireCode => PendingRequestCapacity,
            SendQueueCapacityWireCode => SendQueueCapacity,
            _ => Unspecified
        };
        return reason != Unspecified;
    }
}
