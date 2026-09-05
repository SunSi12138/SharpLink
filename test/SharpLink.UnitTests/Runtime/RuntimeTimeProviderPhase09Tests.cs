using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;

namespace SharpLink.UnitTests.Runtime;

public sealed class RuntimeTimeProviderPhase09Tests
{
    private static readonly DateTimeOffset UtcStart =
        new(2026, 8, 10, 8, 30, 0, TimeSpan.Zero);

    [Test]
    public async Task SessionActivityShouldUseMonotonicTimeoutAndProviderUtcActivity()
    {
        var provider = new ManualTimeProvider(UtcStart);
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        await using var session = new RpcSession(
            new TestTransportConnection(),
            RpcSessionTestFixture.ClientOptions(context));

        provider.Advance(TimeSpan.FromSeconds(3));
        Ensure(session.TimeSinceLastActivity == TimeSpan.FromSeconds(3),
            "session timeout elapsed must come from its monotonic provider");
        Ensure(session.LastActive == UtcStart.UtcDateTime,
            "an inactive session must retain its creation-time UTC activity snapshot");

        provider.SetUtcNow(UtcStart.AddDays(7));
        Ensure(session.TimeSinceLastActivity == TimeSpan.FromSeconds(3),
            "a forward UTC jump must not change monotonic timeout elapsed");
        Ensure(session.LastActive == UtcStart.UtcDateTime,
            "a UTC jump alone must not rewrite the last recorded activity snapshot");
        provider.SetUtcNow(UtcStart.AddDays(-7));
        Ensure(session.TimeSinceLastActivity == TimeSpan.FromSeconds(3),
            "a backward UTC jump must not change monotonic timeout elapsed");
        Ensure(session.LastActive == UtcStart.UtcDateTime,
            "a backward UTC jump alone must not move the last recorded activity snapshot");

        var externalActivity = UtcStart.AddHours(-4).UtcDateTime;
        session.LastActive = externalActivity;
        provider.Advance(TimeSpan.FromSeconds(2));
        Ensure(session.LastActive == externalActivity,
            "an external LastActive override must remain visible until the next real activity");
        Ensure(session.TimeSinceLastActivity == TimeSpan.FromSeconds(5),
            "the diagnostic override must not mutate the monotonic timeout timestamp");

        session.MarkActive();
        Ensure(session.TimeSinceLastActivity == TimeSpan.Zero,
            "real activity must reset monotonic elapsed at the provider timestamp");
        Ensure(session.LastActive == UtcStart.AddDays(-7).AddSeconds(2).UtcDateTime,
            "real activity must clear the override and snapshot the owning provider's current UTC value");
    }

    [Test]
    public async Task SessionsWithDifferentProvidersShouldAdvanceIndependently()
    {
        var firstProvider = new ManualTimeProvider(UtcStart);
        var secondStart = UtcStart.AddHours(1);
        var secondProvider = new ManualTimeProvider(secondStart);
        using var firstContext = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(firstProvider)
            .Build(includeGeneratedAssemblyCatalog: false);
        using var secondContext = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(secondProvider)
            .Build(includeGeneratedAssemblyCatalog: false);
        await using var first = new RpcSession(
            new TestTransportConnection(),
            RpcSessionTestFixture.ClientOptions(firstContext));
        await using var second = new RpcSession(
            new TestTransportConnection(),
            RpcSessionTestFixture.ClientOptions(secondContext));

        firstProvider.Advance(TimeSpan.FromSeconds(4));

        Ensure(first.TimeSinceLastActivity == TimeSpan.FromSeconds(4),
            "the advanced RuntimeContext must observe its own elapsed time");
        Ensure(second.TimeSinceLastActivity == TimeSpan.Zero,
            "advancing one RuntimeContext must not move another session clock");
        Ensure(first.LastActive == UtcStart.UtcDateTime &&
               second.LastActive == secondStart.UtcDateTime,
            "each session must retain its own provider UTC activity snapshot");

        first.MarkActive();
        secondProvider.Advance(TimeSpan.FromSeconds(6));

        Ensure(first.TimeSinceLastActivity == TimeSpan.Zero,
            "the first session must remain at its independently recorded activity timestamp");
        Ensure(first.LastActive == UtcStart.AddSeconds(4).UtcDateTime,
            "the first session activity must snapshot only its owning provider UTC value");
        Ensure(second.TimeSinceLastActivity == TimeSpan.FromSeconds(6),
            "the second provider must advance only its own session");
    }

    [Test]
    public async Task LastActiveOverrideShouldNormalizeToUtcAndYieldToTheNextProviderActivity()
    {
        var provider = new ManualTimeProvider(UtcStart);
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        await using var session = new RpcSession(
            new TestTransportConnection(),
            RpcSessionTestFixture.ClientOptions(context));
        var utcReadsAfterConstruction = provider.UtcNowReadCount;
        var local = new DateTime(2026, 8, 10, 16, 45, 12, DateTimeKind.Local);

        session.LastActive = local;
        Ensure(session.LastActive == local.ToUniversalTime() &&
               session.LastActive.Kind == DateTimeKind.Utc,
            "a Local diagnostic override must be exposed as its equivalent UTC value");
        var unspecified = new DateTime(2026, 8, 10, 9, 15, 30, DateTimeKind.Unspecified);
        session.LastActive = unspecified;
        Ensure(session.LastActive == DateTime.SpecifyKind(unspecified, DateTimeKind.Utc) &&
               session.LastActive.Kind == DateTimeKind.Utc,
            "an Unspecified diagnostic override must preserve its ticks while exposing the UTC contract");

        provider.Advance(TimeSpan.FromSeconds(7));
        session.MarkActive();

        Ensure(provider.UtcNowReadCount == utcReadsAfterConstruction + 1,
            "each real MarkActive must read UTC once from the owning RuntimeContext provider");
        Ensure(session.LastActive == UtcStart.AddSeconds(7).UtcDateTime &&
               session.LastActive.Kind == DateTimeKind.Utc,
            "the next real activity must clear the external override and publish current provider UTC");
    }

