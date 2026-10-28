using System.IO.Pipes;
using System.Threading.Channels;
using PipeStreamOptions = System.IO.Pipes.PipeOptions;

namespace SharpLink.IntegrationTests;

// Diagnostic overlay only: this file is copied into an isolated worktree, never the product test tree.
public class SharedMemoryHandshakeBacklogDiagnosticTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GatedCleanupMustDistinguishOldBacklogFromRearmedListener(bool waitForRearm)
    {
        Ensure(OperatingSystem.IsLinux(), "this diagnostic is explicitly Linux-only");
        foreach (var assembly in new[] { typeof(NamedPipeServerStream).Assembly, typeof(object).Assembly })
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant();
            Console.WriteLine($"NONCE_RUNTIME {Path.GetFileName(assembly.Location)} sha256={hash} location={assembly.Location}");
        }
        var name = $"gate{Guid.NewGuid():N}"[..20];
        await using var listener = new SharedMemoryServerTransportListener(name);
        var iterations = Channel.CreateUnbounded<int>();
        var iteration = 0;
        listener.AcceptIterationStartedForTesting = () =>
            iterations.Writer.TryWrite(Interlocked.Increment(ref iteration));
        var atWindow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWindow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.RejectedMappingDisposedForDiagnostics = () =>
        {
            atWindow.TrySetResult();
            return releaseWindow.Task;
        };
        var accept = listener.AcceptAsync().AsTask();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ITransportConnection? client = null;
        ITransportConnection? server = null;
        try
        {
            await ExpectIterationAsync(1);
            await using var bad = new NamedPipeClientStream(".", $"shm-{name}", PipeDirection.InOut,
                PipeStreamOptions.Asynchronous | PipeStreamOptions.CurrentUserOnly);
            await bad.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var nonce = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            var hello = new byte[48];
            BinaryPrimitives.WriteInt32LittleEndian(hello, 0x53484D31);
            BinaryPrimitives.WriteInt32LittleEndian(hello.AsSpan(4), 3);
            BinaryPrimitives.WriteInt32LittleEndian(hello.AsSpan(8), 64 * 1024);
            BinaryPrimitives.WriteInt32LittleEndian(hello.AsSpan(12), Environment.ProcessId);
            nonce.CopyTo(hello, 16);
            await bad.WriteAsync(hello).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var header = new byte[52];
            await bad.ReadExactlyAsync(header).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var pathLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
            Ensure(pathLength is > 0 and <= 3072, "valid server mapping path length");
            var path = new byte[pathLength];
            await bad.ReadExactlyAsync(path).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var mappingPath = System.Text.Encoding.UTF8.GetString(path);
            Ensure(File.Exists(mappingPath), "mapping must exist before rejection");
            var ack = new byte[40];
            BinaryPrimitives.WriteInt32LittleEndian(ack, 0x53484D31);
            BinaryPrimitives.WriteInt32LittleEndian(ack.AsSpan(4), 3);
            nonce.CopyTo(ack, 8);
            ack[8] ^= 1;
            await bad.WriteAsync(ack).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await atWindow.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!File.Exists(mappingPath) && !accept.IsCompleted && iteration == 1,
                "gate must hold after mapping removal and before old-pipe disposal/rearm");

            await using var factory = new SharedMemoryClientTransportFactory(name);
            var connects = 0;
            factory.PipeConnectedForDiagnostics = () =>
            {
                Interlocked.Increment(ref connects);
                connected.TrySetResult();
                return allowHello.Task;
            };
            if (waitForRearm)
            {
                releaseWindow.TrySetResult();
                await ExpectIterationAsync(2);
            }
            var connection = factory.ConnectAsync().AsTask(); // Exactly one valid attempt per case.
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (!waitForRearm)
            {
                Ensure(!releaseWindow.Task.IsCompleted && iteration == 1,
                    "early client must connect while the old listener is still physically held");
                releaseWindow.TrySetResult();
                await ExpectIterationAsync(2); // Old listening socket has now been disposed.
            }
            allowHello.TrySetResult();
            if (waitForRearm)
            {
                client = await connection.WaitAsync(TimeSpan.FromSeconds(2));
                server = await accept.WaitAsync(TimeSpan.FromSeconds(2));
                await ExchangeAsync(client, server, 0x45);
                await ExchangeAsync(server, client, 0x67);
                Console.WriteLine($"NONCE_GATE waitForRearm=True connects={connects} result=success exchange=both");
            }
            else
            {
                Exception? failure = null;
                try { client = await connection.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception exception) { failure = exception; }
                Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Unavailable, InnerException: IOException },
                    $"old-backlog connection must fail as Unavailable/IOException, observed {failure}");
                Ensure(!accept.IsCompleted, "failed old-backlog client must not complete the rearmed original accept");
                Console.WriteLine($"NONCE_GATE waitForRearm=False connects={connects} result=Unavailable/IOException");
            }
            Ensure(connects == 1, "diagnostic must not retry the valid connection");
        }
        finally
        {
            releaseWindow.TrySetResult();
            allowHello.TrySetResult();
            if (client is not null) await client.DisposeAsync();
            if (server is not null) await server.DisposeAsync();
            await listener.DisposeAsync();
            try { _ = await accept.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
        }

        async Task ExpectIterationAsync(int expected)
        {
            var next = iterations.Reader.ReadAsync().AsTask();
            await Task.WhenAny(next, accept).WaitAsync(TimeSpan.FromSeconds(2));
            if (accept.IsCompleted)
            {
                await using var unexpected = await accept;
                throw new InvalidOperationException("original accept completed unexpectedly");
            }
            Ensure(await next == expected, "unexpected accept iteration");
        }
    }

    private static async Task ExchangeAsync(ITransportConnection sender, ITransportConnection receiver, byte value)
    {
        var flush = await sender.Output.WriteAsync(new[] { value }).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!flush.IsCanceled && !flush.IsCompleted, "output unexpectedly closed");
        var read = await receiver.Input.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        try { Ensure(read.Buffer.ToArray().SequenceEqual(new[] { value }), "recovered bytes differ"); }
        finally { receiver.Input.AdvanceTo(read.Buffer.End); }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
