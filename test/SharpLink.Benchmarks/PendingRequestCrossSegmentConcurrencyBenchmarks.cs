using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

/// <summary>
/// Exercises the pending-call table with more than one segment in flight while registrations and
/// completions run concurrently. Each worker starts from a different 256-slot segment, completes an
/// older request, then immediately rents/completes a replacement from the advancing request-ID stream.
/// This intentionally creates a cross-segment access pattern instead of the single-thread same-segment
/// pattern covered by <see cref="RuntimeHotPathBenchmarks.PendingRegisterAndComplete"/>.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class PendingRequestCrossSegmentConcurrencyBenchmarks
{
    private const int SegmentSize = 256;
    private const int WorkerCount = 4;
    private const int InitialInFlight = SegmentSize * WorkerCount;

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

        // Align the first benchmark window to a segment boundary so each worker owns exactly one
        // old segment. The benchmark body advances by a multiple of SegmentSize, so subsequent
        // invocations stay aligned even as request IDs wrap.
        while (true)
        {
            var operation = _pending.Rent<int>(out var requestId);
            Complete(operation, requestId);
            if ((requestId & (SegmentSize - 1)) == SegmentSize - 1)
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
    public int RegisterAndCompleteAcrossFourSegments()
    {
        for (var index = 0; index < InitialInFlight; index++)
            _operations[index] = _pending.Rent<int>(out _requestIds[index]);

        if ((_requestIds[0] & (SegmentSize - 1)) != 0)
            throw new InvalidOperationException("Benchmark request window is not segment-aligned.");

        Parallel.For(0, WorkerCount, worker =>
        {
            var start = worker * SegmentSize;
            var end = start + SegmentSize;
            for (var index = start; index < end; index++)
            {
                // Complete from one of four older segments while all workers concurrently advance
                // the registration stream into newer segments. This keeps >256 requests in flight
                // for most of the batch and interleaves old-segment completion with new registration.
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
