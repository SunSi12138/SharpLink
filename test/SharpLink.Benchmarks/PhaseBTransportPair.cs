using System.IO.Pipelines;
using System.Net;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class PhaseBTransportPair
{
    internal static async Task<(ITransportConnection Client, ITransportConnection Server)> CreateAsync(string kind)
    {
        if (kind == "pipe")
        {
            var c2s = new Pipe(); var s2c = new Pipe();
            return (new InMemoryTransport(s2c.Reader, c2s.Writer), new InMemoryTransport(c2s.Reader, s2c.Writer));
        }
        SocketTransportOptions? socketOptions = null;
#if SHARPLINK_READY_WRITER_EXPERIMENT
        var receiveBufferText = Environment.GetEnvironmentVariable("SHARPLINK_READY_TCP_RECEIVE_BUFFER");
        if (!string.IsNullOrEmpty(receiveBufferText) && receiveBufferText != "0")
        {
            if (receiveBufferText != "262144")
                throw new ArgumentException("Only the reviewed 262144-byte ready-writer TCP evidence control is supported.");
            socketOptions = new SocketTransportOptions { ReceiveBufferBytes = 262144 };
        }
#endif
        var name = "phase-b-" + Guid.NewGuid().ToString("N");
        await using IServerTransportListener listener = kind == "tcp"
            ? new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0), options: socketOptions)
            : new SharedMemoryServerTransportListener(name);
        await using IClientTransportFactory factory = kind == "tcp"
            ? new SocketClientTransportFactory(listener.LocalEndPoint!, socketOptions)
            : new SharedMemoryClientTransportFactory(name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepting = listener.AcceptAsync(timeout.Token).AsTask();
        ITransportConnection? client = null;
        try
        {
            client = await factory.ConnectAsync(timeout.Token);
            return (client, await accepting);
        }
        catch
        {
            timeout.Cancel();
            if (client is not null) await client.DisposeAsync();
            try { var orphan = await accepting; await orphan.DisposeAsync(); }
            catch (Exception) { } // Preserve the original connect/accept failure.
            throw;
        }
    }

    private sealed class InMemoryTransport(PipeReader input, PipeWriter output) : ITransportConnection
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public PipeReader Input => input;
        public PipeWriter Output => output;
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;
        public async ValueTask DisposeAsync() { await output.CompleteAsync(); await input.CompleteAsync(); }
    }
}
