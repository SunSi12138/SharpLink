using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

[RpcService]
public class BenchmarkRpcService : IBenchmarkRpc
{
    private long _publishedCount;

    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public ValueTask<string> EchoAsync(string value) => ValueTask.FromResult(value);

    public ValueTask<BenchmarkPayload> EchoPayloadAsync(BenchmarkPayload payload) => ValueTask.FromResult(payload);

    public ValueTask<int> SumArrayAsync(int[] values) => ValueTask.FromResult(values.Sum());

    public ValueTask<int> SumListAsync(List<int> values) => ValueTask.FromResult(values.Sum());

    public ValueTask<int> SumMemoryAsync(Memory<byte> values)
    {
        var sum = 0;
        foreach (var value in values.Span)
        {
            sum += value;
        }

        return ValueTask.FromResult(sum);
    }

    public ValueTask PublishEventAsync(int code, long ticks, string tag)
    {
        _ = code;
        _ = ticks;
        _ = tag;
        Interlocked.Increment(ref _publishedCount);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> UploadNumbersAsync(IAsyncEnumerable<int> numbers)
    {
        var sum = 0;
        await foreach (var number in numbers)
        {
            sum += number;
        }

        return sum;
    }

    public async IAsyncEnumerable<int> DownloadNumbersAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return i;
            await Task.CompletedTask;
        }
    }

    public async IAsyncEnumerable<string> DuplexAsync(IAsyncEnumerable<string> values)
    {
        await foreach (var value in values)
        {
            yield return value;
        }
    }

    public async ValueTask<int> MergeStreamsAsync(IAsyncEnumerable<int> left, IAsyncEnumerable<int> right)
    {
        var sum = 0;
        await foreach (var value in left)
        {
            sum += value;
        }

        await foreach (var value in right)
        {
            sum += value;
        }

        return sum;
    }
}
