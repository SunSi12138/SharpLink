using System.Diagnostics;
using System.Reflection;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class PreCreditStarvedMemoryEvidenceRunner
{
    private const int Streams = 128;
    private const int StarvedWindowBytes = 1;
    private static readonly int[] SupportedPayloadBytes = [64 * 1024, 1024 * 1024];

    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 1 ||
            !int.TryParse(args[0], out var payloadBytes) ||
            Array.IndexOf(SupportedPayloadBytes, payloadBytes) < 0)
        {
            throw new ArgumentException(
                "Starved-memory evidence requires one payload size: 65536 or 1048576.");
        }

        await WarmupAsync().ConfigureAwait(false);

        var codec = new UnsizedPayloadCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec(codec)
            .Build(includeGeneratedAssemblyCatalog: false);
        using var transport = new BenchmarkTransport($"pre-credit-starved-memory-{payloadBytes}");
        await using var session = new RpcSession(
            transport,
            new RpcSessionCreationOptions(RpcSessionRole.Client, context));
        var negotiated = new NegotiatedSessionOptions(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.FlowControl,
            context.Protocol.MaxFramePayloadBytes,
            StarvedWindowBytes,
            StarvedWindowBytes,
            null);
        if (!session.TryCompleteHandshake(negotiated))
            throw new InvalidOperationException("Starved-memory evidence handshake failed.");

        // Consume the only byte of protocol credit before measuring so every measured item takes
        // the unsized zero-WindowUpdate starvation path.
        await session.AcquireStreamSendCreditAsync(
            900_000,
            1,
            1,
            CancellationToken.None).ConfigureAwait(false);

        ForceFullCollection();
        var baseline = CaptureMemory();
        using var sampler = new WorkingSetSampler();
        sampler.Start();

        var sends = new Task[Streams];
        for (var index = 0; index < sends.Length; index++)
        {
            sends[index] = session.SendStreamChunkAsync(
                index + 1,
                1,
                new UnsizedPayload(payloadBytes),
                CancellationToken.None).AsTask();
        }

        await WaitForSerializationAsync(codec, sends).ConfigureAwait(false);
        var postLaunch = CaptureMemory();

        // Observe the natural retained state while the protocol remains starved. Do not force a
        // Gen2 collection here: ArrayPool.Shared may trim on Gen2, which would understate the
        // backing memory retained after excess producers have returned their writers.
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        var stable = CaptureMemory();
        var sampledPeakWorkingSetBytes = sampler.Stop();

        var pendingCount = 0;
        var rejectedCount = 0;
        for (var index = 0; index < sends.Length; index++)
        {
            if (!sends[index].IsCompleted)
            {
                pendingCount++;
                continue;
            }

            if (sends[index].Exception?.GetBaseException() is SharpLinkException
                {
                    Code: SharpLinkErrorCode.ResourceExhausted
                })
            {
                rejectedCount++;
            }
        }

        var reservedBytes = ReadInternalNumber(session, "PreCreditSerializedBytes");
        var waiterCount = ReadInternalNumber(session, "PreCreditSerializedWaiterCount");
        Console.WriteLine(
            $"[PreCreditStarvedMemory] payloadBytes={payloadBytes} streams={Streams} " +
            $"serializeCount={codec.SerializeCount} pendingCount={pendingCount} " +
            $"rejectedCount={rejectedCount} reservedBytes={reservedBytes} waiterCount={waiterCount} " +
            $"baselineWorkingSetBytes={baseline.WorkingSetBytes} " +
            $"postLaunchWorkingSetBytes={postLaunch.WorkingSetBytes} " +
            $"stableWorkingSetBytes={stable.WorkingSetBytes} " +
            $"sampledPeakWorkingSetBytes={sampledPeakWorkingSetBytes} " +
            $"sampledPeakDeltaBytes={Math.Max(0, sampledPeakWorkingSetBytes - baseline.WorkingSetBytes)} " +
            $"stableWorkingSetDeltaBytes={stable.WorkingSetBytes - baseline.WorkingSetBytes} " +
            $"baselinePrivateBytes={baseline.PrivateBytes} " +
            $"stablePrivateBytes={stable.PrivateBytes} " +
            $"stablePrivateDeltaBytes={stable.PrivateBytes - baseline.PrivateBytes} " +
            $"baselineGcHeapBytes={baseline.GcHeapBytes} " +
            $"stableGcHeapBytes={stable.GcHeapBytes} " +
            $"baselineGcCommittedBytes={baseline.GcCommittedBytes} " +
            $"stableGcCommittedBytes={stable.GcCommittedBytes}");

        var terminal = new SharpLinkException(
            SharpLinkErrorCode.ConnectionClosed,
            "starved-memory evidence cleanup");
        session.NotifyDisconnected(terminal);
        for (var index = 0; index < sends.Length; index++)
        {
            try
            {
                await sends[index].ConfigureAwait(false);
            }
            catch (SharpLinkException exception) when (
                ReferenceEquals(exception, terminal) ||
                exception.Code == SharpLinkErrorCode.ResourceExhausted)
            {
            }
        }

        ForceFullCollection();
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        var settled = CaptureMemory();
        Console.WriteLine(
            $"[PreCreditStarvedMemorySettled] payloadBytes={payloadBytes} streams={Streams} " +
            $"workingSetBytes={settled.WorkingSetBytes} " +
            $"workingSetDeltaBytes={settled.WorkingSetBytes - baseline.WorkingSetBytes} " +
            $"privateBytes={settled.PrivateBytes} " +
            $"privateDeltaBytes={settled.PrivateBytes - baseline.PrivateBytes} " +
            $"gcHeapBytes={settled.GcHeapBytes} gcCommittedBytes={settled.GcCommittedBytes}");
    }

    private static async Task WarmupAsync()
    {
        var codec = new UnsizedPayloadCodec();
        using var context = new SharpLinkRuntimeContextBuilder()
            .AddCodec(codec)
            .Build(includeGeneratedAssemblyCatalog: false);
        using var transport = new BenchmarkTransport("pre-credit-starved-memory-warmup");
        await using var session = new RpcSession(
            transport,
            new RpcSessionCreationOptions(RpcSessionRole.Client, context));
        var negotiated = new NegotiatedSessionOptions(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.FlowControl,
            context.Protocol.MaxFramePayloadBytes,
            16 * 1024 * 1024,
            16 * 1024 * 1024,
            null);
        if (!session.TryCompleteHandshake(negotiated))
            throw new InvalidOperationException("Starved-memory warmup handshake failed.");

        for (var index = 0; index < 64; index++)
        {
            await session.SendStreamChunkAsync(
                1,
                1,
                new UnsizedPayload(1024),
                CancellationToken.None).ConfigureAwait(false);
            session.ApplyWindowUpdate(1, new ProtocolV2WindowUpdate(1, 1024));
        }
        await session.FlushSendQueueAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task WaitForSerializationAsync(UnsizedPayloadCodec codec, Task[] sends)
    {
        for (var round = 0; round < 20_000; round++)
        {
            if (codec.SerializeCount == Streams)
                return;
            if (Array.TrueForAll(sends, static task => task.IsCompleted))
                return;
            await Task.Yield();
        }
        throw new InvalidOperationException(
            $"Expected {Streams} serializers, observed {codec.SerializeCount}.");
    }

    private static MemorySnapshot CaptureMemory()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        return new MemorySnapshot(
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(forceFullCollection: false),
            gc.TotalCommittedBytes);
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static string ReadInternalNumber(RpcSession session, string propertyName)
    {
        var property = typeof(RpcSession).GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        return property?.GetValue(session)?.ToString() ?? "n/a";
    }

    private readonly record struct MemorySnapshot(
        long WorkingSetBytes,
        long PrivateBytes,
        long GcHeapBytes,
        long GcCommittedBytes);

    private sealed class WorkingSetSampler : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _started = new(false);
        private int _stop;
        private long _maxWorkingSetBytes;

        internal WorkingSetSampler()
        {
            _thread = new Thread(SampleLoop)
            {
                IsBackground = true,
                Name = "SharpLink pre-credit working-set sampler"
            };
        }

        internal void Start()
        {
            _thread.Start();
            _started.Wait();
        }

        internal long Stop()
        {
            Interlocked.Exchange(ref _stop, 1);
            _thread.Join();
            return Volatile.Read(ref _maxWorkingSetBytes);
        }

        public void Dispose()
        {
            if (_thread.IsAlive)
            {
                Interlocked.Exchange(ref _stop, 1);
                _thread.Join();
            }
            _started.Dispose();
        }

        private void SampleLoop()
        {
            using var process = Process.GetCurrentProcess();
            _started.Set();
            while (Volatile.Read(ref _stop) == 0)
            {
                process.Refresh();
                RecordMax(process.WorkingSet64);
                Thread.Sleep(1);
            }
            process.Refresh();
            RecordMax(process.WorkingSet64);
        }

        private void RecordMax(long value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxWorkingSetBytes);
                if (value <= current)
                    return;
                if (Interlocked.CompareExchange(ref _maxWorkingSetBytes, value, current) == current)
                    return;
            }
        }
    }
}
