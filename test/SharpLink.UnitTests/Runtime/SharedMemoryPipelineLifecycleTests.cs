using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class SharedMemoryPipelineLifecycleTests
{
    [Test]
    public async Task ReaderCompletionShouldWaitForAPendingReadOperation()
    {
        await using var harness = await PipelineHarness.CreateAsync();
        var reader = new SharedMemoryPipeReader(harness.Direction, harness.Control, spinCount: 0);
        var pendingField = typeof(SharedMemoryPipeReader).GetField(
            "_readOperationPending",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        pendingField.SetValue(reader, 1);

        var completion = reader.CompleteAsync().AsTask();
        var completedBeforeReadExited = completion.IsCompleted;
        if (!completion.IsCompleted)
        {
            pendingField.SetValue(reader, 0);
            var release = (TaskCompletionSource<bool>)typeof(SharedMemoryPipeReader).GetField(
                    "_readActivityReleased",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(reader)!;
            release.TrySetResult(true);
            await completion.WaitAsync(TimeSpan.FromSeconds(2));
        }

        await Assert.That(completedBeforeReadExited).IsFalse();
    }

    [Test]
    public async Task RejectedSecondReadShouldNotBreakTheActiveReadCancellation()
    {
        await using var harness = await PipelineHarness.CreateAsync();
        var reader = new SharedMemoryPipeReader(harness.Direction, harness.Control, spinCount: 0);
        using var cancellation = new CancellationTokenSource();
        var first = reader.ReadAsync(cancellation.Token).AsTask();
        var second = reader.ReadAsync().AsTask();
        await Task.Yield();

        if (second.Exception?.GetBaseException() is not InvalidOperationException)
            throw new Exception("expected the second pending read to be rejected");
        cancellation.Cancel();
        await Task.Delay(50);
        var activeReadObservedCancellation = first.IsCompleted;
        reader.Complete();
        try { await first.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

        await Assert.That(activeReadObservedCancellation).IsTrue();
    }

    [Test]
    public async Task RejectedSecondReadShouldNotBreakTheActiveReadNotification()
    {
        await using var harness = await PipelineHarness.CreateAsync();
        var reader = new SharedMemoryPipeReader(harness.Direction, harness.Control, spinCount: 0);
        var writer = new SharedMemoryPipeWriter(harness.Direction, harness.PeerControl, spinCount: 0);
        var first = reader.ReadAsync().AsTask();
        await Task.Delay(50);
        var second = reader.ReadAsync().AsTask();
        await Task.Yield();

        if (second.Exception?.GetBaseException() is not InvalidOperationException)
            throw new Exception("expected the second pending read to be rejected");
        writer.GetSpan(1)[0] = 42;
        writer.Advance(1);
        await writer.FlushAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!first.IsCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var activeReadObservedData = first.IsCompleted;
        reader.CancelPendingRead();
        var result = await first.WaitAsync(TimeSpan.FromSeconds(2));
        if (!result.Buffer.IsEmpty)
            reader.AdvanceTo(result.Buffer.End);

        await Assert.That(activeReadObservedData).IsTrue();
    }

    [Test]
    public async Task WriterCompletionShouldJoinAnActiveFlushBeforeReturning()
    {
        await using var harness = await PipelineHarness.CreateAsync();
        var writer = new SharedMemoryPipeWriter(harness.Direction, harness.Control, spinCount: 0);
        writer.Advance(writer.GetMemory(harness.Direction.Capacity).Length);
        await writer.FlushAsync();
        _ = writer.GetMemory(1);
        writer.Advance(1);

        var flush = writer.FlushAsync().AsTask();
        await Task.Delay(50);
        var completion = writer.CompleteAsync().AsTask();
        await Task.Delay(50);
        var returnedBeforeFlush = completion.IsCompleted && !flush.IsCompleted;

        harness.Direction.PublishReadPosition(harness.Direction.Capacity);
        harness.Control.PulseSpaceWaiter();
        await flush.WaitAsync(TimeSpan.FromSeconds(2));
        await completion.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(returnedBeforeFlush).IsFalse();
    }

    private sealed class PipelineHarness : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly NamedPipeClientStream _client;
        private readonly SharedMemoryMapping _mapping;

        private PipelineHarness(
            NamedPipeServerStream server,
            NamedPipeClientStream client,
            SharedMemoryMapping mapping,
            SharedMemoryControlChannel control,
            SharedMemoryControlChannel peerControl,
            SharedMemoryRingDirection direction)
        {
            _server = server;
            _client = client;
            _mapping = mapping;
            Control = control;
            PeerControl = peerControl;
            Direction = direction;
        }

        internal SharedMemoryControlChannel Control { get; }
        internal SharedMemoryControlChannel PeerControl { get; }
        internal SharedMemoryRingDirection Direction { get; }

        internal static async Task<PipelineHarness> CreateAsync()
        {
            var pipeName = $"sp{Guid.NewGuid():N}"[..20];
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var accept = server.WaitForConnectionAsync();
            await client.ConnectAsync();
            await accept;
            var nonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);
            var mapping = SharedMemoryMapping.CreateServer(64 * 1024, nonce, out _);
            var control = new SharedMemoryControlChannel(server);
            var peerControl = new SharedMemoryControlChannel(client);
            var direction = SharedMemoryLayout.GetDirection(mapping, clientToServer: true);
            return new PipelineHarness(server, client, mapping, control, peerControl, direction);
        }

        public async ValueTask DisposeAsync()
        {
            await Control.DisposeAsync();
            await PeerControl.DisposeAsync();
            await _mapping.DisposeAsync();
            await _client.DisposeAsync();
            await _server.DisposeAsync();
        }
    }
}
