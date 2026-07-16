namespace SharpLink.Abstractions;

public enum SharpLinkErrorCode
{
    Unknown = 0,
    RemoteError = 1,
    AuthenticationRejected = 2,
    AuthenticationExpired = 3,
    AuthorizationDenied = 4,
    ConnectionClosed = 5,
    HeartbeatTimeout = 6,
    ProtocolViolation = 7,
    DataLoss = 8,
    ResourceExhausted = 9,
    Unavailable = 10,
    Cancelled = 11,
    InvalidArgument = 12,
    DeadlineExceeded = 13,
    NotFound = 14,
    AlreadyExists = 15,
    PermissionDenied = 16,
    FailedPrecondition = 17,
    Aborted = 18,
    OutOfRange = 19,
    Unimplemented = 20,
    Internal = 21
}
