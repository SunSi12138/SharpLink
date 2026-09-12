using System.Diagnostics;
using System.IO.Pipelines;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public sealed class RpcSessionRuntimeFlushPolicyTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TimedBatchShouldIgnoreDelayedDataWakeAfterQueueWasDrained(bool publishRuntimePolicy)
    {
        var clock = new ManualTimeProvider();
        var provider = new TimerCountingTimeProvider(clock);
        var initial = new RpcSessionFlushOptions(1024, TimeSpan.FromSeconds(30));
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        var owner = CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy());
        var policy = owner.GetOrCreateSessionFlushPolicyState(initial, context.PerformanceProfile);
        if (publishRuntimePolicy)
            Ensure(policy.Publish(4096, TimeSpan.FromSeconds(30)), "runtime threshold publication");
        var input = new Pipe();
        var output = new Pipe();
        var session = CreateSession("delayed-data-wake", context, owner, initial, input, output);
        try
        {
            var readTask = output.Reader.ReadAsync().AsTask();
            session.SendPacket(CreateFrame(session, 64, 1));
            await WaitUntilAsync(() => provider.TimerCount > 0);
            var armedCount = provider.TimerCount;
            var pump = typeof(RpcSession).GetField("_pump", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(session)!;
            var wakeup = (WakeupSignal)pump.GetType()
                .GetField("_wakeup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pump)!;

            // Model an enqueuer delayed between queue publication and Signal: the
            // pump already consumed its frame through a previous wake.
            wakeup.Signal();
            await WaitUntilAsync(() => readTask.IsCompleted || provider.TimerCount > armedCount);
            Ensure(!readTask.IsCompleted && session.QueuedSendBytes > 0,
                "a delayed data wake must preserve the timed batch and retained frame");

            clock.Advance(TimeSpan.FromSeconds(30));
            var read = await readTask.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(read.Buffer.Length == ProtocolV2Constants.HeaderBytes + 64,
                "the original deadline must publish the complete retained frame");
            output.Reader.AdvanceTo(read.Buffer.End);
            await session.FlushSendQueueAsync();
            Ensure(session.QueuedSendBytes == 0, "the deadline flush must release queued byte ownership");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task ThresholdDecreaseShouldWakeArmedExistingSessionAndFutureSessionShouldShareGeneration()
    {
        var clock = new ManualTimeProvider();
        var initial = new RpcSessionFlushOptions(1024 * 1024, TimeSpan.FromSeconds(30));
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var owner = CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy());
        var policy = owner.GetOrCreateSessionFlushPolicyState(initial, context.PerformanceProfile);
        var firstInput = new Pipe();
        var firstOutput = new Pipe();
        var first = CreateSession("runtime-flush-existing", context, owner, initial, firstInput, firstOutput);
        try
        {
            var firstRead = firstOutput.Reader.ReadAsync().AsTask();
            var firstFrame = CreateFrame(first, 64, 1);
            first.SendPacket(firstFrame);
            await WaitUntilAsync(() => first.QueuedSendBytes > 0 && clock.ActiveTimerCount > 0);
            Ensure(!firstRead.IsCompleted, "the initial large threshold must keep the existing session batch armed");

            Ensure(policy.Publish(1, TimeSpan.FromSeconds(30)), "threshold decrease must publish one new generation");
            var published = policy.Capture();
            Ensure(published.Generation == 1 && published.FlushSizeThreshold == 1,
                "threshold decrease generation snapshot");
            var read = await firstRead.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!read.Buffer.IsEmpty, "threshold decrease must wake and flush the already armed existing session");
            firstOutput.Reader.AdvanceTo(read.Buffer.End);

            var secondInput = new Pipe();
            var secondOutput = new Pipe();
            var second = CreateSession("runtime-flush-future", context, owner, initial, secondInput, secondOutput);
            try
            {
                var secondRead = secondOutput.Reader.ReadAsync().AsTask();
                var secondFrame = CreateFrame(second, 64, 2);
                second.SendPacket(secondFrame);
                var futureRead = await secondRead.WaitAsync(TimeSpan.FromSeconds(2));
                Ensure(!futureRead.Buffer.IsEmpty,
                    "a session created after publication must use the same threshold generation");
                secondOutput.Reader.AdvanceTo(futureRead.Buffer.End);
            }
            finally
            {
                await second.DisposeAsync();
                await secondOutput.Reader.CompleteAsync();
                await secondInput.Writer.CompleteAsync();
            }
        }
        finally
        {
            await first.DisposeAsync();
            await firstOutput.Reader.CompleteAsync();
            await firstInput.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task ThresholdIncreaseShouldNotFlushAtOldBoundary()
    {
        var clock = new ManualTimeProvider();
        var initial = new RpcSessionFlushOptions(256, TimeSpan.FromSeconds(30));
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var owner = CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy());
        var policy = owner.GetOrCreateSessionFlushPolicyState(initial, context.PerformanceProfile);
        var input = new Pipe();
        var output = new Pipe();
        var session = CreateSession("runtime-flush-threshold-increase", context, owner, initial, input, output);
        try
        {
            var readTask = output.Reader.ReadAsync().AsTask();
            session.SendPacket(CreateFrame(session, 64, 1));
            await WaitUntilAsync(() => session.QueuedSendBytes > 0 && clock.ActiveTimerCount > 0);

            Ensure(policy.Publish(4096, TimeSpan.FromSeconds(30)), "threshold increase must publish");
            session.SendPacket(CreateFrame(session, 256, 2));
            await WaitUntilAsync(() => session.QueuedSendBytes > 256);
            await Task.Delay(50);
            Ensure(!readTask.IsCompleted,
                "bytes crossing the old threshold after an increase must remain batched under the new threshold");

            Ensure(policy.Publish(1, TimeSpan.FromSeconds(30)), "cleanup threshold decrease must publish");
            var read = await readTask.WaitAsync(TimeSpan.FromSeconds(2));
            output.Reader.AdvanceTo(read.Buffer.End);
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task LatencyDecreaseShouldRecomputeFromOriginalBatchStart()
    {
        var clock = new ManualTimeProvider();
        var initial = new RpcSessionFlushOptions(1024 * 1024, TimeSpan.FromSeconds(10));
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var owner = CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy());
        var policy = owner.GetOrCreateSessionFlushPolicyState(initial, context.PerformanceProfile);
        var input = new Pipe();
        var output = new Pipe();
        var session = CreateSession("runtime-flush-latency-decrease", context, owner, initial, input, output);
        try
        {
            var readTask = output.Reader.ReadAsync().AsTask();
            session.SendPacket(CreateFrame(session, 64, 1));
            await WaitUntilAsync(() => session.QueuedSendBytes > 0 && clock.ActiveTimerCount > 0);
            clock.Advance(TimeSpan.FromSeconds(3));
            Ensure(!readTask.IsCompleted, "the original ten-second boundary must still be armed at three seconds");

            Ensure(policy.Publish(1024 * 1024, TimeSpan.FromSeconds(2)), "latency decrease must publish");
            var read = await readTask.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!read.Buffer.IsEmpty,
                "latency decrease below elapsed batch age must flush without resetting the batch start");
            output.Reader.AdvanceTo(read.Buffer.End);
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task LatencyIncreaseShouldIgnoreOldTimerBoundary()
    {
        var clock = new ManualTimeProvider();
        var initial = new RpcSessionFlushOptions(1024 * 1024, TimeSpan.FromSeconds(2));
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var owner = CompressionSendPolicyState.CreateInitial(new SharpLinkCompressionSendPolicy());
        var policy = owner.GetOrCreateSessionFlushPolicyState(initial, context.PerformanceProfile);
        var input = new Pipe();
        var output = new Pipe();
        var session = CreateSession("runtime-flush-latency-increase", context, owner, initial, input, output);
        try
        {
            var readTask = output.Reader.ReadAsync().AsTask();
            session.SendPacket(CreateFrame(session, 64, 1));
            await WaitUntilAsync(() => session.QueuedSendBytes > 0 && clock.ActiveTimerCount > 0);
            clock.Advance(TimeSpan.FromSeconds(1));

            Ensure(policy.Publish(1024 * 1024, TimeSpan.FromSeconds(10)), "latency increase must publish");
            await Task.Yield();
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
            Ensure(!readTask.IsCompleted,
                "completion of the old two-second timer must not flush a batch after latency increases");

            clock.Advance(TimeSpan.FromSeconds(8));
            var read = await readTask.WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(!read.Buffer.IsEmpty, "the batch must flush at the recomputed ten-second boundary");
            output.Reader.AdvanceTo(read.Buffer.End);
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    private sealed class TimerCountingTimeProvider(ManualTimeProvider clock) : TimeProvider
    {
        private int _timerCount;
        internal int TimerCount => Volatile.Read(ref _timerCount);
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override long GetTimestamp() => clock.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = clock.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timerCount);
            return timer;
        }
    }

    private static RpcSession CreateSession(
        string id,
        SharpLinkRuntimeContext context,
        CompressionSendPolicyState owner,
        RpcSessionFlushOptions initial,
        Pipe input,
        Pipe output)
        => RpcSessionTestFixture.CreateSessionOverTestTransport(
            id,
            input.Reader,
            output.Writer,
            new RpcSessionCreationOptions(
                RpcSessionRole.Client,
                context,
                initial,
                owner));

    private static IRpcByteBufferWriter CreateFrame(RpcSession session, int payloadBytes, ulong requestId)
    {
        var writer = session.RuntimeContext.Buffers.Rent();
        using (writer.BeginPacketScope(
                   ProtocolV2FrameType.Response,
                   ProtocolV2FrameFlags.None,
                   requestId))
        {
            writer.Write(new byte[payloadBytes]);
        }
        return writer;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("condition was not reached");
            await Task.Delay(5);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
