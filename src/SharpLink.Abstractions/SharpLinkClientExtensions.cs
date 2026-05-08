using System.Runtime.ExceptionServices;

namespace SharpLink.Abstractions;

public static class SharpLinkClientExtensions
{
    public static async Task ConnectOrThrowAsync(this ISharpLinkClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (await client.ConnectAsync(ct).ConfigureAwait(false))
            return;

        if (client is ISharpLinkClientDiagnostics diagnostics &&
            diagnostics.LastConnectionException is { } lastConnectionException)
        {
            ExceptionDispatchInfo.Capture(lastConnectionException).Throw();
        }

        throw new SharpLinkException(
            SharpLinkErrorCode.AuthenticationRejected,
            "SharpLink client connection was rejected during handshake.");
    }
}
