namespace SharpLink.Abstractions;

/// <summary>Describes whether an endpoint may accept one client attempt.</summary>
/// <param name="IsAllowed">Whether the endpoint may receive the attempt.</param>
/// <param name="Token">An opaque policy-owned token returned unchanged to <see cref="ISharpLinkEndpointAdmissionPolicy.Report"/>.</param>
/// <param name="RetryAfter">An optional non-negative wait before a rejected endpoint may be considered again.</param>
public readonly record struct SharpLinkEndpointAdmissionDecision(
    bool IsAllowed,
    long Token,
    TimeSpan? RetryAfter);

/// <summary>Classifies the terminal result of one endpoint-bound client attempt.</summary>
public enum SharpLinkEndpointOutcomeKind : byte
{
    /// <summary>The endpoint returned a successful response or locally completed a one-way call.</summary>
    Success,
    /// <summary>The endpoint returned a structured RPC error.</summary>
    RemoteError,
    /// <summary>The request could not be sent.</summary>
    SendFailure,
    /// <summary>The connection closed before the attempt completed.</summary>
    ConnectionClosed,
    /// <summary>The endpoint asked the client to stop accepting calls on this connection.</summary>
    GoAway,
    /// <summary>The caller cancelled the attempt.</summary>
    Cancelled,
    /// <summary>The effective deadline expired.</summary>
    DeadlineExceeded
}

/// <summary>Contains one completed endpoint-bound client attempt for admission policy reporting.</summary>
/// <param name="Endpoint">The immutable endpoint candidate selected for the attempt.</param>
/// <param name="Method">The generated RPC method descriptor.</param>
/// <param name="Kind">The terminal attempt classification.</param>
/// <param name="ErrorCode">The structured RPC error code when available.</param>
/// <param name="ResponseObserved">Whether a valid Response or Error frame matched the pending call.</param>
/// <param name="Elapsed">The time from endpoint selection to terminal completion.</param>
public readonly record struct SharpLinkEndpointOutcome(
    SharpLinkEndpointCandidate Endpoint,
    RpcMethodDescriptor Method,
    SharpLinkEndpointOutcomeKind Kind,
    SharpLinkErrorCode? ErrorCode,
    bool ResponseObserved,
    TimeSpan Elapsed);

/// <summary>Controls whether a specific endpoint may receive a client RPC attempt.</summary>
/// <remarks>
/// Implementations run synchronously on the client attempt path. They must not block, perform I/O,
/// retain client connection/session objects, or initiate RPC calls. <see cref="Report"/> may be called
/// from an I/O completion path and must not throw; thrown report exceptions are logged and ignored.
/// </remarks>
public interface ISharpLinkEndpointAdmissionPolicy
{
    /// <summary>Attempts to acquire permission to send one RPC attempt to an endpoint.</summary>
    SharpLinkEndpointAdmissionDecision TryAcquire(
        in SharpLinkEndpointCandidate endpoint,
        in RpcMethodDescriptor method);

    /// <summary>Reports the single terminal outcome for a successfully acquired endpoint attempt.</summary>
    void Report(in SharpLinkEndpointOutcome outcome, long token);
}
