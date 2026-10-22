using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpLink.UnitTests.Runtime;

public class AbandonedGeneratedStreamOwnershipTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AbandonedMoveNextMustFinishBeforeDisposalAndRetainLifetime(bool cancel, bool lateFault)
    {
        await using var fixture = new Fixture();
        var stream = new GatedStream();
        var pump = fixture.Pump(stream);
        try
        {
            fixture.Terminate(cancel);
            var failure = await ObserveFailureAsync(pump);
            AssertTerminal(failure, cancel);
            var work = GetPhysicalWork(failure);
            Ensure(work is not null && !work.IsCompleted,
                "the terminal must carry incomplete physical cleanup ownership");
            Ensure(stream.DisposeCalls == 0, "Dispose must not overlap a pending MoveNext");
            Ensure(IsTokenSourceAlive(stream.Token), "pending MoveNext must retain its linked token source");

            stream.ReleaseMove(lateFault);
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            Ensure(!work.IsCompleted,
                "cleanup ownership must include asynchronous disposal, not only MoveNext");
            Ensure(IsTokenSourceAlive(stream.Token), "Dispose must retain the linked token source");
            stream.DisposeRelease.TrySetResult();
            await work.WaitAsync(Watchdog);
            Ensure(stream.DisposeCalls == 1 && !stream.ConcurrentDispose,
                "the enumerator must be disposed exactly once after physical MoveNext completion");
            Ensure(!IsTokenSourceAlive(stream.Token), "completed cleanup must dispose its linked token source");
        }
        finally
        {
            stream.ReleaseMove(lateFault: false);
            stream.DisposeRelease.TrySetResult();
            await DrainPhysicalWorkAsync(pump);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompilerGeneratedIteratorMustExecuteFinallyAfterAbandonment(bool cancel)
    {
        await using var fixture = new Fixture();
        var release = NewSignal();
        var entered = NewSignal();
        var finalized = NewSignal();
        var pump = fixture.Pump(Iterator(entered, release, finalized));
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            fixture.Terminate(cancel);
            var failure = await ObserveFailureAsync(pump);
            AssertTerminal(failure, cancel);
            Ensure(!finalized.Task.IsCompleted, "a suspended iterator must still own its finally scope");
            release.TrySetResult();
            await finalized.Task.WaitAsync(Watchdog);
            var work = GetPhysicalWork(failure);
            Ensure(work is not null, "compiler-generated iterator cleanup must have a physical owner");
            await work.WaitAsync(Watchdog);
        }
        finally
        {
            release.TrySetResult();
            await DrainPhysicalWorkAsync(pump);
        }
    }

    [Test]
    public async Task UntimedCooperativeCancellationMustPublishBeforeDisposalCompletes()
    {
        await using var fixture = new Fixture(withDeadline: false);
        var stream = new GatedStream(cooperativeMove: true);
        var pump = fixture.Pump(stream);
        try
        {
            fixture.Terminate(cancel: true);
            var failure = await ObserveFailureAsync(pump);
            AssertTerminal(failure, cancel: true);
            Ensure(failure is TaskCanceledException, "a canceled producer Task must retain TaskCanceledException type");
            var work = GetPhysicalWork(failure);
            Ensure(work is not null && !work.IsCompleted,
                "untimed cancellation must retain unfinished disposal after its public terminal");
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            Ensure(IsTokenSourceAlive(stream.Token), "untimed disposal must retain its linked source");
            stream.DisposeRelease.TrySetResult();
            await work.WaitAsync(Watchdog);
            Ensure(stream.DisposeCalls == 1 && !stream.ConcurrentDispose,
                "cooperative cancellation must dispose exactly once after completed MoveNext");
        }
        finally
        {
            stream.DisposeRelease.TrySetResult();
            await DrainPhysicalWorkAsync(pump);
        }
    }

    [Test]
    public async Task SynchronouslyBlockingDisposeMustNotBlockAnAlreadySelectedDeadline()
    {
        await using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        var stream = new SynchronouslyBlockingDisposeStream(release);
        // Both the expired deadline and faulted source are already complete. Cleanup must
        // nevertheless force asynchronous entry before calling arbitrary DisposeAsync code.
        fixture.Terminate(cancel: false);
        var pump = Task.Run(() => fixture.Pump(stream));
        try
        {
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            var failure = await ObserveFailureAsync(pump);
            AssertTerminal(failure, cancel: false);
            var work = GetPhysicalWork(failure);
            Ensure(work is not null && !work.IsCompleted,
                "a synchronously blocked Dispose must remain owned without blocking the public deadline");
            Ensure(IsTokenSourceAlive(stream.Token), "blocking Dispose must retain its linked source");
            release.Set();
            await work.WaitAsync(Watchdog);
            Ensure(stream.DisposeCalls == 1, "blocking disposal must be invoked exactly once");
        }
        finally
        {
            release.Set();
            await DrainPhysicalWorkAsync(pump);
        }
    }

    [Test]
    public async Task BlockingCancellationCallbackMustNotHoldTheSelectedDeadline()
    {
        await using var fixture = new Fixture();
        using var callbackRelease = new ManualResetEventSlim();
        var stream = new GatedStream(cancellationRelease: callbackRelease);
        var pump = fixture.Pump(stream);
        try
        {
            fixture.Terminate(cancel: false);
            await stream.CancellationEntered.Task.WaitAsync(Watchdog);
            var failure = await ObserveFailureAsync(pump);
            AssertTerminal(failure, cancel: false);
            var work = GetPhysicalWork(failure);
            Ensure(work is not null && !work.IsCompleted,
                "synchronous cancellation callbacks must be inside physical cleanup ownership");
            Ensure(stream.DisposeCalls == 0, "blocked cancellation must not race enumerator disposal");
            callbackRelease.Set();
            stream.ReleaseMove(lateFault: false);
            stream.DisposeRelease.TrySetResult();
            await work.WaitAsync(Watchdog);
        }
        finally
        {
            callbackRelease.Set();
            stream.ReleaseMove(lateFault: false);
            stream.DisposeRelease.TrySetResult();
            await DrainPhysicalWorkAsync(pump);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AbandonedSendMustFinishBeforeDisposal(bool cancel, bool lateFault)
    {
        await using var fixture = new Fixture(flowControlled: true);
        using var codecRelease = new ManualResetEventSlim();
        var codec = new BlockingSizedCodec(codecRelease, lateFault);
        var stream = new GatedStream(singleItem: true);
        await fixture.Session.AcquireStreamSendCreditAsync(900, 0, sizeof(int), CancellationToken.None);
        var pump = fixture.Pump(stream, codec);
        try
        {
            // The first credit wait makes the send asynchronous before user serialization blocks.
            fixture.Session.ApplyWindowUpdate(900, new ProtocolV2WindowUpdate(0, sizeof(int)));
            await codec.Entered.Task.WaitAsync(Watchdog);
            fixture.Terminate(cancel);
            var failure = await ObserveFailureAsync(pump);
            AssertTerminal(failure, cancel);
            var work = GetPhysicalWork(failure);
            Ensure(work is not null && !work.IsCompleted, "the pending send must be physically owned");
            Ensure(stream.DisposeCalls == 0, "Dispose must not overlap a framework-abandoned send");
            Ensure(IsTokenSourceAlive(stream.Token), "pending send must retain its linked token source");
            codecRelease.Set();
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            Ensure(!work.IsCompleted, "send ownership must extend through stream cleanup");
            stream.DisposeRelease.TrySetResult();
            await work.WaitAsync(Watchdog);
            Ensure(stream.DisposeCalls == 1, "abandoned send must dispose the producer exactly once");
        }
        finally
        {
            codecRelease.Set();
            stream.DisposeRelease.TrySetResult();
            await DrainPhysicalWorkAsync(pump);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ReusedPublicTerminalMustNotMakeCleanupAwaitItself(bool throwFromDispose, bool alias)
    {
        await using var fixture = new Fixture();
        var stream = new GatedStream();
        var pump = fixture.Pump(stream);
        try
        {
            fixture.Terminate(cancel: false);
            var failure = await ObserveFailureAsync(pump);
            var work = GetPhysicalWork(failure);
            Ensure(work is not null, "the terminal must have a physical owner");
            if (alias)
            {
                var replacement = new InvalidOperationException("mapped public terminal", failure);
                PhysicalWorkType?.GetMethod("PreserveAbandonedUserWork", BindingFlags.NonPublic | BindingFlags.Static)
                    ?.Invoke(null, [failure, replacement]);
                failure = replacement;
                Ensure(ReferenceEquals(work, GetPhysicalWork(failure)), "the mapped terminal must alias the same owner");
            }
            if (throwFromDispose)
            {
                stream.ReleaseMove(lateFault: false);
                await stream.DisposeEntered.Task.WaitAsync(Watchdog);
                stream.DisposeRelease.TrySetException(failure);
            }
            else
            {
                stream.FaultMove(failure);
                stream.DisposeRelease.TrySetResult();
            }
            await work.WaitAsync(Watchdog);
            Ensure(stream.DisposeCalls == 1, "reusing the terminal must still complete disposal once");
        }
        finally
        {
            stream.ReleaseMove(lateFault: false);
            stream.DisposeRelease.TrySetResult();
            await DrainPhysicalWorkAsync(pump);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ConsumedSourceTerminalMustRetainNestedWorkAndDisposal(bool cancel, bool nestedPhysicalWork)
    {
        await using var fixture = new Fixture(withDeadline: !cancel);
        var stream = new GatedStream();
        var descendant = NewSignal();
        var sourceToken = new CancellationToken(canceled: true);
        Exception nestedTerminal = cancel
            ? new OperationCanceledException("nested source cancellation", sourceToken)
            : new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, 421, "nested source deadline");
        if (nestedPhysicalWork)
        {
            TrackPhysicalWork(nestedTerminal, descendant.Task);
        }
        var pump = fixture.Pump(stream);
        try
        {
            if (!nestedPhysicalWork)
                fixture.Terminate(cancel);
            stream.FaultMove(nestedTerminal);
            var failure = await ObserveFailureAsync(pump);
            if (cancel)
                Ensure(failure is OperationCanceledException cancellation && cancellation.CancellationToken == sourceToken,
                    "source cancellation mapping must preserve its independent token");
            else if (nestedPhysicalWork)
                Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded, DetailCode: 421 },
                    "source deadline mapping must preserve its code and detail");
            else
                AssertTerminal(failure, cancel: false);
            Ensure(!ReferenceEquals(failure, nestedTerminal), "the terminal association requires a fresh exception");
            if (nestedPhysicalWork || cancel)
                Ensure(failure.Message == nestedTerminal.Message, "source terminal mapping must preserve its message");
            var work = GetPhysicalWork(failure);
            Ensure(work is not null && !work.IsCompleted,
                "consumed source terminal must retain asynchronous cleanup");
            if (nestedPhysicalWork)
                Ensure(stream.DisposeCalls == 0, "Dispose must follow nested physical work, not its terminal wrapper");
            descendant.TrySetResult();
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            Ensure(!work.IsCompleted, "physical ownership must continue through final disposal");
            stream.DisposeRelease.TrySetResult();
            await work.WaitAsync(Watchdog);
        }
        finally
        {
            descendant.TrySetResult();
            stream.DisposeRelease.TrySetResult();
            await DrainPhysicalWorkAsync(pump);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CrossRecycledTerminalsMustDrainBothOwnersWithoutDeadlock(bool recycleFromDispose)
    {
        await using var fixtureA = new Fixture();
        await using var fixtureB = new Fixture();
        var streamA = new GatedStream();
        var streamB = new GatedStream();
        var pumpA = fixtureA.Pump(streamA);
        var pumpB = fixtureB.Pump(streamB);
        try
        {
            fixtureA.Terminate(cancel: false);
            fixtureB.Terminate(cancel: false);
            var terminalA = await ObserveFailureAsync(pumpA);
            var terminalB = await ObserveFailureAsync(pumpB);
            var workA = GetPhysicalWork(terminalA);
            var workB = GetPhysicalWork(terminalB);
            Ensure(workA is not null && workB is not null, "both terminals must retain physical owners");
            if (recycleFromDispose)
            {
                streamA.ReleaseMove(lateFault: false);
                streamB.ReleaseMove(lateFault: false);
                await Task.WhenAll(streamA.DisposeEntered.Task, streamB.DisposeEntered.Task).WaitAsync(Watchdog);
                streamA.DisposeRelease.TrySetException(terminalB);
                streamB.DisposeRelease.TrySetException(terminalA);
            }
            else
            {
                streamA.FaultMove(terminalB);
                streamB.FaultMove(terminalA);
                await Task.WhenAll(streamA.DisposeEntered.Task, streamB.DisposeEntered.Task).WaitAsync(Watchdog);
                streamA.DisposeRelease.TrySetResult();
                Ensure(!workA.IsCompleted && !workB.IsCompleted,
                    "a dependency cycle must retain both owners until all local cleanup completes");
                Ensure(IsTokenSourceAlive(streamA.Token) && IsTokenSourceAlive(streamB.Token),
                    "a cleanup component must retain every linked lifetime until its last disposal");
                streamB.DisposeRelease.TrySetResult();
            }
            await Task.WhenAll(workA, workB).WaitAsync(Watchdog);
            Ensure(streamA.DisposeCalls == 1 && streamB.DisposeCalls == 1,
                "cross-recycled terminals must dispose each producer once");
            Ensure(!IsTokenSourceAlive(streamA.Token) && !IsTokenSourceAlive(streamB.Token),
                "a completed cleanup component must release both linked sources");
        }
        finally
        {
            streamA.ReleaseMove(lateFault: false);
            streamB.ReleaseMove(lateFault: false);
            streamA.DisposeRelease.TrySetResult();
            streamB.DisposeRelease.TrySetResult();
            await Task.WhenAll(DrainPhysicalWorkAsync(pumpA), DrainPhysicalWorkAsync(pumpB));
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EnumeratorFactoryFaultMustRetainLifetimeUntilNestedWorkCompletes(bool cancel)
    {
        await using var fixture = new Fixture(withDeadline: false);
        var descendant = NewSignal();
        Exception terminal = cancel
            ? new OperationCanceledException("nested factory cancellation", new CancellationToken(canceled: true))
            : new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "nested factory deadline");
        TrackPhysicalWork(terminal, descendant.Task);
        var stream = new ThrowingEnumeratorStream(terminal);
        var pump = fixture.Pump(stream);
        try
        {
            var failure = await ObserveFailureAsync(pump);
            AssertTerminal(failure, cancel);
            Ensure(failure.Message == terminal.Message, "factory failure must preserve its original message");
            var work = GetPhysicalWork(failure);
            Ensure(work is not null && !work.IsCompleted, "a failed enumerator factory must retain its physical descendant");
            Ensure(IsTokenSourceAlive(stream.Token), "factory descendant must retain its linked token source");
            descendant.TrySetResult();
            await work.WaitAsync(Watchdog);
            Ensure(!IsTokenSourceAlive(stream.Token), "factory descendant completion must release the linked source");
        }
        finally
        {
            descendant.TrySetResult();
            await DrainPhysicalWorkAsync(pump);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NormalDisposalFaultMustRetainLifetimeUntilNestedWorkCompletes(bool cancel)
    {
        await using var fixture = new Fixture(withDeadline: false);
        var stream = new GatedStream();
        var descendant = NewSignal();
        Exception terminal = cancel
            ? new OperationCanceledException("nested disposal cancellation", new CancellationToken(canceled: true))
            : new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "nested disposal deadline");
        TrackPhysicalWork(terminal, descendant.Task);
        stream.ReleaseMove(lateFault: false);
        stream.DisposeRelease.TrySetException(terminal);
        var pump = fixture.Pump(stream);
        try
        {
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            var failure = await ObserveFailureAsync(pump);
            AssertTerminal(failure, cancel);
            Ensure(failure.Message == terminal.Message, "normal disposal must preserve its original message");
            var work = GetPhysicalWork(failure);
            Ensure(work is not null && !work.IsCompleted, "ordinary disposal must retain its physical descendant");
            Ensure(IsTokenSourceAlive(stream.Token), "nested disposal must retain its linked token source");
            descendant.TrySetResult();
            await work.WaitAsync(Watchdog);
            Ensure(stream.DisposeCalls == 1, "nested disposal must not repeat DisposeAsync");
            Ensure(!IsTokenSourceAlive(stream.Token), "nested disposal completion must release the linked source");
        }
        finally
        {
            descendant.TrySetResult();
            await DrainPhysicalWorkAsync(pump);
        }
    }

    [Test]
    public async Task SynchronousEofCrossingDeadlineMustNotWaitForDisposal()
    {
        await using var fixture = new Fixture();
        var stream = new GatedStream();
        stream.ReleaseMove(lateFault: false);
        fixture.Terminate(cancel: false);
        var pump = fixture.Pump(stream);
        try
        {
            var failure = await ObserveFailureAsync(pump);
            AssertTerminal(failure, cancel: false);
            var work = GetPhysicalWork(failure);
            Ensure(work is not null && !work.IsCompleted, "EOF deadline must retain incomplete disposal");
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            stream.DisposeRelease.TrySetResult();
            await work.WaitAsync(Watchdog);
        }
        finally
        {
            stream.DisposeRelease.TrySetResult();
            await DrainPhysicalWorkAsync(pump);
        }
    }

    [Test]
    public async Task RetainedTerminalMustNotRootCompletedStreamCleanup()
    {
        await using var fixture = new Fixture();
        var (terminal, streamReference) = await CompleteAndReleaseStreamAsync(fixture);
        for (var attempt = 0; attempt < 5 && streamReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }
        Ensure(!streamReference.IsAlive, "a retained terminal must not retain its completed producer");
        GC.KeepAlive(terminal);
        GC.KeepAlive(fixture);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(Exception Terminal, WeakReference Stream)> CompleteAndReleaseStreamAsync(Fixture fixture)
    {
        var stream = new GatedStream();
        var weak = new WeakReference(stream);
        var pump = fixture.Pump(stream);
        fixture.Terminate(cancel: false);
        var failure = await ObserveFailureAsync(pump);
        stream.ReleaseMove(lateFault: false);
        stream.DisposeRelease.TrySetResult();
        var work = GetPhysicalWork(failure);
        Ensure(work is not null, "retention control requires tracked cleanup");
        await work.WaitAsync(Watchdog);
        return (failure, weak);
    }

    private static async IAsyncEnumerable<int> Iterator(
        TaskCompletionSource entered, TaskCompletionSource release, TaskCompletionSource finalized)
    {
        try
        {
            entered.TrySetResult();
            await release.Task;
            yield return 7;
        }
        finally
        {
            finalized.TrySetResult();
        }
    }

    // Reflection keeps this exact regression source compilable against the unchanged baseline.
    // A missing owner API is deliberately observed as no physical work, not a skipped test.
    private static Type? PhysicalWorkType
        => typeof(SharpLinkRuntimeContext).Assembly.GetType("SharpLink.Runtime.SharpLinkPhysicalWork");

    private static void TrackPhysicalWork(Exception terminal, Task work)
    {
        var owner = PhysicalWorkType?.GetMethod("CreateOwner", BindingFlags.NonPublic | BindingFlags.Static)
            ?.Invoke(null, [terminal, null]);
        owner?.GetType().GetMethod("Track", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(owner, [work]);
    }

    private static Task? GetPhysicalWork(Exception exception)
        => (Task?)PhysicalWorkType?.GetMethod(
            "GetAbandonedUserWork", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, [exception]);

    private static bool IsTokenSourceAlive(CancellationToken token)
    {
        try { _ = token.WaitHandle; return true; }
        catch (ObjectDisposedException) { return false; }
    }

    private static async Task<Exception> ObserveFailureAsync(Task task)
    {
        try { await task.WaitAsync(Watchdog); }
        catch (Exception exception) when (exception is not TimeoutException) { return exception; }
        throw new InvalidOperationException("The generated pump did not produce its terminal failure.");
    }

    private static async Task DrainPhysicalWorkAsync(Task task)
    {
        try { await task.WaitAsync(Watchdog); }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            if (GetPhysicalWork(exception) is { } work)
                await work.WaitAsync(Watchdog);
        }
    }

    private static void AssertTerminal(Exception exception, bool cancel)
        => Ensure(cancel ? exception is OperationCanceledException :
                exception is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            $"expected {(cancel ? "cancellation" : "deadline")}, observed {exception.GetType().Name}");

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Ensure([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Generated physical ownership assertion failed: {message}.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ManualTimeProvider _clock = new();
        private readonly RpcDeadline _deadline;
        internal SharpLinkRuntimeContext Context { get; } =
            new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        internal RpcSession Session { get; }

        internal Fixture(bool flowControlled = false, bool withDeadline = true)
        {
            Session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                "generated-physical-ownership", _input.Reader, _output.Writer,
                RpcSessionTestFixture.ServerOptions(Context), completeHandshake: false);
            RpcSessionTestFixture.CompleteHandshake(Session,
                flowControlled ? ProtocolV2Capabilities.FlowControl : ProtocolV2Capabilities.None,
                streamReceiveWindowBytes: sizeof(int), connectionReceiveWindowBytes: sizeof(int));
            _deadline = withDeadline ? RpcDeadline.Create(TimeSpan.FromSeconds(1), _clock) : default;
        }

        internal Task Pump(IAsyncEnumerable<int> stream, IRpcCodec<int>? codec = null)
        {
            using var scope = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(
                Session.Id, authentication: null, _deadline, _clock));
            return Session.PumpGeneratedOutboundStreamAsync(
                73, 0, stream, codec ?? new IntCodec(), payloadNullable: false, _cancellation.Token).AsTask();
        }

        internal void Terminate(bool cancel)
        {
            if (cancel)
                _cancellation.Cancel();
            else
                _clock.Advance(TimeSpan.FromSeconds(1));
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
            _cancellation.Dispose();
            Context.Dispose();
        }
    }

    private sealed class ThrowingEnumeratorStream(Exception failure) : IAsyncEnumerable<int>
    {
        internal CancellationToken Token { get; private set; }

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            throw failure;
        }
    }

    private sealed class GatedStream(
        bool singleItem = false, ManualResetEventSlim? cancellationRelease = null, bool cooperativeMove = false) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private readonly TaskCompletionSource<bool> _move = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCalls;
        private bool _moved;
        internal TaskCompletionSource CancellationEntered { get; } = NewSignal();
        internal TaskCompletionSource DisposeEntered { get; } = NewSignal();
        internal TaskCompletionSource DisposeRelease { get; } = NewSignal();
        internal CancellationToken Token { get; private set; }
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal bool ConcurrentDispose { get; private set; }
        public int Current => 7;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            if (cooperativeMove)
                cancellationToken.Register(() => _move.TrySetCanceled(cancellationToken));
            if (cancellationRelease is not null)
                cancellationToken.Register(() =>
                {
                    CancellationEntered.TrySetResult();
                    if (!cancellationRelease.Wait(Watchdog))
                        throw new TimeoutException("The cancellation callback test gate was not released.");
                });
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            if (!singleItem)
                return new(_move.Task);
            var hasNext = !_moved;
            _moved = true;
            return ValueTask.FromResult(hasNext);
        }
        internal void FaultMove(Exception exception) => _move.TrySetException(exception);
        internal void ReleaseMove(bool lateFault)
        {
            if (lateFault)
                _move.TrySetException(new InvalidOperationException("late MoveNext failure"));
            else
                _move.TrySetResult(false);
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            ConcurrentDispose = !singleItem && !_move.Task.IsCompleted;
            DisposeEntered.TrySetResult();
            await DisposeRelease.Task;
        }
    }

    private sealed class SynchronouslyBlockingDisposeStream(ManualResetEventSlim release)
        : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource DisposeEntered { get; } = NewSignal();
        internal CancellationToken Token { get; private set; }
        internal int DisposeCalls { get; private set; }
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
            => ValueTask.FromException<bool>(new InvalidOperationException("source completed after deadline"));
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            DisposeEntered.TrySetResult();
            if (!release.Wait(Watchdog))
                throw new TimeoutException("The synchronous disposal test gate was not released.");
            return ValueTask.CompletedTask;
        }
    }

    private class IntCodec : IRpcCodec<int>
    {
        public virtual void Serialize(in int value, IBufferWriter<byte> buffer)
        {
            BitConverter.TryWriteBytes(buffer.GetSpan(sizeof(int)), value);
            buffer.Advance(sizeof(int));
        }
        public int Deserialize(in ReadOnlySequence<byte> buffer) => BitConverter.ToInt32(buffer.FirstSpan);
    }

    private sealed class BlockingSizedCodec(ManualResetEventSlim release, bool lateFault)
        : IntCodec, IRpcSizedCodec<int>
    {
        internal TaskCompletionSource Entered { get; } = NewSignal();
        public bool CanExactSize => true;
        public bool TryGetEncodedSize(in int value, out int size) { size = sizeof(int); return true; }
        public bool TryGetEncodedSize(in int value, out int size, out IRpcSizedCodecSnapshot? snapshot)
        { size = sizeof(int); snapshot = null; return true; }
        public void SerializeSized(in int value, IBufferWriter<byte> buffer, int size, IRpcSizedCodecSnapshot? snapshot)
        {
            Entered.TrySetResult();
            if (!release.Wait(Watchdog))
                throw new TimeoutException("The serialization test gate was not released.");
            if (lateFault)
                throw new InvalidOperationException("late serialization failure");
            Serialize(value, buffer);
        }
        public void ReleaseSnapshot(IRpcSizedCodecSnapshot? snapshot) { }
    }
}
