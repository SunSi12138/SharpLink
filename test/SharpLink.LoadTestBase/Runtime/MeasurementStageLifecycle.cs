using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.LoadTestBase;

public sealed class MeasurementStageLifecycle
{
    private readonly bool[] _readyWorkers;
    private readonly TaskCompletionSource _allReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _readyLock = new();
    private int _readyCount;
    private int _state;

    public MeasurementStageLifecycle(int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        _readyWorkers = new bool[workerCount];
    }

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
        return Stopwatch.GetTimestamp();
    }

    public async Task<double> WaitForDrainAsync(Task workersTask, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(workersTask);
        if (Volatile.Read(ref _state) != 2)
            throw new InvalidOperationException("Drain cannot start before measurement has stopped.");

        var started = Stopwatch.GetTimestamp();
        await workersTask.WaitAsync(timeout).ConfigureAwait(false);
        return Stopwatch.GetElapsedTime(started).TotalSeconds;
    }
}
