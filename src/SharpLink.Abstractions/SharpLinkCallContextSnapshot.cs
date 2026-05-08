namespace SharpLink.Abstractions;

public sealed class SharpLinkCallContextSnapshot(
    string sessionId,
    SharpLinkAuthenticationContext? authentication)
{
    public string SessionId { get; } = sessionId;
    public SharpLinkAuthenticationContext? Authentication { get; } = authentication;
}
