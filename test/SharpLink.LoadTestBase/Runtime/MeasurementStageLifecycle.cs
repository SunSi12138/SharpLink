using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.LoadTestBase;

public sealed class MeasurementStageLifecycle
{
    private readonly bool[] _readyWorkers;
    private readonly OperationAdmissionSlot[] _startingOperations;
    private readonly TaskCompletionSource _allReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _readyLock = new();
    private int _readyCount;
    private int _state;

    public MeasurementStageLifecycle(int workerCount, int additionalAdmissionSlots = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(additionalAdmissionSlots);
        _readyWorkers = new bool[workerCount];
        _startingOperations = new OperationAdmissionSlot[checked(workerCount + additionalAdmissionSlots)];
    }

    public static int OperationAdmissionSlotStrideBytes
        => Marshal.SizeOf<OperationAdmissionSlot>();

    public Task AllWorkersReady => _allReady.Task;

    public bool CanStartOperation => Volatile.Read(ref _state) == 1;

    public Task WaitForStartAsync() => _startGate.Task;

    public async Task ReadyAndWaitForStartAsync(int workerIndex)
    {
        lock (_readyLock)
        {
            if ((uint)workerIndex >= (uint)_readyWorkers.Length)
                throw new ArgumentOutOfRangeException(nameof(workerIndex));
            if (_readyWorkers[workerIndex])
                throw new InvalidOperationException($"Worker {workerIndex} reported ready more than once.");

            _readyWorkers[workerIndex] = true;
            _readyCount++;
            if (_readyCount == _readyWorkers.Length)
                _allReady.TrySetResult();
        }

        await _startGate.Task.ConfigureAwait(false);
    }

    public long StartMeasurement()
    {
        if (!_allReady.Task.IsCompletedSuccessfully)
            throw new InvalidOperationException("Measurement cannot start until every worker is ready.");

        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("Measurement has already started.");

        var started = Stopwatch.GetTimestamp();
        _startGate.TrySetResult();
        return started;
    }

    public long StopStartingNewOperations()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 1) != 1)
            throw new InvalidOperationException("Measurement is not accepting new operations.");

        for (var index = 0; index < _startingOperations.Length; index++)
        {
            var spinner = new SpinWait();
            while (Volatile.Read(ref _startingOperations[index].IsStarting) != 0)
                spinner.SpinOnce();
        }
        return Stopwatch.GetTimestamp();
    }

    public bool TryBeginOperationStart(
        int admissionSlot,
        out OperationStartAdmission admission)
    {
        if ((uint)admissionSlot >= (uint)_startingOperations.Length)
            throw new ArgumentOutOfRangeException(nameof(admissionSlot));
        if (Volatile.Read(ref _state) != 1)
        {
            admission = default;
            return false;
        }

        Volatile.Write(ref _startingOperations[admissionSlot].IsStarting, 1);
        if (Volatile.Read(ref _state) != 1)
        {
            Volatile.Write(ref _startingOperations[admissionSlot].IsStarting, 0);
            admission = default;
            return false;
        }

        admission = new OperationStartAdmission(this, admissionSlot);
        return true;
    }

    private void CompleteOperationStart(int admissionSlot)
        => Volatile.Write(ref _startingOperations[admissionSlot].IsStarting, 0);

    public async Task<double> WaitForDrainAsync(Task workersTask, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(workersTask);
        if (Volatile.Read(ref _state) != 2)
            throw new InvalidOperationException("Drain cannot start before measurement has stopped.");

        var started = Stopwatch.GetTimestamp();
        await workersTask.WaitAsync(timeout).ConfigureAwait(false);
        return Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    public readonly struct OperationStartAdmission : IDisposable
    {
        private readonly MeasurementStageLifecycle? _owner;
        private readonly int _admissionSlot;

        internal OperationStartAdmission(MeasurementStageLifecycle owner, int admissionSlot)
        {
            _owner = owner;
            _admissionSlot = admissionSlot;
        }

        public void Dispose() => _owner?.CompleteOperationStart(_admissionSlot);
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct OperationAdmissionSlot
    {
        [FieldOffset(64)]
        public int IsStarting;
    }
}
