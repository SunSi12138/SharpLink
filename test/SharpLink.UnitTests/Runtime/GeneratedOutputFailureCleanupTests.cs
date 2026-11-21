using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public sealed class GeneratedOutputFailureCleanupTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EofDeadlineMustHandOffBeforeSynchronousDisposal(bool lateCleanupFailure)
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-eof-deadline-disposal", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        await LongRunningTestWorker.Run(() => ObserveEofDeadline(session, lateCleanupFailure));
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static void ObserveEofDeadline(RpcSession session, bool lateCleanupFailure)
    {
        var clock = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), clock);
        var observations = new EofPhaseObservations();
        var observerThreadId = Environment.CurrentManagedThreadId;
        observations.Record("observer-start");
        var stream = new EofDeadlineStream(clock, deadline, lateCleanupFailure, observations);
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationThread = new Thread(() =>
        {
            observations.Record("invocation-thread-entered");
            try
            {
                using var context = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot(session.Id, null, deadline, clock));
                observations.Record("context-installed");
                var logical = new RpcSessionGeneratedServerBridge(session).PumpOutboundStreamAsync(
                    73, 0, stream, new IntCodec(), new RpcMethodDescriptor(101, 202, RpcMethodKind.ServerStreaming, true, false, false, null) { SupportsCancellation = true }, CancellationToken.None).AsTask();
                observations.Record("logical-valuetask-returned:" + logical.Status);
                var published = returned.TrySetResult(logical);
                observations.Record($"returned-published:won={published}:state={returned.Task.Status}");
            }
            catch (Exception error)
            {
                observations.Record("invocation-threw:" + error.GetType().Name);
                returned.TrySetException(error);
            }
        })
        { IsBackground = true };
        invocationThread.Start();
        var phase = "returned";
        string? failureSnapshot = null;
        ExceptionDispatchInfo? oracleFailure = null;
        try
        {
            try
            {
                // Logical completion must not depend on when the cold disposal worker starts.
                observations.Record("observe-returned-start");
                var logical = ObserveTask(returned.Task, TimeSpan.FromSeconds(2));
                phase = "logical-deadline";
                observations.Record("observe-logical-start:" + logical.Status);
                Exception? terminal = null;
                try { ObserveTask(logical, TimeSpan.FromSeconds(2)); }
                catch (Exception error) { terminal = error; }
                observations.Record("logical-observed:" + (terminal as SharpLinkException)?.Code + ":" + logical.Status);
                Ensure(terminal is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                    "EOF-selected framework deadline must return before a synchronous disposal prefix exits");
                phase = "dispose-entered";
                observations.Record("observe-dispose-start");
                ObserveTask(stream.DisposeEntered.Task, TimeSpan.FromSeconds(2));
                phase = "held-physical-owner";
                Ensure(stream.DisposeCount == 1 && Volatile.Read(ref stream.DisposeExited) == 0 && !stream.Release.IsSet &&
                    stream.DisposeThreadId != observerThreadId && invocationThread.ManagedThreadId != observerThreadId &&
                    !stream.Tail.JoinAsync().IsCompleted && !SourceDisposed(stream.Token),
                    "unique cold disposal must retain the original CTS while its synchronous prefix is held");
                observations.Record("held-cts-live:dispose-count=1");
            }
            catch (Exception error)
            {
                failureSnapshot = CaptureFailure(error);
                oracleFailure = ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                phase = "cleanup";
                FinishEof(returned.Task, stream.Tail, invocationThread,
                    stream.Release.Set, stream.Dispose, observations.Record, oracleFailure);
            }
            phase = "physical-exit-owner";
            Ensure(stream.DisposeCount == 1 && Volatile.Read(ref stream.DisposeExited) == 1 && SourceDisposed(stream.Token),
                "late disposal success or fault must release the original CTS once, without replacing the deadline");
            observations.Record("physical-exit-cts-disposed:dispose-count=1");
        }
        catch (Exception error)
        {
            failureSnapshot ??= CaptureFailure(error);
            throw;
        }
        finally
        {
            Console.WriteLine($"EOF_DEADLINE_PHASES_V1 lateCleanupFailure={lateCleanupFailure}; failure={failureSnapshot ?? "none"}; {observations}");
        }

        string CaptureFailure(Exception error)
            => $"phase={phase}; error={error.GetType().Name}; returned={returned.Task.Status}; " +
                $"logical={(returned.Task.IsCompletedSuccessfully ? returned.Task.Result.Status.ToString() : "not-returned")}; " +
                $"disposeEntered={stream.DisposeEntered.Task.IsCompleted}; disposeCount={Volatile.Read(ref stream.DisposeCount)}; " +
                $"deadlineExpired={deadline.IsExpired(clock)}; linkedCtsDisposed={(stream.Token.CanBeCanceled ? SourceDisposed(stream.Token).ToString() : "not-captured")}";
    }

    private static void WaitForOwnedTask(Task task, TimeSpan timeout)
    {
        Ensure(!Thread.CurrentThread.IsThreadPoolThread, "owned task observations require a dedicated observer");
        // Task.Wait uses SetOnInvokeMres (InvokeMayRunArbitraryCode=false), not a
        // separate WaitAsync timeout promise whose RCAA notification can be queued.
        // Never repair a false wait with a later IsCompleted observation.
        try
        {
            if (!task.Wait(timeout))
                throw new TimeoutException("The owned task did not signal within its observation phase.");
        }
        catch (AggregateException) when (task.IsCompleted)
        {
            // Read the original task below, not Task.Wait's exception wrapper.
        }
    }

    private static void ObserveTask(Task task, TimeSpan timeout)
    {
        WaitForOwnedTask(task, timeout);
        task.GetAwaiter().GetResult();
    }

    private static T ObserveTask<T>(Task<T> task, TimeSpan timeout)
    {
        WaitForOwnedTask(task, timeout);
        return task.GetAwaiter().GetResult();
    }

    private static void FinishEof(
        Task<Task> returned, GeneratedOutputPhysicalTail tail, Thread invocationThread,
        Action release, Action disposeGate, Action<string> record, ExceptionDispatchInfo? oracleFailure)
    {
        ExceptionDispatchInfo? cleanupFailure = null;
        Task? logical = null;
        var physicalJoined = false;
        var threadJoined = false;
        record("release");
        try { release(); }
        catch (Exception error) { RecordFailure("release", error); }
        try { logical = ObserveTask(returned, TimeSpan.FromSeconds(5)); }
        catch (Exception error) { RecordFailure("cleanup-returned", error); }
        if (logical is not null)
        {
            if (!logical.IsCompleted)
                RecordFailure("cleanup-logical-pending", new InvalidOperationException(
                    "The logical invocation is still pending; independent physical cleanup must continue."));
            else
            {
                try { logical.GetAwaiter().GetResult(); }
                catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.DeadlineExceeded) { }
                catch (Exception error) { RecordFailure("cleanup-logical", error); }
            }
        }
        try
        {
            ObserveTask(tail.JoinAsync().AsTask(), TimeSpan.FromSeconds(5));
            physicalJoined = true;
            record("physical-join-exited");
        }
        catch (Exception error) { RecordFailure("physical-join", error); }
        try
        {
            Ensure(invocationThread.Join(TimeSpan.FromSeconds(2)), "logical invocation thread must exit independently of disposal");
            threadJoined = true;
            record("invocation-thread-joined");
        }
        catch (Exception error) { RecordFailure("invocation-thread-join", error); }
        // A failed bounded join is not physical exit. Keep its already released gate alive
        // rather than disposing it underneath a still-running Wait/Set on the failure path.
        if (physicalJoined && threadJoined)
        {
            try { disposeGate(); }
            catch (Exception error) { RecordFailure("dispose-gate", error); }
        }
        else
            record("gate-kept:physical-or-thread-not-joined");
        oracleFailure?.Throw();
        cleanupFailure?.Throw();

        void RecordFailure(string phase, Exception error)
        {
            cleanupFailure ??= ExceptionDispatchInfo.Capture(error);
            record($"cleanup-failure:{phase}:{error}");
        }
    }

    [Test]
    public Task UnexpectedLogicalFaultMustJoinPhysicalTailAndPreserveOracleFailure()
        => LongRunningTestWorker.Run(UnexpectedLogicalFaultMustJoinPhysicalTailAndPreserveOracleFailureCore);

    private static void UnexpectedLogicalFaultMustJoinPhysicalTailAndPreserveOracleFailureCore()
    {
        var tail = new GeneratedOutputPhysicalTail();
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new EofCleanupCancellation();
        tail.Add(work.Task);
        tail.Seal(cancellation);
        var release = new ManualResetEventSlim();
        var gateDisposals = 0;
        var invocationThread = new Thread(() =>
        {
            try
            {
                if (release.Wait(TimeSpan.FromSeconds(5))) work.TrySetResult();
                else work.TrySetException(new TimeoutException("cleanup control gate was not released"));
            }
            catch (Exception error) { work.TrySetException(error); }
        })
        { IsBackground = true };
        invocationThread.Start();
        ExceptionDispatchInfo oracleFailure;
        try { throw new InvalidOperationException("original EOF oracle failure"); }
        catch (Exception error) { oracleFailure = ExceptionDispatchInfo.Capture(error); }
        var originalStack = oracleFailure.SourceException.StackTrace!;
        var logicalFailure = new InvalidOperationException("unexpected EOF logical failure");
        var returned = Task.FromResult(Task.FromException(logicalFailure));
        var events = new List<string>();
        Exception? actual = null;
        try
        {
            FinishEof(returned, tail, invocationThread, release.Set, () =>
            {
                Ensure(!invocationThread.IsAlive && tail.JoinAsync().IsCompleted && cancellation.DisposeCount == 1,
                    "the gate can dispose only after the real thread, physical tail and original CTS exit");
                release.Dispose();
                gateDisposals++;
            }, events.Add, oracleFailure);
        }
        catch (Exception error) { actual = error; }
        finally
        {
            // A regression that skips joins must not dispose a still-in-flight Wait.
            if (gateDisposals == 0) release.Set();
            Console.WriteLine("EOF_CLEANUP_FAULT_V1 " + string.Join("; ", events));
        }
        Ensure(actual is not null && ReferenceEquals(actual, oracleFailure.SourceException) &&
            actual.StackTrace!.StartsWith(originalStack, StringComparison.Ordinal),
            "cleanup faults must not replace the first oracle exception or its original stack");
        Ensure(events.Contains("physical-join-exited") && events.Contains("invocation-thread-joined") &&
            events.Any(item => item.Contains("cleanup-failure:cleanup-logical:", StringComparison.Ordinal) &&
                item.Contains(logicalFailure.Message, StringComparison.Ordinal)),
            "the unexpected logical fault must be reported without skipping either real join");
        Ensure(gateDisposals == 1 && cancellation.DisposeCount == 1,
            "successful physical cleanup must release the original CTS and gate once");
        work.Task.GetAwaiter().GetResult();
    }

    [Test]
    public Task PendingLogicalMustNotBlockPhysicalCleanupOrReplaceOracleFailure()
        => LongRunningTestWorker.Run(PendingLogicalMustNotBlockPhysicalCleanupOrReplaceOracleFailureCore);

    private static void PendingLogicalMustNotBlockPhysicalCleanupOrReplaceOracleFailureCore()
    {
        var tail = new GeneratedOutputPhysicalTail();
        var work = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new EofCleanupCancellation();
        tail.Add(work.Task);
        tail.Seal(cancellation);
        var release = new ManualResetEventSlim();
        var releaseCalls = 0;
        var gateDisposals = 0;
        var invocationThread = new Thread(() =>
        {
            try
            {
                if (release.Wait(TimeSpan.FromSeconds(5))) work.TrySetResult();
                else work.TrySetException(new TimeoutException("pending cleanup control gate was not released"));
            }
            catch (Exception error) { work.TrySetException(error); }
        })
        { IsBackground = true };
        invocationThread.Start();
        ExceptionDispatchInfo oracleFailure;
        try { throw new TimeoutException("original logical observation failure"); }
        catch (Exception error) { oracleFailure = ExceptionDispatchInfo.Capture(error); }
        var originalStack = oracleFailure.SourceException.StackTrace!;
        var logical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = Task.FromResult(logical.Task);
        var events = new List<string>();
        Ensure(returned.IsCompletedSuccessfully && !logical.Task.IsCompleted,
            "the control must supply an already-returned but still-pending logical task");
        var finishing = LongRunningTestWorker.Run(() => FinishEof(returned, tail, invocationThread, () =>
        {
            releaseCalls++;
            release.Set();
        }, () =>
        {
            Ensure(!invocationThread.IsAlive && tail.JoinAsync().IsCompleted && cancellation.DisposeCount == 1,
                "pending logical work cannot prevent independent physical resources from exiting");
            release.Dispose();
            gateDisposals++;
        }, events.Add, oracleFailure));
        Exception? actual = null;
        var logicalPendingAtDelivery = false;
        try
        {
            try { ObserveTask(finishing, TimeSpan.FromSeconds(5 + 5 + 2)); }
            catch (Exception error) { actual = error; }
            logicalPendingAtDelivery = !logical.Task.IsCompleted;
        }
        finally
        {
            // Release ran synchronously before the dedicated cleanup worker began its observations.
            // Unblock only this synthetic logical gate, then finish the same cleanup task.
            logical.TrySetResult();
            try { finishing.GetAwaiter().GetResult(); }
            catch (Exception error) { events.Add("cleanup-delivered:" + error); }
            Console.WriteLine($"EOF_CLEANUP_PENDING_V1 logicalPendingAtDelivery={logicalPendingAtDelivery}; " + string.Join("; ", events));
        }
        Ensure(actual is not null && ReferenceEquals(actual, oracleFailure.SourceException) &&
            actual.StackTrace!.StartsWith(originalStack, StringComparison.Ordinal),
            "pending logical work must not replace the first oracle exception or its original stack");
        Ensure(logicalPendingAtDelivery && events.Any(item => item.Contains("cleanup-failure:cleanup-logical-pending:", StringComparison.Ordinal)) &&
            events.Contains("physical-join-exited") && events.Contains("invocation-thread-joined"),
            "the original error and both real joins must finish while the logical task remains pending");
        Ensure(releaseCalls == 1 && gateDisposals == 1 && cancellation.DisposeCount == 1,
            "pending logical work must still release the original physical resources once");
        work.Task.GetAwaiter().GetResult();
    }

    private sealed class EofCleanupCancellation : CancellationTokenSource
    {
        public int DisposeCount;
        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref DisposeCount);
            base.Dispose(disposing);
        }
    }

    [Test]
    public Task OwnedTaskObservationMustPreserveCompletionFaultAndTimeout()
        => LongRunningTestWorker.Run(() =>
        {
            var observerThreadId = Environment.CurrentManagedThreadId;
            var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var producerThreadId = 0;
            var producer = new Thread(() =>
            {
                producerThreadId = Environment.CurrentManagedThreadId;
                source.SetResult(7);
            })
            { IsBackground = true };
            producer.Start();
            ExceptionDispatchInfo? completionFailure = null;
            try
            {
                Ensure(ObserveTask(source.Task, TimeSpan.FromSeconds(2)) == 7 && producerThreadId != observerThreadId,
                    "RCAA completion must be observed on the independent observer, not inline on its producer");
            }
            catch (Exception error) { completionFailure = ExceptionDispatchInfo.Capture(error); }
            try
            {
                Ensure(producer.Join(TimeSpan.FromSeconds(2)), "the actual source producer must exit");
            }
            catch (Exception error) { completionFailure ??= ExceptionDispatchInfo.Capture(error); }
            completionFailure?.Throw();

            var original = new InvalidOperationException("owned source failure");
            Exception? fault = null;
            try { ObserveTask(Task.FromException(original), TimeSpan.FromSeconds(2)); }
            catch (Exception error) { fault = error; }
            Ensure(ReferenceEquals(fault, original), "the actual fault must survive Task.Wait's AggregateException wrapper");

            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? timeout = null;
            try { ObserveTask(pending.Task, TimeSpan.FromSeconds(2)); }
            catch (Exception error) { timeout = error; }
            finally { pending.TrySetResult(); }
            Ensure(timeout is TimeoutException && pending.Task.IsCompletedSuccessfully,
                "a genuine pending-source timeout must remain failed after the source later completes");
            Console.WriteLine($"EOF_OWNED_TASK_OBSERVATION_V1 observer={observerThreadId}; producer={producerThreadId}; " +
                "value=7; originalFault=True; pendingTimeout=True; laterCompletionNotPromoted=True");
        });

    [Test]
    [Arguments("GetEnumerator")]
    [Arguments("MoveNext")]
    [Arguments("Dispose")]
    public async Task SynchronousOutputFailureMustKeepCauseAndFinishOriginalCleanup(string failureAt)
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-output-sync-failure", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var stream = new FailureStream(failureAt);
        Exception? actual = null;
        try
        {
            await new RpcSessionGeneratedServerBridge(session).PumpOutboundStreamAsync(
                73, 0, stream, new IntCodec(), new RpcMethodDescriptor(101, 202, RpcMethodKind.ServerStreaming, true, false, false, null) { SupportsCancellation = true }, CancellationToken.None);
        }
        catch (Exception error) { actual = error; }
        Ensure(ReferenceEquals(actual, stream.Failure), "normal untimed synchronous failure must retain its exact original cause");
        Ensure(stream.DisposeCount == (failureAt == "GetEnumerator" ? 0 : 1), "only an acquired enumerator disposes, once");
        var source = typeof(CancellationToken).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stream.Token)!;
        Ensure((bool)typeof(CancellationTokenSource).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!,
            "non-abandoned synchronous failure must release its original linked CTS");
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static bool SourceDisposed(CancellationToken token)
    {
        var source = typeof(CancellationToken).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(token)!;
        return (bool)typeof(CancellationTokenSource).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
    }

    private sealed class EofDeadlineStream(
        ManualTimeProvider clock, RpcDeadline deadline, bool lateCleanupFailure, EofPhaseObservations observations)
        : IAsyncEnumerable<int>, IAsyncEnumerator<int>, IGeneratedOutputPhysicalLifetime, IDisposable
    {
        public GeneratedOutputPhysicalTail Tail { get; } = new();
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public CancellationToken Token;
        public int DisposeCount;
        public int DisposeThreadId;
        public int DisposeExited;
        public int Current => throw new InvalidOperationException("EOF cannot read Current");
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        { Token = cancellationToken; observations.Record("get-enumerator"); return this; }
        public ValueTask<bool> MoveNextAsync()
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            observations.Record("move-next-eof:expired=" + deadline.IsExpired(clock));
            return ValueTask.FromResult(false);
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            DisposeThreadId = Environment.CurrentManagedThreadId;
            observations.Record("dispose-entered");
            var signaled = DisposeEntered.TrySetResult();
            observations.Record($"dispose-signal-published:won={signaled}:state={DisposeEntered.Task.Status}");
            try
            {
                Ensure(Release.Wait(TimeSpan.FromSeconds(5)), "synchronous disposal release watchdog exhausted");
                if (lateCleanupFailure) throw new InvalidOperationException("late EOF disposal failure");
                return ValueTask.CompletedTask;
            }
            finally { Volatile.Write(ref DisposeExited, 1); }
        }
        public GeneratedOutputPhysicalTail GetPhysicalTail() => Tail;
        public void Dispose() => Release.Dispose();
    }

    private sealed class EofPhaseObservations
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly ConcurrentQueue<(string Phase, long Timestamp, int Thread)> _events = new();

        public void Record(string phase)
            => _events.Enqueue((phase, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId));

        public override string ToString()
            => string.Join("; ", _events.Select(item =>
                $"{item.Phase}@{Stopwatch.GetElapsedTime(_start, item.Timestamp).TotalMilliseconds:F3}ms/thread{item.Thread}"));
    }

    private sealed class FailureStream(string failureAt) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public Exception Failure { get; } = new InvalidOperationException("original output failure: " + failureAt);
        public CancellationToken Token;
        public int DisposeCount;
        public int Current => 7;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            if (failureAt == "GetEnumerator") throw Failure;
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            if (failureAt == "MoveNext") throw Failure;
            return ValueTask.FromResult(false);
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (failureAt == "Dispose") throw Failure;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IntCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer) => throw new InvalidOperationException("empty stream");
        public int Deserialize(in ReadOnlySequence<byte> buffer) => throw new InvalidOperationException("no input");
    }
}
