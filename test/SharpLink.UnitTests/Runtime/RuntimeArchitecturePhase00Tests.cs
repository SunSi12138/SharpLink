using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public sealed class RuntimeArchitecturePhase00Tests
{
    private const int RaceSeed = 68002026;
    private const int RaceRepetitions = 100;
    private static readonly ReadOnlySequence<byte> SResponsePayload = new(new byte[sizeof(int)]);

    [Test]
    public void ManualTimeProviderShouldAdvanceMonotonicAndUtcTimeAndRunTimersDeterministically()
    {
        var start = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(start);
        var callbackTimestamps = new List<long>();
        using var timer = timeProvider.CreateTimer(
            _ => callbackTimestamps.Add(timeProvider.GetTimestamp()),
            state: null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(2));

        timeProvider.Advance(TimeSpan.FromSeconds(4));
        Ensure(callbackTimestamps.Count == 0, "the timer must not fire before its monotonic deadline");

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        Ensure(callbackTimestamps.SequenceEqual([
                TimeSpan.FromSeconds(5).Ticks,
                TimeSpan.FromSeconds(7).Ticks,
                TimeSpan.FromSeconds(9).Ticks]),
            "periodic callbacks must fire at their exact deterministic timestamps");
        Ensure(timeProvider.GetTimestamp() == TimeSpan.FromSeconds(9).Ticks,
            "the monotonic timestamp must advance to the requested target");
        Ensure(timeProvider.GetUtcNow() == start.AddSeconds(9),
            "UTC and monotonic time must advance together in the test fixture");

        timer.Dispose();
        timeProvider.Advance(TimeSpan.FromHours(1));
        Ensure(callbackTimestamps.Count == 3, "a disposed timer must not escape into later test phases");
    }

    [Test]
    public async Task SessionFaultShutdownAndDisposeRaceShouldDisposeItsTransportExactlyOnce()
    {
        var random = new Random(RaceSeed);
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        for (var iteration = 0; iteration < RaceRepetitions; iteration++)
        {
            var transport = new CountingTransportConnection($"phase00-session-{iteration}");
            var session = new RpcSession(
                transport,
                RpcSessionTestFixture.ClientOptions(context));
            var runtimeContext = session.RuntimeContext;
            var streamManager = session.StreamManager;
            var input = session.Input;
            using var start = new ManualResetEventSlim();

            Func<Task>[] racers =
            [
                () =>
                {
                    session.NotifyDisconnected(new IOException($"fault-{iteration}"));
                    return Task.CompletedTask;
                },
                () =>
                {
                    session.BeginShutdown();
                    return Task.CompletedTask;
                },
                () => session.DisposeAsync().AsTask()
            ];
            Shuffle(racers, random);
            var tasks = new Task[racers.Length];
            for (var index = 0; index < racers.Length; index++)
            {
                var racer = racers[index];
                tasks[index] = Task.Run(async () =>
                {
                    start.Wait();
                    await racer().ConfigureAwait(false);
                });
            }

            start.Set();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            Ensure(transport.DisposeCount == 1,
                $"Fault/BeginShutdown/DisposeAsync must have one transport owner; seed={RaceSeed}, iteration={iteration}");
            Ensure(ReferenceEquals(runtimeContext, session.RuntimeContext),
                "the bound RuntimeContext reference must remain stable through the terminal race");
            Ensure(ReferenceEquals(streamManager, session.StreamManager),
                "the StreamManager reference must remain stable through the terminal race");
            Ensure(ReferenceEquals(input, session.Input),
                "the transport input reference must remain stable through the terminal race");
        }
    }

    [Test]
    public async Task FiveWayPendingTerminalRaceShouldChooseOneWinnerAndBalanceEveryCounter()
    {
        var random = new Random(RaceSeed);
        var owner = new RecordingPendingCallOwner();
        using var table = new PendingRequestTable(
            1,
            PendingRequestTableTestFixture.Codecs,
            owner,
            TimeProvider.System);

        for (var iteration = 0; iteration < RaceRepetitions; iteration++)
        {
            var operation = table.Rent<int>(out var requestId);
            using var start = new ManualResetEventSlim();
            Func<bool>[] racers =
            [
                () =>
                {
                    var payload = SResponsePayload;
                    return table.Dispatch(requestId, ref payload);
                },
                () => table.TryComplete(requestId, PendingCallCompletionReason.UserCancellation),
                () => table.TryComplete(requestId, PendingCallCompletionReason.DeadlineExceeded),
                () => table.TryComplete(
                    requestId,
                    PendingCallCompletionReason.ConnectionClosed,
                    new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "phase00 disconnect")),
                () => table.TryComplete(
                    requestId,
                    PendingCallCompletionReason.GoAway,
                    new SharpLinkException(SharpLinkErrorCode.Unavailable, "phase00 go-away"))
            ];
            Shuffle(racers, random);
            var tasks = new Task<bool>[racers.Length];
            for (var index = 0; index < racers.Length; index++)
            {
                var racer = racers[index];
                tasks[index] = Task.Run(() =>
                {
                    start.Wait();
                    return racer();
                });
            }

            start.Set();
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(results.Count(static result => result) == 1,
                $"five pending terminal paths must have one winner; seed={RaceSeed}, iteration={iteration}");

            var failure = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
            var exposesTerminalReason = failure switch
            {
                null => true,
                OperationCanceledException => true,
                SharpLinkException
                {
                    Code: SharpLinkErrorCode.DeadlineExceeded or
                            SharpLinkErrorCode.ConnectionClosed or
                            SharpLinkErrorCode.Unavailable
                } => true,
                _ => false
            };
            Ensure(exposesTerminalReason,
                $"the operation must expose the selected terminal reason; seed={RaceSeed}, iteration={iteration}");
            Ensure(table.Count == 0,
                $"the terminal winner must release the pending slot; seed={RaceSeed}, iteration={iteration}");
            Ensure(owner.ActiveCount == 0 && owner.MinimumActiveCount >= 0,
                $"pending ownership must balance without underflow; seed={RaceSeed}, iteration={iteration}");
        }

        Ensure(owner.RegisteredCount == RaceRepetitions, "every pending call must publish one registration");
        Ensure(owner.CompletedCount == RaceRepetitions, "every pending call must publish one terminal completion");
    }

    [Test]
    public async Task PendingTerminalMatrixShouldReleaseThePhysicalOwnerExactlyOnce()
    {
        foreach (var terminal in Enum.GetValues<PendingTerminal>())
        {
            var owner = new RecordingPendingCallOwner();
            using var table = new PendingRequestTable(
                1,
                PendingRequestTableTestFixture.Codecs,
                owner,
                TimeProvider.System);
            var operation = table.Rent<int>(out var requestId);

            var won = CompletePendingTerminal(table, requestId, terminal);

            Ensure(won, $"{terminal}: selected terminal cause must own its pending slot");
            Ensure(!table.TryComplete(
                    requestId,
                    PendingCallCompletionReason.ConnectionClosed,
                    new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "late terminal")),
                $"{terminal}: a losing terminal cause must not complete the owner twice");

            _ = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
            Ensure(table.Count == 0,
                $"{terminal}: the terminal owner must remove the pending slot");
            Ensure(owner.RegisteredCount == 1 && owner.CompletedCount == 1 &&
                   owner.ActiveCount == 0 && owner.MinimumActiveCount >= 0,
                $"{terminal}: physical ownership must register and release exactly once");
        }
    }

    [Test]
    public async Task PendingTerminalRacesShouldLeaveExactlyOnePhysicalOwner()
    {
        foreach (var terminal in Enum.GetValues<PendingTerminal>())
        {
            var owner = new RecordingPendingCallOwner();
            using var table = new PendingRequestTable(
                1,
                PendingRequestTableTestFixture.Codecs,
                owner,
                TimeProvider.System);
            var operation = table.Rent<int>(out var requestId);
            var competingTerminal = terminal == PendingTerminal.GoAway
                ? PendingTerminal.ConnectionFailure
                : PendingTerminal.GoAway;
            using var start = new ManualResetEventSlim();
            var racers = new Task<bool>[]
            {
                Task.Run(() =>
                {
                    start.Wait();
                    return CompletePendingTerminal(table, requestId, terminal);
                }),
                Task.Run(() =>
                {
                    start.Wait();
                    return CompletePendingTerminal(table, requestId, competingTerminal);
                })
            };

            start.Set();
            var results = await Task.WhenAll(racers).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(results.Count(static result => result) == 1,
                $"{terminal}: competing terminal paths must have exactly one pending-slot winner");
            _ = await CaptureExceptionAsync(operation.AsValueTask().AsTask());
            Ensure(table.Count == 0,
                $"{terminal}: a terminal race must remove the pending slot");
            Ensure(owner.RegisteredCount == 1 && owner.CompletedCount == 1 &&
                   owner.ActiveCount == 0 && owner.MinimumActiveCount >= 0,
                $"{terminal}: a terminal race must release the physical owner exactly once");
        }
    }

    private static bool CompletePendingTerminal(
        PendingRequestTable table,
        long requestId,
        PendingTerminal terminal)
        => terminal switch
        {
            PendingTerminal.Response => DispatchResponse(table, requestId),
            PendingTerminal.RemoteError => table.DispatchError(
                requestId,
                new SharpLinkException(SharpLinkErrorCode.RemoteError, "phase13 remote error")),
            PendingTerminal.UserCancellation => table.TryComplete(
                requestId,
                PendingCallCompletionReason.UserCancellation),
            PendingTerminal.Deadline => table.TryComplete(
                requestId,
                PendingCallCompletionReason.DeadlineExceeded),
            PendingTerminal.ConnectionFailure => table.TryComplete(
                requestId,
                PendingCallCompletionReason.ConnectionClosed,
                new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "phase13 disconnect")),
            PendingTerminal.GoAway => table.TryComplete(
                requestId,
                PendingCallCompletionReason.GoAway,
                new SharpLinkException(SharpLinkErrorCode.Unavailable, "phase13 go-away")),
            PendingTerminal.ConsumerAbandonment => table.TryComplete(
                requestId,
                PendingCallCompletionReason.ConsumerAbandoned),
            PendingTerminal.SendFailure => table.TryComplete(
                requestId,
                PendingCallCompletionReason.SendFailure,
                new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "phase13 send failure")),
            _ => throw new ArgumentOutOfRangeException(nameof(terminal), terminal, null)
        };

    private static bool DispatchResponse(PendingRequestTable table, long requestId)
    {
        var payload = SResponsePayload;
        return table.Dispatch(requestId, ref payload);
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Shuffle<T>(T[] values, Random random)
    {
        for (var index = values.Length - 1; index > 0; index--)
        {
            var selected = random.Next(index + 1);
            (values[index], values[selected]) = (values[selected], values[index]);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private enum PendingTerminal
    {
        Response,
        RemoteError,
        UserCancellation,
        Deadline,
        ConnectionFailure,
        GoAway,
        ConsumerAbandonment,
        SendFailure
    }

    private sealed class RecordingPendingCallOwner : IPendingCallOwner
    {
        private int _activeCount;
        private int _minimumActiveCount;
        private int _registeredCount;
        private int _completedCount;

        public int ActiveCount => Volatile.Read(ref _activeCount);
        public int MinimumActiveCount => Volatile.Read(ref _minimumActiveCount);
        public int RegisteredCount => Volatile.Read(ref _registeredCount);
        public int CompletedCount => Volatile.Read(ref _completedCount);

        public void OnPendingCallRegistered()
        {
            Interlocked.Increment(ref _registeredCount);
            Interlocked.Increment(ref _activeCount);
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            _ = completion;
            Interlocked.Increment(ref _completedCount);
            var remaining = Interlocked.Decrement(ref _activeCount);
            while (true)
            {
                var minimum = Volatile.Read(ref _minimumActiveCount);
                if (remaining >= minimum ||
                    Interlocked.CompareExchange(ref _minimumActiveCount, remaining, minimum) == minimum)
                {
                    return;
                }
            }
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
            => throw new Exception("the phase-00 unary race must not publish producer cancellation failures", exception);
    }

    private sealed class CountingTransportConnection(string id) : ITransportConnection
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private int _disposeCount;

        public string Id { get; } = id;
        public PipeReader Input => _input.Reader;
        public PipeWriter Output => _output.Writer;
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
        }
    }
}
