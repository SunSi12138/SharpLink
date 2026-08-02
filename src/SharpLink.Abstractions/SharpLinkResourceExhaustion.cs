namespace SharpLink.Abstractions;

internal static class SharpLinkResourceExhaustion
{
    private const string ReasonDataKey = "SharpLink.ResourceExhaustionReason";

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

    internal static string GetReason(Exception exception)
        => exception.Data[ReasonDataKey] as string ?? Unspecified;
}
