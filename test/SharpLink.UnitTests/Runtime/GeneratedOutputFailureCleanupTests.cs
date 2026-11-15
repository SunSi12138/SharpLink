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
        var clock = new ManualTimeProvider();
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), clock);
        var observations = new EofPhaseObservations();
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
                    73, 0, stream, new IntCodec(), false, 101, 202, CancellationToken.None).AsTask();
                observations.Record("logical-valuetask-returned:" + logical.Status);
                returned.TrySetResult(logical);
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
                var logical = await returned.Task.WaitAsync(TimeSpan.FromSeconds(2));
                phase = "logical-deadline";
                observations.Record("observe-logical-start:" + logical.Status);
                Exception? terminal = null;
                try { await logical.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception error) { terminal = error; }
                observations.Record("logical-observed:" + (terminal as SharpLinkException)?.Code + ":" + logical.Status);
                Ensure(terminal is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                    "EOF-selected framework deadline must return before a synchronous disposal prefix exits");
                phase = "dispose-entered";
                observations.Record("observe-dispose-start");
                await stream.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                phase = "held-physical-owner";
                Ensure(stream.DisposeCount == 1 && !stream.Tail.JoinAsync().IsCompleted && !SourceDisposed(stream.Token),
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
                await FinishEofAsync(returned.Task, stream.Tail, invocationThread,
                    stream.Release.Set, stream.Dispose, observations.Record, oracleFailure);
            }
            phase = "physical-exit-owner";
            Ensure(stream.DisposeCount == 1 && SourceDisposed(stream.Token),
                "late disposal success or fault must release the original CTS once, without replacing the deadline");
            observations.Record("physical-exit-cts-disposed:dispose-count=1");
            phase = "complete-pipes";
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
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

    private static async Task FinishEofAsync(
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
        try { logical = await returned.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) { RecordFailure("cleanup-returned", error); }
        if (logical is not null)
        {
            if (!logical.IsCompleted)
                RecordFailure("cleanup-logical-pending", new InvalidOperationException(
                    "The logical invocation is still pending; independent physical cleanup must continue."));
            else
            {
                try { await logical; }
                catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.DeadlineExceeded) { }
                catch (Exception error) { RecordFailure("cleanup-logical", error); }
            }
        }
        try
        {
            await tail.JoinAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
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
    public async Task UnexpectedLogicalFaultMustJoinPhysicalTailAndPreserveOracleFailure()
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
            await FinishEofAsync(returned, tail, invocationThread, release.Set, () =>
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
        await work.Task;
    }

    [Test]
    public async Task PendingLogicalMustNotBlockPhysicalCleanupOrReplaceOracleFailure()
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
        var finishing = FinishEofAsync(returned, tail, invocationThread, () =>
        {
            releaseCalls++;
            release.Set();
        }, () =>
        {
            Ensure(!invocationThread.IsAlive && tail.JoinAsync().IsCompleted && cancellation.DisposeCount == 1,
                "pending logical work cannot prevent independent physical resources from exiting");
            release.Dispose();
            gateDisposals++;
        }, events.Add, oracleFailure);
        Exception? actual = null;
        var logicalPendingAtDelivery = false;
        try
        {
            try { await finishing.WaitAsync(TimeSpan.FromSeconds(5 + 5 + 2)); }
            catch (Exception error) { actual = error; }
            logicalPendingAtDelivery = !logical.Task.IsCompleted;
        }
        finally
        {
            // Release ran synchronously before FinishEofAsync could return its Task.
            // Unblock only this synthetic logical gate, then finish the same cleanup task.
            logical.TrySetResult();
            try { await finishing; }
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
        await work.Task;
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
                73, 0, stream, new IntCodec(), false, 101, 202, CancellationToken.None);
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
            observations.Record("dispose-entered");
            DisposeEntered.TrySetResult();
            Ensure(Release.Wait(TimeSpan.FromSeconds(5)), "synchronous disposal release watchdog exhausted");
            if (lateCleanupFailure) throw new InvalidOperationException("late EOF disposal failure");
            return ValueTask.CompletedTask;
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
