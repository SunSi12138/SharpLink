using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel("route-footprint")]
public sealed class StreamManagerPackedFlagsTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    public void LifecycleFlagsMustRemainIndependentOfAcquisitionAndDetach(int order)
    {
        var entry = new EntryProbe();
        Require(entry.Acquire(), "initial acquisition must succeed");
        Action terminal = () => Require(entry.PublishTerminal(), "terminal has one winner");
        Action retire = () => Require(entry.ClaimRetirement(), "retirement has one winner");
        Action peer = entry.MarkPeerTerminal;
        Action[][] orders =
        [
            [terminal, retire, peer], [terminal, peer, retire],
            [retire, terminal, peer], [retire, peer, terminal],
            [peer, terminal, retire], [peer, retire, terminal]
        ];
        foreach (var action in orders[order]) action();
        Require(entry.PeerTerminal() && entry.State.HasActiveDispatches && !entry.State.IsDetached,
            "lifecycle flags must not counterfeit dispatch drain or detach");
        Require(!entry.PublishTerminal() && !entry.ClaimRetirement(), "claims must be idempotent");
        Require(entry.Acquire(), "retirement claim is not dispatch Close");
        entry.Release();
        entry.State.Close();
        Require(!entry.Acquire(), "Close excludes new acquisitions");
        entry.Detach();
        Require(entry.Dispatcher.Drained == 0, "detach must not drain the outstanding acquisition");
        entry.Release();
        Require(entry.PeerTerminal() && !entry.State.HasActiveDispatches && entry.State.IsDetached &&
                entry.Dispatcher.Drained == 1, "last release preserves flags and notifies exactly once");
        entry.Detach();
        Require(entry.Dispatcher.Drained == 1 && !entry.PublishTerminal() && !entry.ClaimRetirement(),
            "duplicate terminal/retirement/detach cannot claim another owner");
    }

    [Test]
    public void ConcurrentFlagsMustNotLoseAcquisitionsOrMultipleClaimWinners()
    {
        for (var round = 0; round < 64; round++)
        {
            var entry = new EntryProbe();
            var terminalWinners = 0;
            var retirementWinners = 0;
            Parallel.For(0, 32, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
            {
                Require(entry.Acquire(), "open route remains acquirable during flag publication");
                if (entry.PublishTerminal()) Interlocked.Increment(ref terminalWinners);
                if (entry.ClaimRetirement()) Interlocked.Increment(ref retirementWinners);
                entry.MarkPeerTerminal();
                entry.Release();
            });
            Require(terminalWinners == 1 && retirementWinners == 1 && entry.PeerTerminal() &&
                    !entry.State.HasActiveDispatches, "racing flags must preserve the complete count and one-shot claims");
            entry.State.Close();
            entry.Detach();
            Require(entry.Dispatcher.Drained == 1, "final detach owns exactly one pool notification");
        }
    }

    [Test]
    public void CleanupPinMustSurviveAllColdFlagsAndClearWithoutResettingThem()
    {
        var entry = new EntryProbe();
        Require(entry.Acquire(), "pin fixture must acquire DATA");
        entry.State.Close();
        var callbacks = 0;
        entry.RunWhenDrained(() =>
        {
            callbacks++;
            Require(entry.State.HasActiveDispatches, "cleanup pin remains active after the DATA count reaches zero");
            Require(entry.PublishTerminal() && entry.ClaimRetirement(), "cleanup may set both independent one-shot flags");
            entry.MarkPeerTerminal();
            entry.Detach();
            Require(entry.Dispatcher.Drained == 0, "pool notification waits until cleanup pin release");
        });
        entry.Release();
        Require(callbacks == 1 && entry.Dispatcher.Drained == 1 && !entry.State.HasActiveDispatches &&
                entry.PeerTerminal() && !entry.PublishTerminal() && !entry.ClaimRetirement(),
            "cleanup-pin removal must preserve every published lifecycle flag");
    }

    [Test]
    public void PackedFlagsMustPreserveTheOriginalAcquisitionCountLimit()
    {
        var entry = new EntryProbe();
        Require(entry.PublishTerminal() && entry.ClaimRetirement(), "initialize independent flags");
        entry.MarkPeerTerminal();
        var before = entry.ReadState();
        entry.WriteState(before | int.MaxValue);
        Require(!entry.Acquire(), "saturated count must reject acquisition rather than carry into flags");
        entry.Release();
        Require(entry.Acquire() && !entry.Acquire(), "one release must restore exactly one slot at saturation");
        Require(entry.PeerTerminal() && !entry.PublishTerminal() && !entry.ClaimRetirement(),
            "count saturation cannot reset lifecycle flags");
        entry.WriteState(entry.ReadState() & ~((long)int.MaxValue));
        entry.Detach();
        Require(entry.Dispatcher.Drained == 1, "boundary fixture must detach once");
    }

    [Test]
    [Arguments((ushort)0)]
    [Arguments((ushort)7)]
    public void RouteLifecycleAllocationProbe(ushort streamId)
    {
        const int repetitions = 3;
        const int iterations = 10000;
        const long requestId = 9751;
        var manager = new StreamManager();
        var dispatcher = new StatelessDispatcher();
        for (var i = 0; i < 2048; i++)
        {
            manager.Register(requestId, streamId, dispatcher);
            manager.Unregister(requestId, streamId);
        }
        var rows = new string[repetitions + 1];
        rows[0] = "repeat,bytes_per_route,nanoseconds_per_route";
        for (var repeat = 0; repeat < repetitions; repeat++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++)
            {
                manager.Register(requestId, streamId, dispatcher);
                manager.Unregister(requestId, streamId);
            }
            var elapsed = Stopwatch.GetElapsedTime(started);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Require(allocated > 0 && manager.ActiveStreamCount == 0,
                "probe must measure actual completed route lifecycles");
            rows[repeat + 1] = string.Create(CultureInfo.InvariantCulture,
                $"{repeat},{allocated / (double)iterations:F6},{elapsed.TotalNanoseconds / iterations:F6}");
        }
        // Opt-in artifact output is used by the separate baseline/candidate preflight;
        // ordinary unit runs do not write files or assert a machine-dependent timing limit.
        var directory = Environment.GetEnvironmentVariable("SHARPLINK_ROUTE_FOOTPRINT_DIRECTORY");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, $"route-{streamId}.csv"), rows);
        }
        Console.WriteLine($"Route footprint, pointer size {IntPtr.Size}, stream {streamId}: {string.Join("; ", rows)}");
        manager.CompleteAll(exception: null);
    }

    private sealed class EntryProbe
    {
        internal readonly CapturingDispatcher Dispatcher = new();
        internal readonly IStreamDispatchState State;
        internal readonly Func<bool> Acquire;
        internal readonly Action Release;
        internal readonly Action Detach;
        internal readonly Func<bool> PublishTerminal;
        internal readonly Func<bool> ClaimRetirement;
        internal readonly Action MarkPeerTerminal;
        internal readonly Func<bool> PeerTerminal;
        internal readonly Action<Action> RunWhenDrained;
        private readonly FieldInfo _stateField;

        internal EntryProbe()
        {
            var type = typeof(StreamManager).GetNestedType("DispatcherEntry", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("DispatcherEntry type was not found.");
            var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                [typeof(IStreamDispatcher), typeof(StreamFlowController.ResolvedReceiveCreditLease)], null)
                ?? throw new InvalidOperationException("DispatcherEntry constructor was not found.");
            State = (IStreamDispatchState)constructor.Invoke(
                [Dispatcher, default(StreamFlowController.ResolvedReceiveCreditLease)]);
            T Bind<T>(string method) where T : Delegate
                => (type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"Missing method {method}."))
                    .CreateDelegate<T>(State);
            Acquire = Bind<Func<bool>>("TryAcquire");
            Release = Bind<Action>("Release");
            Detach = Bind<Action>("Detach");
            PublishTerminal = Bind<Func<bool>>("TryPublishReceiveTerminal");
            ClaimRetirement = Bind<Func<bool>>("TryClaimRetirement");
            MarkPeerTerminal = Bind<Action>("MarkPeerTerminalReceived");
            PeerTerminal = Bind<Func<bool>>("get_PeerTerminalReceived");
            RunWhenDrained = Bind<Action<Action>>("RunWhenDispatchesDrained");
            _stateField = type.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Entry atomic state was not found.");
        }

        internal long ReadState() => (long)_stateField.GetValue(State)!;
        internal void WriteState(long value) => _stateField.SetValue(State, value);
    }

    private sealed class CapturingDispatcher : IStreamDispatcher, IStreamDispatchLease
    {
        private int _drained;
        internal int Drained => Volatile.Read(ref _drained);
        public void BindDispatchState(IStreamDispatchState state) => _ = state;
        public void OnDispatchesDrained() => Interlocked.Increment(ref _drained);
        public ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount)
            => ValueTask.CompletedTask;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
    }

    private sealed class StatelessDispatcher : IStreamDispatcher
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
