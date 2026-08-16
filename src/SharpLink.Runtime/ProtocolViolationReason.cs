namespace SharpLink.Runtime;

/// <summary>
/// Coarse, fixed-cardinality categories for inbound Protocol v2 violations. Values are
/// mapped to stable lowercase log tokens; never derive them from attacker-controlled text.
/// </summary>
internal enum ProtocolViolationReason
{
    /// <summary>The frame did not start with the Protocol v2 magic byte.</summary>
    InvalidMagic,

    /// <summary>The frame structure, payload shape, or field encoding was malformed.</summary>
    MalformedFrame,

    /// <summary>The frame is well-formed but is not legal for the current session state.</summary>
    ProtocolState,

    /// <summary>A server-side invariant was violated; this is not attributable to wire input.</summary>
    InternalState,

    /// <summary>The violation does not fit a finer-grained category.</summary>
    Other
}

/// <summary>
/// A Protocol v2 violation that carries its stable low-cardinality classification. The
/// classification is the only thing the server may forward into structured logs; the message
/// itself must never be treated as safe log material.
/// </summary>
internal sealed class SharpLinkProtocolViolationException : SharpLinkException
{
    internal SharpLinkProtocolViolationException(ProtocolViolationReason reason, string message)
        : base(SharpLinkErrorCode.ProtocolViolation, message)
    {
        Reason = reason;
    }

    internal SharpLinkProtocolViolationException(
        ProtocolViolationReason reason,
        string message,
        Exception? innerException)
        : base(SharpLinkErrorCode.ProtocolViolation, message, innerException)
    {
        Reason = reason;
    }

    internal ProtocolViolationReason Reason { get; }

    internal static ProtocolViolationReason Classify(SharpLinkException exception)
        => exception is SharpLinkProtocolViolationException violation
            ? violation.Reason
            : ProtocolViolationReason.Other;
}

internal static class ProtocolViolationLogTokens
{
    /// <summary>Maps a fixed classification to its stable, low-cardinality log token.</summary>
    internal static string ToLogToken(this ProtocolViolationReason reason) => reason switch
    {
        ProtocolViolationReason.InvalidMagic => "invalid_magic",
        ProtocolViolationReason.MalformedFrame => "malformed_frame",
        ProtocolViolationReason.ProtocolState => "protocol_state",
        ProtocolViolationReason.InternalState => "internal_state",
        _ => "other"
    };
}
