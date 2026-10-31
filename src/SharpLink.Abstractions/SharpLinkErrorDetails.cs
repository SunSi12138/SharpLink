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
        /// <summary>No confirmed route satisfies the exact channel identity and requirement.</summary>
        public const ushort NoBoundRoute = 100;
        /// <summary>The exact binding token is unknown in this physical session.</summary>
        public const ushort BindingUnknown = 101;
        /// <summary>The captured binding registration is retired.</summary>
        public const ushort BindingRetired = 102;
        /// <summary>The physical session is draining and cannot admit a new binding.</summary>
        public const ushort SessionBindingDraining = 103;
    }

    /// <summary>Stable detail codes for <see cref="SharpLinkErrorCode.FailedPrecondition"/>.</summary>
    public static class FailedPrecondition
    {
        /// <summary>The local selector was issued by another client.</summary>
        public const ushort ForeignLocalContract = 100;
        /// <summary>The exact local registration is absent, ambiguous, or retired.</summary>
        public const ushort LocalContractUnavailable = 101;
        /// <summary>The local descriptor, hash, or codec ownership is invalid.</summary>
        public const ushort InvalidLocalContract = 102;
        /// <summary>The method does not belong to the selected local contract.</summary>
        public const ushort MethodContractMismatch = 103;
        /// <summary>The invocation shape does not match the captured method facts.</summary>
        public const ushort MethodShapeMismatch = 104;
        /// <summary>The remote contract has a different complete RPC assembly hash.</summary>
        public const ushort BindHashMismatch = 105;
        /// <summary>The receipt does not belong to this server and registry epoch.</summary>
        public const ushort ReceiptScopeMismatch = 106;
        /// <summary>The selected registration's own publication generation is below the receipt requirement.</summary>
        public const ushort BelowMinimumVersion = 107;
        /// <summary>The selected registration does not match the receipt registration identifier.</summary>
        public const ushort ExactRegistrationMismatch = 108;
    }

    /// <summary>Stable detail codes for <see cref="SharpLinkErrorCode.NotFound"/>.</summary>
    public static class NotFound
    {
        /// <summary>The captured registration does not define the requested method.</summary>
        public const ushort UnknownBoundMethod = 100;
        /// <summary>The requested exact contract is not registered in the binding scope.</summary>
        public const ushort UnknownBoundContract = 101;
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
        /// <summary>The bounded binding-slot capacity was exhausted.</summary>
        public const ushort BindingSlots = 100;
        /// <summary>The bounded binding-operation capacity was exhausted.</summary>
        public const ushort BindingOperations = 101;
        /// <summary>The bounded binding-control publication queue was full.</summary>
        public const ushort BindingControlQueue = 102;
        /// <summary>The bounded live-demand or weak-cache capacity was exhausted.</summary>
        public const ushort BindingDemandSites = 103;
        /// <summary>The bounded per-site or client-wide preparation waiter capacity was exhausted.</summary>
        public const ushort BindingWaiters = 104;
        /// <summary>The bounded binding-maintenance queue was full.</summary>
        public const ushort BindingMaintenance = 105;
        /// <summary>The bounded binding-session tracking capacity was exhausted.</summary>
        public const ushort BindingSessionTracking = 106;
    }
}
