using System.Diagnostics.Metrics;
using System.IO.Pipes;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
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
        for (var index = 0; index < signalCount; index++)
            control.SignalDataAvailable();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (Volatile.Read(ref notifications) + Volatile.Read(ref coalesced) != signalCount)
            await Task.Delay(10, timeout.Token);

        await Assert.That(Volatile.Read(ref requests)).IsEqualTo(signalCount);
        await Assert.That(Volatile.Read(ref coalesced)).IsGreaterThan(0);
        await Assert.That(Volatile.Read(ref notifications)).IsLessThan(signalCount);
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
}
