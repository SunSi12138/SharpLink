namespace SharpLink.Abstractions;

internal static class SharpLinkResourceExhaustion
{
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
    internal const string ServerDecodeConcurrency = "server_decode_concurrency";
    internal const string ServerRetainedCompressedBytes = "server_retained_compressed_bytes";
    internal const string ServerDecodedBytes = "server_decoded_bytes";
    internal const string ServerDecodeQueue = "server_decode_queue";
    internal const string ServerPreAdmissionStreamBytes = "server_pre_admission_stream_bytes";

    internal static SharpLinkException Create(string reason, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new SharpLinkException(
            SharpLinkErrorCode.ResourceExhausted,
            GetDetailCode(reason),
            message);
    }

    internal static SharpLinkException CreateWire(string reason, string message)
        => Create(reason, message);

    internal static SharpLinkException CreateRemote(
        SharpLinkErrorCode code,
        ushort detailCode,
        string message)
        => new(code, detailCode, message);

    internal static SharpLinkException CreateRemote(
        SharpLinkErrorCode code,
        string message)
        => new(code, message);

    internal static string GetReason(Exception exception)
        => exception is SharpLinkException
        {
            Code: SharpLinkErrorCode.ResourceExhausted
        } sharpLinkException
            ? GetReason(sharpLinkException.DetailCode)
            : Unspecified;

    internal static ushort GetDetailCode(string reason)
        => reason switch
        {
            ServerCallCapacity => SharpLinkErrorDetails.ResourceExhausted.ServerCallCapacity,
            PerConnectionCallCapacity => SharpLinkErrorDetails.ResourceExhausted.PerConnectionCallCapacity,
            AdmissionConcurrency => SharpLinkErrorDetails.ResourceExhausted.AdmissionConcurrency,
            AdmissionQueue => SharpLinkErrorDetails.ResourceExhausted.AdmissionQueue,
            AdmissionRate => SharpLinkErrorDetails.ResourceExhausted.AdmissionRate,
            AdmissionPartitionCapacity => SharpLinkErrorDetails.ResourceExhausted.AdmissionPartitionCapacity,
            AdmissionOther => SharpLinkErrorDetails.ResourceExhausted.AdmissionOther,
            PendingRequestCapacity => SharpLinkErrorDetails.ResourceExhausted.PendingRequestCapacity,
            SendQueueCapacity => SharpLinkErrorDetails.ResourceExhausted.SendQueueCapacity,
            ServerDecodeConcurrency => SharpLinkErrorDetails.ResourceExhausted.ServerDecodeConcurrency,
            ServerRetainedCompressedBytes => SharpLinkErrorDetails.ResourceExhausted.ServerRetainedCompressedBytes,
            ServerDecodedBytes => SharpLinkErrorDetails.ResourceExhausted.ServerDecodedBytes,
            ServerDecodeQueue => SharpLinkErrorDetails.ResourceExhausted.ServerDecodeQueue,
            ServerPreAdmissionStreamBytes => SharpLinkErrorDetails.ResourceExhausted.ServerPreAdmissionStreamBytes,
            _ => SharpLinkErrorDetails.ResourceExhausted.Unspecified
        };

    internal static string GetReason(ushort detailCode)
        => detailCode switch
        {
            SharpLinkErrorDetails.ResourceExhausted.ServerCallCapacity => ServerCallCapacity,
            SharpLinkErrorDetails.ResourceExhausted.PerConnectionCallCapacity => PerConnectionCallCapacity,
            SharpLinkErrorDetails.ResourceExhausted.AdmissionConcurrency => AdmissionConcurrency,
            SharpLinkErrorDetails.ResourceExhausted.AdmissionQueue => AdmissionQueue,
            SharpLinkErrorDetails.ResourceExhausted.AdmissionRate => AdmissionRate,
            SharpLinkErrorDetails.ResourceExhausted.AdmissionPartitionCapacity => AdmissionPartitionCapacity,
            SharpLinkErrorDetails.ResourceExhausted.AdmissionOther => AdmissionOther,
            SharpLinkErrorDetails.ResourceExhausted.PendingRequestCapacity => PendingRequestCapacity,
            SharpLinkErrorDetails.ResourceExhausted.SendQueueCapacity => SendQueueCapacity,
            SharpLinkErrorDetails.ResourceExhausted.ServerDecodeConcurrency => ServerDecodeConcurrency,
            SharpLinkErrorDetails.ResourceExhausted.ServerRetainedCompressedBytes => ServerRetainedCompressedBytes,
            SharpLinkErrorDetails.ResourceExhausted.ServerDecodedBytes => ServerDecodedBytes,
            SharpLinkErrorDetails.ResourceExhausted.ServerDecodeQueue => ServerDecodeQueue,
            SharpLinkErrorDetails.ResourceExhausted.ServerPreAdmissionStreamBytes => ServerPreAdmissionStreamBytes,
            _ => Unspecified
        };
}
