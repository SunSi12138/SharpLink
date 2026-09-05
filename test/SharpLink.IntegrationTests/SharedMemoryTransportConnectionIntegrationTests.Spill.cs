using System.Diagnostics.Metrics;
using System.IO.Pipelines;

namespace SharpLink.IntegrationTests;

public partial class SharedMemoryTransportConnectionIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task SharedMemoryAccumulatedSpillShouldNotRecopyPendingBytes()
    {
        const int capacity = 64 * 1024;
        int[] chunkSizes = [17, 1024, 70_000];
        var spillCopyBytes = 0L;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                instrument.Name == "sharplink.shared_memory.spill.copy.bytes")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
            Interlocked.Add(ref spillCopyBytes, measurement));
        meterListener.Start();

        var (listener, factory, client, server) = await CreateRawPairAsync();
        await using var listenerScope = listener;
        await using var factoryScope = factory;
        await using var clientScope = client;
        await using var serverScope = server;

        var fullRing = client.Output.GetMemory(capacity);
        fullRing.Span[..capacity].Fill(0x41);
        client.Output.Advance(capacity);
        _ = await client.Output.FlushAsync();

        var written = 0;
        foreach (var chunkSize in chunkSizes)
        {
            var chunk = client.Output.GetMemory(chunkSize);
            for (var index = 0; index < chunkSize; index++)
                chunk.Span[index] = unchecked((byte)((written + index) * 31));
            client.Output.Advance(chunkSize);
            written += chunkSize;
        }
        Ensure(Volatile.Read(ref spillCopyBytes) == 0,
            "shared-memory segmented spill does not recopy pending bytes");

        using var cancellation = new CancellationTokenSource();
        var canceledFlush = client.Output.FlushAsync(cancellation.Token).AsTask();
        var initialRead = await server.Input.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var initialLength = initialRead.Buffer.Length;
        server.Input.AdvanceTo(initialRead.Buffer.End);
        Ensure(initialLength == capacity, "shared-memory accumulated spill initial ring");

        var firstSpillRead = await server.Input.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var received = ValidateAndConsumePattern(server.Input, firstSpillRead, 0);
        try
        {
            _ = await canceledFlush.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception("expected segmented spill flush cancellation");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        var resumedFlush = client.Output.FlushAsync().AsTask();
        while (received < written)
        {
            var read = await server.Input.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            received += ValidateAndConsumePattern(server.Input, read, received);
        }
        Ensure(received == written, "shared-memory accumulated spill byte count");
        _ = await resumedFlush.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static int ValidateAndConsumePattern(PipeReader reader, ReadResult read, int offset)
    {
        var length = checked((int)read.Buffer.Length);
        try
        {
            ValidatePattern(read.Buffer, offset);
        }
        finally
        {
            reader.AdvanceTo(read.Buffer.End);
        }
        return length;
    }
}
