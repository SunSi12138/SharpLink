using System;
using System.Buffers;
using System.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10)]
[BenchmarkCategory("PendingRequestTable", "Saturation")]
public class PendingRequestSaturationBenchmarks
{
    private SharpLinkRuntimeContext _context = null!;
    private PendingRequestTable _empty = null!;
    private PendingRequestTable _halfFull = null!;
    private PendingRequestTable _capacityMinusOne = null!;
    private PendingRequestTable _full = null!;
    private RpcRequestOperation<int>[] _halfFullOperations = null!;
    private RpcRequestOperation<int>[] _capacityMinusOneOperations = null!;
    private RpcRequestOperation<int>[] _fullOperations = null!;
    private byte[] _responsePayload = null!;

    [Params(64, 1024, 65_536)]
    public int Capacity { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = new SharpLinkRuntimeContextBuilder().Build();
        _responsePayload = new byte[sizeof(int)];
        _empty = CreateTable();
        _halfFull = CreateTable();
        _capacityMinusOne = CreateTable();
        _full = CreateTable();
        _halfFullOperations = Fill(_halfFull, Capacity / 2);
        _capacityMinusOneOperations = Fill(_capacityMinusOne, Capacity - 1);
        _fullOperations = Fill(_full, Capacity);
    }

    [Benchmark(Baseline = true)]
    public int EmptyRegisterAndComplete()
        => RegisterAndComplete(_empty);

    [Benchmark]
    public int HalfFullRegisterAndComplete()
        => RegisterAndComplete(_halfFull);

    [Benchmark]
    public int CapacityMinusOneRegisterAndComplete()
        => RegisterAndComplete(_capacityMinusOne);

    [Benchmark]
    public SharpLinkErrorCode FullFailFast()
    {
        try
        {
            _ = _full.Rent<int>(out _);
            throw new InvalidOperationException("A full pending table unexpectedly accepted a request.");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
        {
            return exception.Code;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Drain(_halfFull, _halfFullOperations);
        Drain(_capacityMinusOne, _capacityMinusOneOperations);
        Drain(_full, _fullOperations);
        _empty.Dispose();
        _halfFull.Dispose();
        _capacityMinusOne.Dispose();
        _full.Dispose();
        _context.Dispose();
    }

    private PendingRequestTable CreateTable()
        => new(
            Capacity,
            _context.Codecs,
            BenchmarkOwner.Instance,
            TimeProvider.System);

    private static RpcRequestOperation<int>[] Fill(PendingRequestTable table, int count)
    {
        var operations = new RpcRequestOperation<int>[count];
        for (var index = 0; index < operations.Length; index++)
            operations[index] = table.Rent<int>(out _);
        return operations;
    }

    private int RegisterAndComplete(PendingRequestTable table)
    {
        var operation = table.Rent<int>(out var requestId);
        var payload = new ReadOnlySequence<byte>(_responsePayload);
        if (!table.Dispatch(requestId, ref payload))
            throw new InvalidOperationException("Benchmark response did not match its pending request.");
        return operation.AsValueTask().GetAwaiter().GetResult();
    }

    private static void Drain(
        PendingRequestTable table,
        RpcRequestOperation<int>[] operations)
    {
        table.FailAllPendingRequests(new IOException("benchmark cleanup"));
        foreach (var operation in operations)
        {
            try
            {
                _ = operation.AsValueTask().GetAwaiter().GetResult();
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class BenchmarkOwner : IPendingCallOwner
    {
        internal static BenchmarkOwner Instance { get; } = new();

        public void OnPendingCallRegistered()
        {
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
        }
    }
}
