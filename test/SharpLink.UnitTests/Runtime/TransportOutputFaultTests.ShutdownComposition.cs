namespace SharpLink.UnitTests.Runtime;

public partial class TransportOutputFaultTests
{
    [Test]
    public async Task StoppingWinnerMustNotReplayAfterHeldFlushFails()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var stream = new ObservingStream(blockFlush: true, failure: "flush-after-gate");
        var session = CreateSession(context, new StreamTransportConnection(stream));
        var frame = CreateFrame(session, 64, 1);
        Exception? terminal = null;
        var notifications = 0;
        session.OnDisconnected += exception => { terminal ??= exception; notifications++; };
        var sent = session.SendPacketAndFlushAsync(frame).AsTask();
        try
        {
            await stream.FlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.BeginShutdown();
            var stopping = terminal;
            stream.ReleaseFlush.TrySetResult();
            _ = await CaptureAsync(() => new ValueTask(sent));
            await session.DisposeAsync();
            Ensure(stopping is not null && ReferenceEquals(stopping, terminal) && notifications == 1,
                "the stopping terminal remains the selected public outcome");
            Ensure(stream.WriteCount == 1 && stream.BytesWritten == 79 && stream.FlushCount == 1,
                "a failed flush has unknown delivery and must not replay already-written bytes");
            EnsureReturned(frame);
        }
        finally { stream.ReleaseFlush.TrySetResult(); await session.DisposeAsync(); }
    }

    [Test]
    public async Task CanceledHeldWriteMustNotReplayItsPartialPrefix()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var stream = new ObservingStream(blockWrite: true, failure: "cancel-prefix-after-gate");
        var session = CreateSession(context, new StreamTransportConnection(stream));
        var frame = CreateFrame(session, 64, 1);
        var sent = session.SendPacketAndFlushAsync(frame).AsTask();
        try
        {
            await stream.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var disposal = session.DisposeAsync().AsTask();
            await stream.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!disposal.IsCompleted && frame.WrittenCount == 79,
                "logical cancellation and transport abort cannot complete physical writer ownership");
            stream.ReleaseWrite.TrySetResult();
            _ = await CaptureAsync(() => new ValueTask(sent));
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(stream.WriteCount == 1 && stream.BytesWritten == 7 && stream.DisposeCount == 1,
                "a canceled partial write is not a safe normal-completion flush");
            EnsureReturned(frame);
        }
        finally { stream.ReleaseWrite.TrySetResult(); await session.DisposeAsync(); }
    }

    [Test]
    public async Task NormalBufferedFlushMustPublishSingleFlightOwnerBeforeUserReentry()
    {
        var stream = new ObservingStream();
        var transport = new StreamTransportConnection(stream);
        Task? writeReentry = null, disposeReentry = null;
        stream.OnWrite = () => writeReentry = transport.DisposeAsync().AsTask();
        stream.OnDispose = () => disposeReentry = transport.DisposeAsync().AsTask();
        transport.Output.Write(new byte[64]);
        var disposal = transport.DisposeAsync().AsTask();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(ReferenceEquals(disposal, writeReentry) && ReferenceEquals(disposal, disposeReentry),
            "normal flush and stream disposal callbacks must observe the published same owner");
        Ensure(stream.WriteCount == 1 && stream.BytesWritten == 64 && stream.DisposeCount == 1,
            "normal buffered output still flushes once without recursive disposal");
    }
}