    [Test]
    public async Task ConcurrentLastActiveReadersShouldObserveOnlyNondecreasingUtcActivity()
    {
        var provider = new ManualTimeProvider(UtcStart);
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        await using var session = new RpcSession(
            new TestTransportConnection(),
            RpcSessionTestFixture.ClientOptions(context));
        var utcReadsAfterConstruction = provider.UtcNowReadCount;
        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = Enumerable.Range(0, 4).Select(async _ =>
        {
            await start.Task;
            var previous = DateTime.MinValue;
            for (var read = 0; read < 512; read++)
            {
                var current = session.LastActive;
                Ensure(current.Kind == DateTimeKind.Utc,
                    "concurrent LastActive reads must preserve the public UTC contract");
                Ensure(current >= previous,
                    "concurrent LastActive reads must never observe activity moving backward");
                previous = current;
                await Task.Yield();
            }
        }).ToArray();
        var writer = Task.Run(async () =>
        {
            await start.Task;
            for (var write = 0; write < 256; write++)
            {
                provider.Advance(TimeSpan.FromTicks(1));
                session.MarkActive();
                await Task.Yield();
            }
        });

        start.TrySetResult();
        await Task.WhenAll(readers.Append(writer));

        Ensure(session.LastActive == UtcStart.AddTicks(256).UtcDateTime,
            "the final activity projection must match every monotonic advance without loss or regression");
        Ensure(provider.UtcNowReadCount == utcReadsAfterConstruction + 256,
            "every concurrent MarkActive writer iteration must obtain its UTC value from the provider");
    }

    [Test]
    public async Task PingPayloadShouldUseTheSessionProviderTimestampWithoutChangingProtocolShape()
    {
        var provider = new ManualTimeProvider(UtcStart);
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(provider)
            .Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "phase09-provider-ping",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context));
        provider.Advance(TimeSpan.FromMilliseconds(1_234));
        var expectedTimestamp = provider.GetTimestamp();

        await session.SendPingWithBackpressureAsync();
        await session.FlushSendQueueAsync();
        var read = await output.Reader.ReadAsync();
        var remaining = read.Buffer;

        Ensure(ProtocolV2FrameParser.TryReadFrame(
                ref remaining,
                context.Protocol,
                out var header,
                out var payload),
            "provider-backed Ping frame must be emitted");
        Ensure(header is
        {
            Type: ProtocolV2FrameType.Ping,
            Flags: ProtocolV2FrameFlags.None,
            RequestId: 0
        }, "provider migration must preserve the Protocol v2 Ping header");
        Ensure(payload.Length == sizeof(long) &&
               BinaryPrimitives.ReadInt64LittleEndian(payload.ToArray()) == expectedTimestamp,
            "Ping payload must contain the exact owning provider timestamp");
        Ensure(remaining.IsEmpty,
            "a single Ping must not emit an additional compatibility frame");

        output.Reader.AdvanceTo(read.Buffer.End);
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task SharpLinkTimerTimeoutShouldHonorBeforeEqualityAfterAndReleaseCleanup()
    {
        var timeoutProvider = new ManualTimeProvider(UtcStart);
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = SharpLinkTimer.WaitAsync(
            neverCompletes.Task,
            TimeSpan.FromSeconds(5),
            timeoutProvider).AsTask();

        Ensure(timeoutProvider.ActiveTimerCount == 1,
            "a provider-aware timeout must own one timer while pending");
        timeoutProvider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        await Task.Yield();
        Ensure(!timeout.IsCompleted,
            "the timeout must remain pending one provider tick before its boundary");

        timeoutProvider.Advance(TimeSpan.FromTicks(1));
        Ensure(!await timeout,
            "an incomplete owner must time out at exact provider equality");
        Ensure(timeoutProvider.ActiveTimerCount == 0,
            "the equality winner must dispose its provider timer");

        timeoutProvider.Advance(TimeSpan.FromDays(1));
        Ensure(timeout.IsCompletedSuccessfully && !timeout.Result,
            "advancing after the terminal boundary must not change the timeout result");

        var releaseProvider = new ManualTimeProvider(UtcStart);
        var releasedOwner = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var released = SharpLinkTimer.WaitAsync(
            releasedOwner.Task,
            TimeSpan.FromSeconds(5),
            releaseProvider).AsTask();
        releaseProvider.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        releasedOwner.TrySetResult();

        Ensure(await released,
            "owner completion immediately before the boundary must beat the timeout");
        await releaseProvider.WaitForTimersDrainedAsync();
        Ensure(releaseProvider.ActiveTimerCount == 0,
            "owner completion must disarm the losing provider timer");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
