using SharpLink.Runtime;
using SharpLink.Abstractions;
using System;
using SharpLink.Client;
using SharpLink.Server;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.LoadTestBase;

public static class LoadTestTransportFactory
{
    public static ISharpLinkServer CreateServer(
        TransportMode transport,
        string bindIp,
        int port,
        string udsPath,
        string pipeName,
        int heartbeatCheckIntervalSeconds,
        int heartbeatTimeoutSeconds,
        Func<SharpLinkServerBuilder, SharpLinkServerBuilder> configure)
    {
        var builder = configure(SharpLinkServerBuilder.Create())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatCheckIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds));

        return transport switch
        {
            TransportMode.Tcp => builder.UseTcp(port, bindIp).Build(),
            TransportMode.Uds => builder.UseUds(udsPath).Build(),
            TransportMode.NamedPipe => builder.UseNamedPipe(pipeName).Build(),
            TransportMode.AnonymousPipe => throw new InvalidOperationException("Anonymous pipe transport only supports --mode local."),
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };
    }

    public static ISharpLinkClient CreateClient(
        TransportMode transport,
        string host,
        int port,
        string udsPath,
        string pipeName,
        int heartbeatIntervalSeconds,
        int heartbeatTimeoutSeconds)
    {
        var builder = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds));

        return transport switch
        {
            TransportMode.Tcp => builder.UseTcp(host, port).Build(),
            TransportMode.Uds => builder.UseUds(udsPath).Build(),
            TransportMode.NamedPipe => builder.UseNamedPipe(pipeName).Build(),
            TransportMode.AnonymousPipe => throw new InvalidOperationException("Anonymous pipe transport only supports --mode local."),
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };
    }

    public static LocalHarness CreateLocalHarness(
        TransportMode transport,
        string host,
        string bindIp,
        int port,
        string udsPath,
        string pipeName,
        int heartbeatIntervalSeconds,
        int heartbeatCheckIntervalSeconds,
        int heartbeatTimeoutSeconds,
        Func<SharpLinkServerBuilder, SharpLinkServerBuilder> configure)
    {
        if (transport != TransportMode.AnonymousPipe)
        {
            var server = CreateServer(transport, bindIp, port, udsPath, pipeName, heartbeatCheckIntervalSeconds, heartbeatTimeoutSeconds, configure);
            var client = CreateClient(transport, host, port, udsPath, pipeName, heartbeatIntervalSeconds, heartbeatTimeoutSeconds);
            return new LocalHarness(server, client, static () => { });
        }

        var offerTcs = new TaskCompletionSource<AnonymousPipeOffer>(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverBuilder = configure(SharpLinkServerBuilder.Create())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatCheckIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds));
        var serverAnonymous = serverBuilder.UseAnonymousPipe(
            (offer, _) =>
            {
                offerTcs.TrySetResult(offer);
                return ValueTask.CompletedTask;
            }).Build();

        var clientAnonymous = SharpClientBuilder.Create()
            .UseTransport(new DeferredAnonymousPipeClientTransport(offerTcs.Task))
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromSeconds(heartbeatIntervalSeconds), TimeSpan.FromSeconds(heartbeatTimeoutSeconds))
            .Build();

        return new LocalHarness(serverAnonymous, clientAnonymous, static () => { });
    }
}

internal sealed class DeferredAnonymousPipeClientTransport(Task<AnonymousPipeOffer> offerTask) : ITransport
{
    private readonly Task<AnonymousPipeOffer> _offerTask = offerTask ?? throw new ArgumentNullException(nameof(offerTask));
    private AnonymousPipeTransport? _inner;
    private int _connected;
    private bool _disposed;

    public async Task<IRpcSession> ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _connected, 1) != 0)
            throw new InvalidOperationException("Deferred anonymous pipe transport only supports one connection.");

        var offer = await _offerTask.WaitAsync(ct);
        _inner = new AnonymousPipeTransport(offer.InHandle, offer.OutHandle);
        return await _inner.ConnectAsync(ct);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _inner?.Dispose();
    }
}
