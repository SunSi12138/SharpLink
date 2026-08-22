using System.Buffers;
using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Evidence-only workload for #252 used-but-sparse storage. The sequential method deliberately
/// keeps at most one request active so a sparse candidate cannot pass by promoting during warmup.
/// The concurrent method reuses the established 1,024-in-flight / four-worker shape so smaller
/// sparse rings are forced through promotion before measured steady state.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class PendingRequestSparseRingBenchmarks
{
    private const int WindowSize = 256;
    private const int WorkerCount = 4;
    private const int InitialInFlight = WindowSize * WorkerCount;

    private SharpLinkRuntimeContext _context = null!;
    private PendingRequestTable _sequential = null!;
    private PendingRequestTable _concurrent = null!;
    private RpcRequestOperation<int>[] _operations = null!;
    private long[] _requestIds = null!;
    private byte[] _responsePayload = null!;

    [GlobalSetup]
    public void Setup()
    {
        _context = new SharpLinkRuntimeContextBuilder().Build();
        _sequential = CreateTable();
        _concurrent = CreateTable();
        _operations = new RpcRequestOperation<int>[InitialInFlight];
        _requestIds = new long[InitialInFlight];
        _responsePayload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(_responsePayload, 42);

        // Keep the concurrent workload aligned with the established four-window benchmark.
        while (true)
        {
            var operation = _concurrent.Rent<int>(out var requestId);
            Complete(_concurrent, operation, requestId);
            if ((requestId & (WindowSize - 1)) == WindowSize - 1)
                break;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sequential.Dispose();
        _concurrent.Dispose();
        _context.Dispose();
    }

    [Benchmark]
    public int SequentialRegisterAndComplete()
    {
        var operation = _sequential.Rent<int>(out var requestId);
        return Complete(_sequential, operation, requestId);
    }

    [Benchmark]
    public int RegisterAndCompleteAcrossFourWindows()
    {
        for (var index = 0; index < InitialInFlight; index++)
            _operations[index] = _concurrent.Rent<int>(out _requestIds[index]);

        Parallel.For(0, WorkerCount, worker =>
        {
            var start = worker * WindowSize;
            var end = start + WindowSize;
            for (var index = start; index < end; index++)
            {
                Complete(_concurrent, _operations[index], _requestIds[index]);
                var replacement = _concurrent.Rent<int>(out var replacementId);
                Complete(_concurrent, replacement, replacementId);
            }
        });

        var remaining = _concurrent.ActiveCount;
        if (remaining != 0)
            throw new InvalidOperationException($"Benchmark leaked {remaining} pending calls.");
        return remaining;
    }

    private PendingRequestTable CreateTable()
        => new(
            65_536,
            _context.Codecs,
            BenchmarkPendingCallOwner.Instance,
            TimeProvider.System);

    private int Complete(PendingRequestTable table, RpcRequestOperation<int> operation, long requestId)
    {
        var payload = new ReadOnlySequence<byte>(_responsePayload);
        if (!table.Dispatch(requestId, ref payload))
            throw new InvalidOperationException("Pending request dispatch did not find the registered call.");
        return operation.AsValueTask().GetAwaiter().GetResult();
    }
}
