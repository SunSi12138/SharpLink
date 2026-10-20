namespace SharpLink.Abstractions;

/// <summary>Defines stable machine-readable detail codes for SharpLink wire errors.</summary>
/// <remarks>
/// Detail-code namespaces are scoped by the top-level <see cref="SharpLinkErrorCode"/>. A value of
/// <see cref="Unspecified"/> means that no finer-grained classification was supplied.
/// </remarks>
public static class SharpLinkErrorDetails
{
    /// <summary>No finer-grained error detail was supplied.</summary>
    public const ushort Unspecified = 0;

    /// <summary>Stable detail codes for <see cref="SharpLinkErrorCode.Unavailable"/>.</summary>
    public static class Unavailable
    {
        /// <summary>The current routing snapshot contains no eligible target for this call.</summary>
        public const ushort NoRoutableTarget = 1;
        /// <summary>The selected local or server registration retired before a call lease was acquired.</summary>
        public const ushort RegistrationRetired = 2;
    }

    /// <summary>Stable detail codes for <see cref="SharpLinkErrorCode.FailedPrecondition"/>.</summary>
    public static class FailedPrecondition
    {
        /// <summary>The requested assembly wire identity differs from the current service registration.</summary>
        public const ushort ContractMismatch = 1;
        /// <summary>The current server registry does not satisfy the supplied registration receipt.</summary>
        public const ushort ReceiptMismatch = 2;
    }

    /// <summary>Stable detail codes for <see cref="SharpLinkErrorCode.Unimplemented"/>.</summary>
    public static class Unimplemented
    {
        /// <summary>The server has no current registration for the requested contract.</summary>
        public const ushort ContractNotRegistered = 1;
        /// <summary>The current contract does not implement the requested method.</summary>
        public const ushort MethodNotImplemented = 2;
    }

    /// <summary>Stable detail codes for <see cref="SharpLinkErrorCode.ResourceExhausted"/>.</summary>
    public static class ResourceExhausted
    {
        /// <summary>No specific resource-exhaustion reason was supplied.</summary>
        public const ushort Unspecified = SharpLinkErrorDetails.Unspecified;
        /// <summary>The server-wide concurrent-call capacity was exhausted.</summary>
        public const ushort ServerCallCapacity = 1;
        /// <summary>The per-connection concurrent-call capacity was exhausted.</summary>
        public const ushort PerConnectionCallCapacity = 2;
        /// <summary>An admission concurrency limiter rejected the call.</summary>
        public const ushort AdmissionConcurrency = 3;
        /// <summary>An admission queue was full.</summary>
        public const ushort AdmissionQueue = 4;
        /// <summary>An admission rate limiter rejected the call.</summary>
        public const ushort AdmissionRate = 5;
        /// <summary>Admission partition capacity was exhausted.</summary>
        public const ushort AdmissionPartitionCapacity = 6;
        /// <summary>An admission controller reported another bounded-capacity rejection.</summary>
        public const ushort AdmissionOther = 7;
        /// <summary>The client pending-request capacity was exhausted.</summary>
        public const ushort PendingRequestCapacity = 8;
        /// <summary>The session send-queue capacity was exhausted.</summary>
        public const ushort SendQueueCapacity = 9;
        /// <summary>The server concurrent decode budget was exhausted.</summary>
        public const ushort ServerDecodeConcurrency = 10;
        /// <summary>The server retained-compressed-bytes budget was exhausted.</summary>
        public const ushort ServerRetainedCompressedBytes = 11;
        /// <summary>The server decoded-bytes budget was exhausted.</summary>
        public const ushort ServerDecodedBytes = 12;
        /// <summary>The server decode queue was full.</summary>
        public const ushort ServerDecodeQueue = 13;
        /// <summary>The server pre-admission stream-byte budget was exhausted.</summary>
        public const ushort ServerPreAdmissionStreamBytes = 14;
    }
}
