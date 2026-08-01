using System.Net;

namespace SharpLink.Abstractions;

/// <summary>Creates independent outbound transport connections.</summary>
public interface IClientTransportFactory : IAsyncDisposable
{
    /// <summary>Creates and connects a new transport connection.</summary>
    /// <param name="cancellationToken">Cancels only this connection attempt.</param>
    /// <returns>A newly owned connection.</returns>
    ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Owns a server listening resource and accepts independent connections.</summary>
public interface IServerTransportListener : IAsyncDisposable
{
    /// <summary>Gets the bound endpoint when the transport exposes one.</summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>Accepts one independently owned connection.</summary>
    /// <param name="cancellationToken">Cancels this accept operation.</param>
    /// <returns>The accepted connection.</returns>
    ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default);
}

/// <summary>Represents one duplex byte transport connection.</summary>
public interface ITransportConnection : IAsyncDisposable
{
    /// <summary>Gets the stable connection identifier.</summary>
    string Id { get; }

    /// <summary>Gets the inbound pipeline.</summary>
    PipeReader Input { get; }

    /// <summary>Gets the outbound pipeline.</summary>
    PipeWriter Output { get; }

    /// <summary>Gets the local endpoint when available.</summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>Gets the remote endpoint when available.</summary>
    EndPoint? RemoteEndPoint { get; }
}
