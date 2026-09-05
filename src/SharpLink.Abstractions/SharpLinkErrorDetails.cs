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
