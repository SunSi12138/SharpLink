using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Exercises the pending-call table with 1,024 requests in flight while registrations and
/// completions run concurrently. Each worker starts from a different 256-request window, completes an
/// older request, then immediately rents/completes a replacement from the advancing request-ID stream.
/// This provides a multi-window concurrent workload alongside the single-thread
/// <see cref="RuntimeHotPathBenchmarks.PendingRegisterAndComplete"/> benchmark.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class PendingRequestConcurrencyBenchmarks
{
    private const int WindowSize = 256;
    private const int WorkerCount = 4;
    private const int InitialInFlight = WindowSize * WorkerCount;

    private SharpLinkRuntimeContext _context = null!;
    private PendingRequestTable _pending = null!;
    private RpcRequestOperation<int>[] _operations = null!;
    private long[] _requestIds = null!;
    private byte[] _responsePayload = null!;

    [GlobalSetup]
    public void Setup()
    {
        _context = new SharpLinkRuntimeContextBuilder().Build();
        _pending = new PendingRequestTable(
            65_536,
            _context.Codecs,
            BenchmarkPendingCallOwner.Instance,
            TimeProvider.System);
        _operations = new RpcRequestOperation<int>[InitialInFlight];
        _requestIds = new long[InitialInFlight];
        _responsePayload = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(_responsePayload, 42);

        // Align the first benchmark window to a 256-request boundary. The benchmark body advances
        // by a multiple of WindowSize, so later invocations remain aligned as request IDs wrap.
        while (true)
        {
            var operation = _pending.Rent<int>(out var requestId);
            Complete(operation, requestId);
            if ((requestId & (WindowSize - 1)) == WindowSize - 1)
                break;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pending.Dispose();
        _context.Dispose();
    }

    [Benchmark]
    public int RegisterAndCompleteAcrossFourWindows()
    {
        for (var index = 0; index < InitialInFlight; index++)
            _operations[index] = _pending.Rent<int>(out _requestIds[index]);

        if ((_requestIds[0] & (WindowSize - 1)) != 0)
            throw new InvalidOperationException("Benchmark request window is not aligned.");

        Parallel.For(0, WorkerCount, worker =>
        {
            var start = worker * WindowSize;
            var end = start + WindowSize;
            for (var index = start; index < end; index++)
            {
                // Interleave completion of 1,024 older requests with replacement registrations so
                // the table stays substantially occupied while four workers mutate it concurrently.
                Complete(_operations[index], _requestIds[index]);
                var replacement = _pending.Rent<int>(out var replacementId);
                Complete(replacement, replacementId);
            }
        });

        var remaining = _pending.ActiveCount;
        if (remaining != 0)
            throw new InvalidOperationException($"Benchmark leaked {remaining} pending calls.");
        return remaining;
    }

    private int Complete(RpcRequestOperation<int> operation, long requestId)
    {
        var payload = new ReadOnlySequence<byte>(_responsePayload);
        _pending.Dispatch(requestId, ref payload);
        return operation.AsValueTask().GetAwaiter().GetResult();
    }
}
