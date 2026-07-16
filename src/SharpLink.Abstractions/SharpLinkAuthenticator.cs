using System.Net;

namespace SharpLink.Abstractions;

/// <summary>Creates the opaque authentication payload for each client connection handshake.</summary>
public interface ISharpLinkClientAuthenticator
{
    /// <summary>Creates a payload for one connection attempt.</summary>
    /// <param name="cancellationToken">Cancels authentication material acquisition.</param>
    ValueTask<ReadOnlyMemory<byte>> CreatePayloadAsync(CancellationToken cancellationToken);
}

/// <summary>Authenticates one server-side connection handshake.</summary>
public interface ISharpLinkServerAuthenticator
{
    /// <summary>Validates an opaque client payload without logging its contents.</summary>
    /// <param name="request">The immutable authentication request.</param>
    /// <param name="cancellationToken">Cancels authentication.</param>
    ValueTask<SharpLinkAuthenticationResult> AuthenticateAsync(
        SharpLinkAuthenticationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Contains the opaque authentication payload and peer information for one connection.</summary>
/// <param name="ConnectionId">The transport connection identifier.</param>
/// <param name="Payload">The bounded opaque handshake payload.</param>
/// <param name="LocalEndPoint">The local endpoint when exposed by the transport.</param>
/// <param name="RemoteEndPoint">The remote endpoint when exposed by the transport.</param>
public readonly record struct SharpLinkAuthenticationRequest(
    string ConnectionId,
    ReadOnlyMemory<byte> Payload,
    EndPoint? LocalEndPoint,
    EndPoint? RemoteEndPoint);

/// <summary>Creates allocation-free delegate adapters for custom authentication providers.</summary>
public static class SharpLinkAuthenticator
{
    /// <summary>Creates a client authenticator from an asynchronous payload callback.</summary>
    public static ISharpLinkClientAuthenticator CreateClient(
        Func<CancellationToken, ValueTask<ReadOnlyMemory<byte>>> createPayload)
    {
        ArgumentNullException.ThrowIfNull(createPayload);
        return new DelegateClientAuthenticator(createPayload);
    }

    /// <summary>Creates a server authenticator from an asynchronous validation callback.</summary>
    public static ISharpLinkServerAuthenticator CreateServer(
        Func<SharpLinkAuthenticationRequest, CancellationToken, ValueTask<SharpLinkAuthenticationResult>> authenticate)
    {
        ArgumentNullException.ThrowIfNull(authenticate);
        return new DelegateServerAuthenticator(authenticate);
    }

    private sealed class DelegateClientAuthenticator(
        Func<CancellationToken, ValueTask<ReadOnlyMemory<byte>>> createPayload) : ISharpLinkClientAuthenticator
    {
        public ValueTask<ReadOnlyMemory<byte>> CreatePayloadAsync(CancellationToken cancellationToken)
            => createPayload(cancellationToken);
    }

    private sealed class DelegateServerAuthenticator(
        Func<SharpLinkAuthenticationRequest, CancellationToken, ValueTask<SharpLinkAuthenticationResult>> authenticate)
        : ISharpLinkServerAuthenticator
    {
        public ValueTask<SharpLinkAuthenticationResult> AuthenticateAsync(
            SharpLinkAuthenticationRequest request,
            CancellationToken cancellationToken)
            => authenticate(request, cancellationToken);
    }
}
