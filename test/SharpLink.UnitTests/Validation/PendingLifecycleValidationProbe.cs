using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Validation;

// These process workers are deliberately explicit: the Python driver supplies isolation,
// watchdogs and the actual characterization/regression assertions. Never run the +1
// worker directly without an external process timeout.
[Explicit]
public sealed class PendingLifecycleValidationProbe
{
    [Test]
    public async Task Run()
    {
        var scenario = Environment.GetEnvironmentVariable("SHARPLINK_VALIDATION_SCENARIO")
            ?? throw new InvalidOperationException("Use eng/validate-pending-lifecycle.py.");
        if (scenario.StartsWith("deadline-", StringComparison.Ordinal))
            await DeadlineReuse(scenario[9..]);
        else if (scenario is "metric-plus" or "metric-minus" or "metric-control" or "no-listener")
            await Metrics(scenario);
        else
            throw new InvalidOperationException($"Unknown scenario: {scenario}");
    }

    private static async Task DeadlineReuse(string completionPath)
    {
        using var clock = new ScanClock();
        var owner = new RecordingOwner();
        using var table = PendingRequestTableTestFixture.Create(1, owner: owner, timeProvider: clock);
        using var cancellation = new CancellationTokenSource();
        var oldDeadline = RpcDeadline.Create(TimeSpan.FromSeconds(1), clock);
        var first = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, oldDeadline,
            cancellation.Token, out var firstId).AsValueTask().AsTask();
        var original = Slot(table);
        clock.Now = TimeSpan.FromSeconds(2).Ticks;
        Exception? scanFailure = null;
        var scan = new Thread(() =>
        {
            try
            {
                clock.ArmForCurrentThread();
                typeof(PendingRequestTable).GetMethod("ScanExpiredDeadlines",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(table, null);
            }
            catch (Exception exception)
            {
                scanFailure = exception;
            }
        })
        { IsBackground = true, Name = "deadline-reuse-validation" };
        scan.Start();
        try
        {
            Require(clock.Entered.Wait(TimeSpan.FromSeconds(10)), "scan did not read the old deadline");
            // The old deadline's struct receiver has already been read by the scan.
            // A is expired, so every competing entry point authoritatively selects
            // DeadlineExceeded. Record the entry point separately from that reason.
            switch (completionPath)
            {
                case "response":
                    Respond(table, firstId);
                    break;
                case "cancel":
                    cancellation.Cancel();
                    break;
                case "disconnect":
                    table.FailAllPendingRequests(new SharpLinkException(
                        SharpLinkErrorCode.ConnectionClosed, "validation disconnect"));
                    break;
                default:
                    throw new InvalidOperationException(completionPath);
            }
            var firstError = await Observe(first);
            var oldReason = owner.LastReason.ToString();
            var newDeadline = RpcDeadline.Create(TimeSpan.FromHours(1), clock);
            var second = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, newDeadline,
                CancellationToken.None, out var secondId).AsValueTask().AsTask();
            var sameReference = ReferenceEquals(original, Slot(table));
            var futureBefore = !newDeadline.IsExpired(clock);
            Require(sameReference, "fixture failed to reuse the exact PendingCall object");
            Require(firstId != secondId && futureBefore, "new request identity/deadline invalid");
            clock.Release.Set();
            Require(scan.Join(TimeSpan.FromSeconds(10)), "scan failed to finish");
            if (scanFailure is not null)
                throw new InvalidOperationException("scan invocation failed", scanFailure);
            var pending = table.Contains(secondId) && !second.IsCompleted;
            var futureAfter = !newDeadline.IsExpired(clock);
            var reasonAfterScan = owner.LastReason.ToString();
            if (table.Contains(secondId))
                Respond(table, secondId);
            var secondError = await Observe(second);
            Write(new
            {
                phase = "complete",
                scenario = $"deadline-{completionPath}",
                sameReference,
                firstId,
                secondId,
                futureBefore,
                futureAfter,
                oldReason,
                firstError,
                reasonAfterScan,
                secondError,
                invariant = pending,
                active = table.ActiveCount,
                count = table.Count
            });
        }
        finally
        {
            clock.Release.Set();
            Require(scan.Join(TimeSpan.FromSeconds(10)), "scan cleanup timed out");
        }
    }

    private static async Task Metrics(string scenario)
    {
        var owner = new RecordingOwner();
        var table = PendingRequestTableTestFixture.Create(1, owner: owner);
        var hits = 0;
        var positiveHits = 0;
        var negativeHits = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "SharpLink" && instrument.Name == "sharplink.requests.pending")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
        {
            Interlocked.Increment(ref hits);
            if (measurement > 0) Interlocked.Increment(ref positiveHits);
            if (measurement < 0) Interlocked.Increment(ref negativeHits);
            if ((scenario == "metric-plus" && measurement == 1) ||
                (scenario == "metric-minus" && measurement == -1))
                throw new ProbeCallbackException();
        });
        if (scenario != "no-listener")
            listener.Start();

        RpcRequestOperation<int>? operation = null;
        Exception? escaped = null;
        long id = 0;
        try
        {
            operation = table.Rent<int>(out id);
            if (scenario != "metric-plus")
                Respond(table, id);
        }
        catch (Exception exception)
        {
            escaped = exception;
        }
        listener.Dispose();
        var countBefore = table.Count;
        var activeBefore = table.ActiveCount;
        if (scenario == "metric-plus")
        {
            var call = table.Count == 0 ? null : Slot(table);
            var registered = call is null ? -1 : (int)call.GetType().GetField("_registered",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(call)!;
            // This file is written atomically BEFORE Dispose. On a broken baseline the outer
            // watchdog still uses it to distinguish the known published/unregistered hang.
            Write(new
            {
                phase = "dispose-enter",
                scenario,
                countBefore,
                activeBefore,
                registered,
                hits,
                positiveHits,
                negativeHits,
                escaped = escaped?.GetType().Name,
                ownerRegistered = owner.Registered
            });
            table.Dispose();
            var disposeOperationError = operation is null
                ? null
                : await Observe(operation.AsValueTask().AsTask());
            var active = table.ActiveCount;
            var count = table.Count;
            Write(new
            {
                phase = "complete",
                scenario,
                countBefore,
                activeBefore,
                registered,
                hits,
                positiveHits,
                negativeHits,
                escaped = escaped?.GetType().Name,
                ownerRegistered = owner.Registered,
                operationError = disposeOperationError,
                active,
                count,
                invariant = escaped is null && registered == 1 && owner.Registered == 1 &&
                    disposeOperationError == SharpLinkErrorCode.ConnectionClosed.ToString() &&
                    active == 0 && count == 0
            });
            return;
        }

        string? operationError = null;
        if (operation is not null)
            operationError = await Observe(operation.AsValueTask().AsTask());
        var ownerRegisteredBeforeNext = owner.Registered;
        var nextSucceeded = false;
        string? nextError = null;
        try
        {
            var next = table.Rent<int>(out var nextId).AsValueTask().AsTask();
            Respond(table, nextId);
            nextError = await Observe(next);
            nextSucceeded = nextError is null;
        }
        catch (Exception exception)
        {
            nextError = Error(exception);
        }
        table.Dispose();
        Write(new
        {
            phase = "complete",
            scenario,
            countBefore,
            activeBefore,
            active = table.ActiveCount,
            count = table.Count,
            hits,
            positiveHits,
            negativeHits,
            escaped = escaped?.GetType().Name,
            operationError,
            ownerRegisteredBeforeNext,
            nextSucceeded,
            nextError,
            invariant = escaped is null && operationError is null &&
                ownerRegisteredBeforeNext == 1 && nextSucceeded &&
                table.ActiveCount == 0 && table.Count == 0
        });
    }

    private static object Slot(PendingRequestTable table)
    {
        var slots = (Array)typeof(PendingRequestTable).GetField("_slots",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;
        return slots.GetValue(0) ?? throw new InvalidOperationException("slot is empty");
    }

    private static void Respond(PendingRequestTable table, long id)
    {
        var payload = new ReadOnlySequence<byte>(new byte[sizeof(int)]);
        Require(table.Dispatch(id, ref payload), "response did not reach the pending call");
    }

    private static async Task<string?> Observe(Task<int> task)
    {
        try { _ = await task; return null; }
        catch (Exception exception) { return Error(exception); }
    }

    private static string Error(Exception exception)
        => exception is SharpLinkException rpc ? rpc.Code.ToString() : exception.GetType().Name;

    internal static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Write<T>(T value)
    {
        var path = Environment.GetEnvironmentVariable("SHARPLINK_VALIDATION_OUTPUT")
            ?? throw new InvalidOperationException("The external driver must provide an output path.");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private sealed class ProbeCallbackException : Exception;

    private sealed class RecordingOwner : IPendingCallOwner
    {
        internal int Registered { get; private set; }
        internal PendingCallCompletionReason LastReason { get; private set; }
        public void OnPendingCallRegistered() => Registered++;
        public void OnPendingCallCompleted(in PendingCallCompletion completion) => LastReason = completion.Reason;
        public void OnProducerCancellationCallbackFailed(Exception exception) => throw exception;
    }

    private sealed class ScanClock : TimeProvider, IDisposable
    {
        private long _now;
        private int _scanThread;
        private int _armed;
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal long Now { set => Interlocked.Exchange(ref _now, value); }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void ArmForCurrentThread()
        {
            _scanThread = Environment.CurrentManagedThreadId;
            Volatile.Write(ref _armed, 1);
        }
        public override long GetTimestamp()
        {
            if (Environment.CurrentManagedThreadId == _scanThread && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Entered.Set();
                Require(Release.Wait(TimeSpan.FromSeconds(10)), "scan gate release timed out");
            }
            return Interlocked.Read(ref _now);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new InertTimer();
        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
