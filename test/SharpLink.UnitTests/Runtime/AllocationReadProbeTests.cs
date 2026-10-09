using System.Diagnostics;
using System.IO.Pipelines;
using SharpLink.Benchmarks;

namespace SharpLink.UnitTests.Runtime;

public class AllocationReadProbeTests
{
    [Test]
    public async Task ObservationShouldTrackRealDataAndPendingReadCancellationAcrossFreshArms()
    {
        await using var pair = await ObservedPair.CreateAsync();
        Ensure(!pair.Probe.HasPendingRead, "no read has started");

        for (var index = 0; index < 4; index++)
        {
            var pending = pair.Server.Input.ReadAsync().AsTask();
            var advanced = false;
            try
            {
                await WaitForPendingReadAsync(pair.Probe);
                pair.Client.Output.GetSpan(1)[0] = (byte)(42 + index);
                pair.Client.Output.Advance(1);
                var flush = await pair.Client.Output.FlushAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                Ensure(!flush.IsCanceled && !flush.IsCompleted, "real writer remains usable");
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
                try
                {
                    Ensure(!pair.Probe.HasPendingRead, "completed read cannot advertise its old arm");
                    Ensure(result.Buffer.Length == 1 && result.Buffer.FirstSpan[0] == 42 + index,
                        "real shared-memory payload reaches the observed reader");
                }
                finally
                {
                    pair.Server.Input.AdvanceTo(result.Buffer.End);
                    advanced = true;
                }
            }
            finally
            {
                await ReleasePendingReadAsync(pair.Server.Input, pending, advanced);
            }

            pending = pair.Server.Input.ReadAsync().AsTask();
            advanced = false;
            try
            {
                await WaitForPendingReadAsync(pair.Probe);
                pair.Server.Input.CancelPendingRead();
                Ensure(!pair.Probe.HasPendingRead, "cancel requested arm cannot allow another send");
                var canceled = await pending.WaitAsync(TimeSpan.FromSeconds(2));
                Ensure(canceled.IsCanceled && canceled.Buffer.IsEmpty, "real read observes cancellation");
                pair.Server.Input.AdvanceTo(canceled.Buffer.End);
                advanced = true;
                Ensure(!pair.Probe.HasPendingRead, "canceled arm cannot satisfy the next iteration");
            }
            finally
            {
                await ReleasePendingReadAsync(pair.Server.Input, pending, advanced);
            }
        }
    }

    [Test]
    public async Task TokenCancellationShouldReleaseTheArmAndAllowARealReadToRearm()
    {
        await using var pair = await ObservedPair.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = pair.Server.Input.ReadAsync(cancellation.Token).AsTask();
        try
        {
            await WaitForPendingReadAsync(pair.Probe);
            cancellation.Cancel();
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(2));
                throw new Exception("expected token cancellation");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            Ensure(!pair.Probe.HasPendingRead, "token canceled read no longer advertises pending");
        }
        finally
        {
            cancellation.Cancel();
            await ReleasePendingReadAsync(pair.Server.Input, pending, advanced: false);
        }

        pending = pair.Server.Input.ReadAsync().AsTask();
        try
        {
            await WaitForPendingReadAsync(pair.Probe);
            Ensure(!pending.IsCompleted, "fresh arm really remains pending after token cancellation");
        }
        finally
        {
            await ReleasePendingReadAsync(pair.Server.Input, pending, advanced: false);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReaderOrUnderlyingConnectionClosureShouldExcludeThePendingArm(bool closeConnection)
    {
        await using var pair = await ObservedPair.CreateAsync();
        var pending = pair.Server.Input.ReadAsync().AsTask();
        var advanced = false;
        try
        {
            await WaitForPendingReadAsync(pair.Probe);
            var completion = closeConnection
                ? pair.Server.DisposeAsync().AsTask()
                : pair.Server.Input.CompleteAsync().AsTask();
            Ensure(!pair.Probe.HasPendingRead, "close request excludes pending before disposal joins");
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(result.IsCompleted && result.Buffer.IsEmpty, "closed reader returns its terminal result");
            pair.Server.Input.AdvanceTo(result.Buffer.End);
            advanced = true;
            await completion.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!pair.Probe.HasPendingRead, "disposed reader cannot advertise a reusable arm");
        }
        finally
        {
            await ReleasePendingReadAsync(pair.Server.Input, pending, advanced);
        }
    }

    private static async Task WaitForPendingReadAsync(AllocationReadProbe probe)
    {
        var deadline = Stopwatch.GetTimestamp() + 2L * Stopwatch.Frequency;
        while (!probe.HasPendingRead)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("Real observed read did not enter its pending data wait.");
            await Task.Yield();
        }
    }

    private static async Task ReleasePendingReadAsync(PipeReader reader, Task<ReadResult> pending, bool advanced)
    {
        if (!pending.IsCompleted)
            reader.CancelPendingRead();
        try
        {
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            if (!advanced)
                reader.AdvanceTo(result.Buffer.End);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ObservedPair(
        IServerTransportListener listener,
        SharedMemoryClientTransportFactory factory,
        ITransportConnection client,
        ITransportConnection server,
        AllocationReadProbe probe) : IAsyncDisposable
    {
        internal ITransportConnection Client { get; } = client;
        internal ITransportConnection Server { get; } = server;
        internal AllocationReadProbe Probe { get; } = probe;

        internal static async Task<ObservedPair> CreateAsync()
        {
            var name = $"allocation-probe-{Guid.NewGuid():N}";
            var options = new SharedMemoryTransportOptions { SpinCount = 0, HandshakeTimeout = TimeSpan.FromSeconds(5) };
            var probe = new AllocationReadProbe();
            var listener = probe.Wrap(new SharedMemoryServerTransportListener(name, options));
            var factory = new SharedMemoryClientTransportFactory(name, options);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var accept = listener.AcceptAsync(cancellation.Token).AsTask();
            ITransportConnection? client = null;
            try
            {
                client = await factory.ConnectAsync(cancellation.Token);
                var server = await accept;
                return new ObservedPair(listener, factory, client, server, probe);
            }
            catch
            {
                cancellation.Cancel();
                try
                {
                    var server = await accept.WaitAsync(TimeSpan.FromSeconds(5));
                    await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                finally
                {
                    try
                    {
                        if (client is not null)
                            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    finally
                    {
                        await factory.DisposeAsync();
                        await listener.DisposeAsync();
                    }
                }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                try
                {
                    await Client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    await factory.DisposeAsync();
                    await listener.DisposeAsync();
                }
            }
        }
    }
}
