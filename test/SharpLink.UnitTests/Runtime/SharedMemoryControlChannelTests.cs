using System.Diagnostics.Metrics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class SharedMemoryControlChannelTests
{
    [Test]
    public async Task ConcurrentDataAndSpaceSignalsShouldShareControlWrites()
    {
        var pipeName = $"sc{Guid.NewGuid():N}"[..20];
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await accept;
        await using var control = new SharedMemoryControlChannel(server);

        const int pairCount = 1_000;
        var combinedWrites = 0;
        var buffer = new byte[1];
        for (var index = 0; index < pairCount; index++)
        {
            control.SignalDataAvailable();
            control.SignalSpaceAvailable();

            byte observed = 0;
            while ((observed & 3) != 3)
            {
                await client.ReadExactlyAsync(buffer);
                if ((buffer[0] & 3) == 3)
                    combinedWrites++;
                observed |= buffer[0];
            }
        }

        await Assert.That(combinedWrites).IsGreaterThan(0);
    }

    [Test]
    public async Task DisposeShouldDrainTheFinalCloseSignalBeforeCompletingWakeSource()
    {
        var pipeName = $"sc{Guid.NewGuid():N}"[..20];
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await accept;
        await using var control = new SharedMemoryControlChannel(server);

        var dispose = control.DisposeAsync().AsTask();
        var signal = new byte[1];
        await client.ReadExactlyAsync(signal).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(signal[0]).IsEqualTo((byte)4);
    }

    [Test]
    // MeterListener registration is process-wide and observes shared-memory notification instruments.
    [NotInParallel]
    public async Task RepeatedOutboundSignalsShouldShareOnePendingWake()
    {
        var requests = 0L;
        var coalesced = 0L;
        var notifications = 0L;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name is
                "sharplink.shared_memory.notification.requests" or
                "sharplink.shared_memory.notification.coalesced" or
                "sharplink.shared_memory.notifications")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key != "sharplink.shared_memory.notification_kind" ||
                    !Equals(tag.Value, "data"))
                {
                    continue;
                }

                if (instrument.Name == "sharplink.shared_memory.notification.requests")
                    Interlocked.Add(ref requests, measurement);
                else if (instrument.Name == "sharplink.shared_memory.notification.coalesced")
                    Interlocked.Add(ref coalesced, measurement);
                else if (instrument.Name == "sharplink.shared_memory.notifications")
                    Interlocked.Add(ref notifications, measurement);
            }
        });
        meterListener.Start();

        var pipeName = $"sc{Guid.NewGuid():N}"[..20];
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await accept;
        await using var control = new SharedMemoryControlChannel(server);

        const int signalCount = 10_000;
        using var drainCancellation = new CancellationTokenSource();
        var drainTask = DrainControlSignalsAsync(client, drainCancellation.Token);
        try
        {
            for (var index = 0; index < signalCount; index++)
                control.SignalDataAvailable();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Volatile.Read(ref notifications) + Volatile.Read(ref coalesced) < signalCount)
                await Task.Delay(10, timeout.Token);

            await Assert.That(Volatile.Read(ref requests)).IsEqualTo(signalCount);
            await Assert.That(Volatile.Read(ref notifications) + Volatile.Read(ref coalesced))
                .IsEqualTo(signalCount);
            await Assert.That(Volatile.Read(ref coalesced)).IsGreaterThan(0);
            await Assert.That(Volatile.Read(ref notifications)).IsLessThan(signalCount);
        }
        finally
        {
            drainCancellation.Cancel();
            try
            {
                await drainTask;
            }
            catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested)
            {
            }
        }

        static async Task DrainControlSignalsAsync(PipeStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[256];
            while (await stream.ReadAsync(buffer, cancellationToken) != 0)
            {
            }
        }
    }

    [Test]
    public async Task WaiterArmedSignalsShouldReachHandlersBeforeAndAfterRegistration()
    {
        var pipeName = $"sc{Guid.NewGuid():N}"[..20];
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await accept;
        await using var control = new SharedMemoryControlChannel(server);

        var dataWaiters = 0;
        var spaceWaiters = 0;
        await client.WriteAsync(new byte[] { 8 | 16 });
        await client.FlushAsync();
        await Task.Delay(50);

        control.RegisterPeerWaiterHandlers(
            () => Interlocked.Increment(ref dataWaiters),
            () => Interlocked.Increment(ref spaceWaiters));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (Volatile.Read(ref dataWaiters) != 1 || Volatile.Read(ref spaceWaiters) != 1)
            await Task.Delay(10, timeout.Token);

        await client.WriteAsync(new byte[] { 8 | 16 });
        await client.FlushAsync();
        while (Volatile.Read(ref dataWaiters) != 2 || Volatile.Read(ref spaceWaiters) != 2)
            await Task.Delay(10, timeout.Token);
    }

    [Test]
    public async Task WaiterArmHintsShouldTolerateTransientCursorSnapshots()
    {
        var nonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);
        await using var mapping = SharedMemoryMapping.CreateServer(64 * 1024, nonce, out _);
        var direction = SharedMemoryLayout.GetDirection(mapping, clientToServer: true);
        direction.PublishReadPosition(1);
        direction.PublishWritePosition(0);

        var pipeName = $"sc{Guid.NewGuid():N}"[..20];
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await accept;
        await using var control = new SharedMemoryControlChannel(server);
        var reader = new SharedMemoryPipeReader(direction, control, spinCount: 0);
        var writer = new SharedMemoryPipeWriter(direction, control, spinCount: 0);

        writer.OnPeerReaderArmed();
        reader.OnPeerWriterArmed();

        await Assert.That(control.IsClosed).IsFalse();
        writer.Complete();
        reader.Complete();
    }

    [Test]
    public async Task UnknownControlSignalShouldSurfaceProtocolViolation()
    {
        var pipeName = $"sc{Guid.NewGuid():N}"[..20];
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await accept;
        await using var control = new SharedMemoryControlChannel(server);

        await client.WriteAsync(new byte[] { 0x7F });
        await client.FlushAsync();

        try
        {
            await control.WaitForDataAsync(default);
            throw new Exception("expected unknown shared-memory control signal rejection");
        }
        catch (SharpLinkException exception)
        {
            await Assert.That(exception.Code).IsEqualTo(SharpLinkErrorCode.ProtocolViolation);
        }
    }

    [Test]
    public async Task DisposeShouldJoinReaderAfterStreamCleanupFailure()
    {
        var stream = new ControlledDisposePipeStream();
        var control = new SharedMemoryControlChannel(stream);
        var dispose = control.DisposeAsync().AsTask();

        await stream.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var returnedBeforeReaderExited = dispose.IsCompleted;
        stream.ReleaseReader();
        var failure = await CaptureFailureAsync(dispose);

        await Assert.That(returnedBeforeReaderExited).IsFalse();
        await Assert.That(ContainsMessage(failure, "control stream cleanup failed")).IsTrue();
    }

    [Test]
    public async Task CancellationTokenShouldWakeAControlWaitWithoutAnExternalPulse()
    {
        var pipeName = $"sc{Guid.NewGuid():N}"[..20];
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await accept;
        await using var control = new SharedMemoryControlChannel(server);
        using var cancellation = new CancellationTokenSource();
        var wait = control.WaitForDataAsync(cancellation.Token).AsTask();

        cancellation.Cancel();
        await Task.Delay(50);
        var completedWithoutPulse = wait.IsCompleted;
        control.PulseDataWaiter();
        var failure = await CaptureFailureAsync(wait);

        await Assert.That(completedWithoutPulse).IsTrue();
        await Assert.That(failure).IsAssignableTo<OperationCanceledException>();
    }

    [Test]
    public async Task DisposeShouldJoinWriterAfterTheInitialCloseTimeout()
    {
        var stream = new ControlledWriterPipeStream();
        var control = new SharedMemoryControlChannel(stream);
        control.SignalDataAvailable();
        await stream.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var dispose = control.DisposeAsync().AsTask();
        await stream.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        var returnedBeforeWriterExited = dispose.IsCompleted;
        stream.ReleaseWriter();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(returnedBeforeWriterExited).IsFalse();
    }

    private static async Task<Exception> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation;
            throw new Exception("expected control cleanup failure");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static bool ContainsMessage(Exception exception, string message)
    {
        if (exception.Message == message)
            return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                if (ContainsMessage(inner, message))
                    return true;
            return false;
        }
        return exception.InnerException is { } nested && ContainsMessage(nested, message);
    }

    private sealed class ControlledDisposePipeStream()
        : PipeStream(PipeDirection.InOut, bufferSize: 1)
    {
        private readonly TaskCompletionSource<int> _readerRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => new(_readerRelease.Task);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public override ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            return ValueTask.FromException(
                new ApplicationException("control stream cleanup failed"));
        }

        internal void ReleaseReader() => _readerRelease.TrySetResult(0);
    }

    private sealed class ControlledWriterPipeStream()
        : PipeStream(PipeDirection.InOut, bufferSize: 1)
    {
        private readonly TaskCompletionSource<int> _readerRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writerRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => new(_readerRelease.Task);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            return new ValueTask(_writerRelease.Task);
        }

        public override ValueTask DisposeAsync()
        {
            DisposeStarted.TrySetResult();
            _readerRelease.TrySetResult(0);
            return ValueTask.CompletedTask;
        }

        internal void ReleaseWriter() => _writerRelease.TrySetResult();
    }
}
