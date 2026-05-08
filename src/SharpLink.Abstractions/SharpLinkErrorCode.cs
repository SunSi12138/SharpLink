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
    ProtocolViolation = 7
}
