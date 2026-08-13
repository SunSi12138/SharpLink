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
    private static readonly byte[] SPayload16 = CreatePayload(16, 17, 31);
    private static readonly byte[] SPayload4096 = CreatePayload(4096, 23, 47);
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

    public async ValueTask<long> UploadPayloadsAsync(IAsyncEnumerable<byte[]> payloads)
    {
        long score = 0;
        await foreach (var payload in payloads)
        {
            score += GetPayloadScore(payload);
        }

        return score;
    }

    public async IAsyncEnumerable<byte[]> DownloadPayloadsAsync(int count, int payloadSize)
    {
        var payload = GetPayload(payloadSize);
        for (var i = 0; i < count; i++)
        {
            yield return payload;
            await Task.CompletedTask;
        }
    }

    public async IAsyncEnumerable<byte[]> DuplexPayloadsAsync(IAsyncEnumerable<byte[]> payloads)
    {
        await foreach (var payload in payloads)
        {
            yield return payload;
        }
    }

    public async ValueTask<int> SlowAsync(int value, int delayMs)
    {
        await Task.Delay(delayMs);
        return value;
    }

    internal static byte[] GetPayload(int payloadSize) => payloadSize switch
    {
        16 => SPayload16,
        4096 => SPayload4096,
        _ => throw new ArgumentOutOfRangeException(
            nameof(payloadSize),
            payloadSize,
            "The generated ABI baseline supports 16-byte and 4-KiB payloads.")
    };

    internal static long GetPayloadScore(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0)
            return 0;
        return payload.Length + payload[0] + payload[^1];
    }

    private static byte[] CreatePayload(int length, byte first, byte last)
    {
        var payload = new byte[length];
        payload[0] = first;
        payload[^1] = last;
        return payload;
    }
}
