using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpPack;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

[RpcContract]
public interface IBenchmarkRpc : IService
{
    [Idempotent]
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);

    [NonCancellable]
    ValueTask<int> AddNonIdempotentAsync(int left, int right);

    [Idempotent]
    [SharpLink.Sdk.Timeout(30)]
    [NonCancellable]
    ValueTask<int> AddTimedIdempotentAsync(int left, int right);

    ValueTask<int> AddCancellableAsync(
        int left,
        int right,
        CancellationToken cancellationToken = default);

    [NonCancellable]
    ValueTask<int> PingAsync();

    [NonCancellable]
    ValueTask TouchAsync();

    [NonCancellable]
    ValueTask<string> EchoAsync(string value);

    [NonCancellable]
    ValueTask<string?> EchoNullableAsync(string? value);

    [NonCancellable]
    ValueTask<BenchmarkPayload> EchoPayloadAsync(BenchmarkPayload payload);

    [NonCancellable]
    ValueTask<byte[]> EchoBytesAsync(byte[] value);

    [NonCancellable]
    ValueTask<int> SumArrayAsync(int[] values);

    [NonCancellable]
    ValueTask<int> SumListAsync(List<int> values);

    [NonCancellable]
    ValueTask<int> SumMemoryAsync(Memory<byte> values);

    [Oneway]
    [NonCancellable]
    ValueTask PublishEventAsync(int code, long ticks, string tag);

    [Oneway]
    [SharpLink.Sdk.Timeout(30)]
    [NonCancellable]
    ValueTask PublishTimedEventAsync(int code);

    [Oneway]
    ValueTask PublishCancellableEventAsync(
        int code,
        CancellationToken cancellationToken = default);

    [Oneway]
    [NonCancellable]
    ValueTask PublishNumbersAsync(IAsyncEnumerable<int> numbers);

    [Oneway]
    [SharpLink.Sdk.Timeout(30)]
    [NonCancellable]
    ValueTask PublishTwoStreamsAsync(
        IAsyncEnumerable<int> left,
        IAsyncEnumerable<int> right);

    [NonCancellable]
    ValueTask<int> UploadNumbersAsync(IAsyncEnumerable<int> numbers);

    ValueTask<int> UploadNumbersCancellableAsync(
        IAsyncEnumerable<int> numbers,
        CancellationToken cancellationToken = default);

    [NonCancellable]
    IAsyncEnumerable<int> DownloadNumbersAsync(int count);

    IAsyncEnumerable<int> DownloadNumbersCancellableAsync(
        int count,
        CancellationToken cancellationToken = default);

    [NonCancellable]
    IAsyncEnumerable<string> DuplexAsync(IAsyncEnumerable<string> values);

    IAsyncEnumerable<string> DuplexCancellableAsync(
        IAsyncEnumerable<string> values,
        CancellationToken cancellationToken = default);

    [NonCancellable]
    ValueTask<int> MergeStreamsAsync(
        IAsyncEnumerable<int> left,
        IAsyncEnumerable<int> right);

    [NonCancellable]
    ValueTask<long> UploadPayloadsAsync(IAsyncEnumerable<byte[]> payloads);

    [NonCancellable]
    IAsyncEnumerable<byte[]> DownloadPayloadsAsync(int count, int payloadSize);

    [NonCancellable]
    IAsyncEnumerable<byte[]> DuplexPayloadsAsync(IAsyncEnumerable<byte[]> payloads);
}

[SharpPackable]
public partial class BenchmarkPayload
{
    public string Name { get; set; } = string.Empty;
    public int[] Values { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public Memory<byte> Buffer { get; set; }
}
