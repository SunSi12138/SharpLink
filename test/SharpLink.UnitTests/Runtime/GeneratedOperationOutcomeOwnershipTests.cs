using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public sealed class GeneratedOperationOutcomeOwnershipTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task CompletedFaultedMoveNextRemainsOnlyInvocationOwned(bool deadline)
        => CheckOrdinarySourceFailureAsync(deadline, sendFailure: false, initiallyComplete: true);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task CompletedFaultedSizedSendRemainsOnlyInvocationOwned(bool deadline)
        => CheckOrdinarySourceFailureAsync(deadline, sendFailure: true, initiallyComplete: true);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task FaultedMoveNextAfterWaitStartedTransfersOnceToInvocation(bool deadline)
        => CheckOrdinarySourceFailureAsync(deadline, sendFailure: false, initiallyComplete: false);

    [Test]
    public Task AlreadyFaultedSourcePrecedesAlreadyCanceledCallerWithoutDeadline()
        => CheckOrdinarySourceFailureAsync(deadline: false, sendFailure: false, initiallyComplete: true, alreadyCanceled: true);

    private static async Task CheckOrdinarySourceFailureAsync(bool deadline, bool sendFailure, bool initiallyComplete, bool alreadyCanceled = false)
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "source-outcome-owner", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        using var cancellation = new CancellationTokenSource(); // CanBeCanceled forces actual guarded branch.
        if (alreadyCanceled) cancellation.Cancel();
        var raw = new InvalidOperationException(sendFailure ? "ordinary sized send" : "ordinary MoveNext");
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (initiallyComplete && !sendFailure) source.SetException(raw);
        var stream = new SourceStream(sendFailure ? null : source.Task);
        var owner = new RecordingOwner();
        var provider = new ManualTimeProvider();
        Task pump;
        using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(session.Id, authentication: null,
                   deadline ? RpcDeadline.Create(TimeSpan.FromSeconds(5), provider) : default,
                   provider)))
        {
            pump = session.PumpGeneratedOutboundStreamAsync(201, 0, stream, new FaultingSizedCodec(raw),
                payloadNullable: false, cancellation.Token, owner).AsTask();
        }
        if (!initiallyComplete)
        {
            Ensure(!pump.IsCompleted, "actual source is held after wait registration");
            source.SetException(raw);
        }
        var failure = await Failure(pump);
        Ensure(ReferenceEquals(failure, raw) && owner.Failures.Count == 0 && owner.Retains == 0 &&
            owner.Releases == 0 && owner.Supervisor.CaptureSnapshot().TotalTracked == 0 && stream.DisposeCount == 1,
            "ordinary source failure is delivered once, never copied into strict physical/Stop outcome");
        Ensure(provider.ActiveTimerCount == 0, "all wait-owned timer cleanup precedes source outcome transfer");
        owner.Supervisor.Seal();
        await owner.Supervisor.DrainAsync();
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task SourceAlreadyTerminalWinsCallerCancellationButExpiredAbsoluteDeadlineWinsBoth()
    {
        var raw = new InvalidOperationException("already faulted source");
        var source = Task.FromException(raw);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var provider = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), provider);
        Ensure(await SharpLinkTimer.WaitForCompletionAsync(source, deadline, provider, canceled.Token),
            "source-ready has priority over caller cancellation before original deadline");
        provider.Advance(TimeSpan.FromSeconds(1));
        Ensure(!await SharpLinkTimer.WaitForCompletionAsync(source, deadline, provider, canceled.Token) &&
            provider.ActiveTimerCount == 0, "expired original absolute deadline wins even already-faulted source, without timer allocation");
    }

    [Test]
    public async Task SimultaneousSourceFaultAndCancellationNeverInferOutcomeFromCaughtException()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "racing-source-owner", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var raw = new InvalidOperationException("racing source");
        using var cancellation = new CancellationTokenSource();
        var owner = new RecordingOwner();
        var pump = session.PumpGeneratedOutboundStreamAsync(203, 0, new SourceStream(source.Task),
            new FaultingSizedCodec(raw), payloadNullable: false, cancellation.Token, owner).AsTask();
        using var start = new Barrier(2);
        await Task.WhenAll(
            Task.Run(() => { Ensure(start.SignalAndWait(Timeout), "source contender ready"); source.SetException(raw); }),
            Task.Run(() => { Ensure(start.SignalAndWait(Timeout), "cancel contender ready"); cancellation.Cancel(); })).WaitAsync(Timeout);
        var logical = await Failure(pump);
        owner.Supervisor.Seal();
        await owner.Supervisor.DrainAsync().WaitAsync(Timeout);
        Ensure(ReferenceEquals(logical, raw)
            ? owner.Failures.Count == 0
            : logical is OperationCanceledException && owner.Failures.Count == 1 && ReferenceEquals(owner.Failures[0], raw),
            "actual pump owns raw source either as its logical outcome OR as untransferred physical stage after cancellation, never both");
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task TimerCleanupMustFinishBeforeOutcomeTransferAndItsFailureRetainsSourceOwner()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var raw = new InvalidOperationException("source not yet transferred");
        var provider = new HeldDisposalTimeProvider();
        var wait = SharpLinkTimer.WaitForCompletionAsync(source.Task,
            RpcDeadline.Create(TimeSpan.FromSeconds(5), provider), provider).AsTask();
        source.SetException(raw);
        await provider.DisposeEntered.Task.WaitAsync(Timeout);
        Ensure(!wait.IsCompleted, "terminal source alone cannot transfer while actual timer cleanup remains held");
        provider.ReleaseDispose.SetResult();
        Ensure(ReferenceEquals(await Failure(wait), provider.DisposeFailure) && provider.DisposeCount == 1,
            "wait cleanup failure remains a wait failure rather than forwarding ordinary source error");
        Ensure(ReferenceEquals(await Failure(source.Task), raw), "unsuccessful wait leaves exact source for physical ownership");
    }

    [Test]
    public async Task PumpTimerCleanupFailureDoesNotEraseOriginalUntransferredSource()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "wait-cleanup-outcome", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var raw = new InvalidOperationException("untransferred actual MoveNext");
        var provider = new HeldDisposalTimeProvider();
        var owner = new RecordingOwner();
        var stream = new SourceStream(source.Task);
        Task pump;
        using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(session.Id, authentication: null,
                   RpcDeadline.Create(TimeSpan.FromSeconds(5), provider), provider)))
        {
            pump = session.PumpGeneratedOutboundStreamAsync(202, 0, stream, new FaultingSizedCodec(raw),
                payloadNullable: false, CancellationToken.None, owner).AsTask();
        }
        source.SetException(raw);
        await provider.DisposeEntered.Task.WaitAsync(Timeout);
        Ensure(!pump.IsCompleted && stream.DisposeCount == 0, "pending source remains owned through wait's physical cleanup");
        provider.ReleaseDispose.SetResult();
        Ensure(ReferenceEquals(await Failure(pump), provider.DisposeFailure) && owner.Failures.Count == 1 &&
            ReferenceEquals(owner.Failures[0], raw) && stream.DisposeCount == 1,
            "failed wait owns timer error; original source was never transferred and remains independent physical outcome");
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    [Test]
    public async Task DeadlineExpiringDuringHeldSuccessfulTimerTeardownKeepsSourcePhysical()
    {
        await CheckFinalTimerBoundaryAsync(throwFinalClock: false);
    }

    [Test]
    public async Task ThrowingFinalClockAfterTimerTeardownKeepsSourceAndCleansIndependentOwners()
    {
        await CheckFinalTimerBoundaryAsync(throwFinalClock: true);
    }

    private static async Task CheckFinalTimerBoundaryAsync(bool throwFinalClock)
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "final-timer-boundary", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var raw = new InvalidOperationException("source completed before held timer teardown");
        var provider = new HeldDisposalTimeProvider { ThrowOnDispose = false, ThrowOnFinalTimestamp = throwFinalClock };
        var owner = new RecordingOwner();
        var stream = new SourceStream(source.Task);
        Task pump;
        using (SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(session.Id, authentication: null,
                   RpcDeadline.Create(TimeSpan.FromSeconds(5), provider), provider)))
        {
            pump = session.PumpGeneratedOutboundStreamAsync(204, 0, stream, new FaultingSizedCodec(raw),
                payloadNullable: false, CancellationToken.None, owner).AsTask();
        }
        source.SetException(raw);
        await provider.DisposeEntered.Task.WaitAsync(Timeout);
        Ensure(!pump.IsCompleted && stream.DisposeCount == 0, "logical source transfer waits for actual timer teardown");
        if (!throwFinalClock) provider.Advance(TimeSpan.FromSeconds(5));
        provider.ReleaseDispose.SetResult();
        var failure = await Failure(pump);
        Ensure(throwFinalClock
            ? ReferenceEquals(failure, provider.ClockFailure)
            : failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "final ownership boundary rechecks the original absolute deadline; clock failure cannot claim source transfer");
        Ensure(owner.Failures.Count == 1 && ReferenceEquals(owner.Failures[0], raw) && stream.DisposeCount == 1 && provider.DisposeCount == 1,
            "source outcome remains independently physical and actual enumerator/timer cleanup each finish once");
        var lifetimeDisposed = false;
        try { _ = stream.LifetimeToken.WaitHandle; }
        catch (ObjectDisposedException) { lifetimeDisposed = true; }
        Ensure(lifetimeDisposed, "final deadline/clock failure cannot skip the original linked lifetime CTS disposal");
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private sealed class SourceStream(Task<bool>? source) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private bool _produced;
        internal int DisposeCount;
        internal CancellationToken LifetimeToken { get; private set; }
        public int Current => 7;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { LifetimeToken = cancellationToken; return this; }
        public ValueTask<bool> MoveNextAsync()
        {
            if (source is not null) return new ValueTask<bool>(source);
            if (_produced) return new ValueTask<bool>(false);
            _produced = true;
            return new ValueTask<bool>(true);
        }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class FaultingSizedCodec(Exception failure) : IRpcCodec<int>, IRpcSizedCodec<int>
    {
        public bool CanExactSize => true;
        public bool TryGetEncodedSize(in int value, out int size) { size = 4; return true; }
        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        { size = 4; snapshot = null; return true; }
        public void SerializeSized(in int value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot) => throw failure;
        public void Serialize(in int value, IBufferWriter<byte> buffer) => throw failure;
        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot) { }
        public int Deserialize(in ReadOnlySequence<byte> buffer) => throw new NotSupportedException();
    }

    private sealed class RecordingOwner : IGeneratedStreamPhysicalOwner
    {
        internal readonly List<Exception> Failures = [];
        internal readonly FrameworkTaskSupervisor Supervisor = new();
        internal int Retains, Releases;
        public void RetainPhysicalTail() => Retains++;
        public void ReleasePhysicalTail() => Releases++;
        public void ReportPhysicalFailure(GeneratedPhysicalFailureStage stage, Exception failure, ref bool accepted)
        { Failures.Add(failure); accepted = true; }
        public void TrackPhysicalTail(Task task, ref FrameworkTaskRegistration? registration)
            => Supervisor.TrackStrictPhysical(task, "outcome-test-tail", ref registration);
    }

    private sealed class HeldDisposalTimeProvider : TimeProvider
    {
        internal readonly TaskCompletionSource DisposeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Exception DisposeFailure = new InvalidOperationException("actual owned timer cleanup failure");
        internal readonly Exception ClockFailure = new InvalidOperationException("actual final clock read failure");
        internal bool ThrowOnDispose { get; init; } = true;
        internal bool ThrowOnFinalTimestamp { get; init; }
        internal int DisposeCount;
        private int _disposalCompleted;
        private long _timestamp;
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            if (ThrowOnFinalTimestamp && Volatile.Read(ref _disposalCompleted) != 0) throw ClockFailure;
            return Volatile.Read(ref _timestamp);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new HeldTimer(this);
        private sealed class HeldTimer(HeldDisposalTimeProvider owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => throw new InvalidOperationException("Completion-only wait must join actual asynchronous timer disposal.");
            public async ValueTask DisposeAsync()
            {
                owner.DisposeCount++;
                owner.DisposeEntered.TrySetResult();
                await owner.ReleaseDispose.Task.ConfigureAwait(false);
                Volatile.Write(ref owner._disposalCompleted, 1);
                if (owner.ThrowOnDispose) throw owner.DisposeFailure;
            }
        }
    }

    private static async Task<Exception> Failure(Task task)
    {
        try { await task.WaitAsync(Timeout); }
        catch (Exception error) when (error is not TimeoutException) { return error; }
        throw new InvalidOperationException("Expected original outcome failure.");
    }
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
