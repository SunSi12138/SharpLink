using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
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
        using var stream = new EofDeadlineStream(clock, deadline, lateCleanupFailure, observations);
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
                throw;
            }
            finally
            {
                observations.Record("release");
                stream.Release.Set();
                phase = "cleanup-returned";
                var logical = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
                phase = "cleanup-logical";
                try { await logical; }
                catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.DeadlineExceeded) { }
                phase = "physical-join";
                await stream.Tail.JoinAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                observations.Record("physical-join-exited");
                phase = "invocation-thread-join";
                Ensure(invocationThread.Join(TimeSpan.FromSeconds(2)), "logical invocation thread must exit independently of disposal");
                observations.Record("invocation-thread-joined");
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
