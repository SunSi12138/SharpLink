namespace SharpLink.Abstractions;

/// <summary>Identifies a framework or application error returned by a SharpLink endpoint.</summary>
public enum SharpLinkErrorCode
{
    /// <summary>The endpoint could not classify the error more precisely.</summary>
    Unknown = 0,
    /// <summary>The remote application reported an error without a more specific SharpLink code.</summary>
    RemoteError = 1,
    /// <summary>The peer rejected the supplied authentication credentials.</summary>
    AuthenticationRejected = 2,
    /// <summary>The supplied authentication credentials have expired.</summary>
    AuthenticationExpired = 3,
    /// <summary>The authenticated principal is not authorized to perform the operation.</summary>
    AuthorizationDenied = 4,
    /// <summary>The connection closed before the operation completed.</summary>
    ConnectionClosed = 5,
    /// <summary>The peer failed to respond within the configured heartbeat interval.</summary>
    HeartbeatTimeout = 6,
    /// <summary>The peer sent data that violates the SharpLink wire protocol.</summary>
    ProtocolViolation = 7,
    /// <summary>Unrecoverable data corruption or loss was detected.</summary>
    DataLoss = 8,
    /// <summary>The operation exceeded an admission, concurrency, memory, or other resource limit.</summary>
    ResourceExhausted = 9,
    /// <summary>The requested service is temporarily unavailable.</summary>
    Unavailable = 10,
    /// <summary>The operation was canceled before completion.</summary>
    Cancelled = 11,
    /// <summary>One or more supplied arguments are invalid.</summary>
    InvalidArgument = 12,
    /// <summary>The operation did not complete before its deadline.</summary>
    DeadlineExceeded = 13,
    /// <summary>The requested resource or operation does not exist.</summary>
    NotFound = 14,
    /// <summary>The resource being created already exists.</summary>
    AlreadyExists = 15,
    /// <summary>The caller does not have permission to access the resource.</summary>
    PermissionDenied = 16,
    /// <summary>The system is not in the state required to perform the operation.</summary>
    FailedPrecondition = 17,
    /// <summary>The operation was aborted because of a conflict or concurrent change.</summary>
    Aborted = 18,
    /// <summary>A supplied value is outside the supported range.</summary>
    OutOfRange = 19,
    /// <summary>The requested operation is recognized but not implemented.</summary>
    Unimplemented = 20,
    /// <summary>The endpoint encountered an unexpected internal failure.</summary>
    Internal = 21
}
