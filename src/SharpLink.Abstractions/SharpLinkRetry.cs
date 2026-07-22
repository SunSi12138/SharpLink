namespace SharpLink.Abstractions;

/// <summary>Describes the completed outcome of one retryable unary attempt.</summary>
/// <param name="Method">The immutable generated RPC method descriptor.</param>
/// <param name="Attempt">The one-based attempt number.</param>
/// <param name="ErrorCode">The structured error code, or null when the failure was not a SharpLink error.</param>
/// <param name="ResponseObserved">Whether a successful response was observed for this attempt.</param>
/// <param name="Elapsed">The elapsed duration of this attempt.</param>
public readonly record struct SharpLinkRetryContext(
    RpcMethodDescriptor Method,
    int Attempt,
    SharpLinkErrorCode? ErrorCode,
    bool ResponseObserved,
    TimeSpan Elapsed);

/// <summary>Specifies whether a failed idempotent unary attempt should be retried.</summary>
/// <param name="ShouldRetry">Whether to schedule another attempt.</param>
/// <param name="Delay">The non-negative delay before the next attempt.</param>
public readonly record struct SharpLinkRetryDecision(bool ShouldRetry, TimeSpan Delay);

/// <summary>Evaluates retry decisions for explicitly idempotent unary RPC calls.</summary>
/// <remarks>
/// The client invokes this policy only after a failed attempt of a method marked <c>[Idempotent]</c>.
/// Policies are synchronous and must not execute RPC attempts, block, or mutate client topology.
/// </remarks>
public interface ISharpLinkRetryPolicy
{
    /// <summary>Evaluates the outcome of one completed unary attempt.</summary>
    /// <param name="context">The immutable method metadata and attempt outcome.</param>
    /// <returns>A decision for the next attempt.</returns>
    SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context);
}
