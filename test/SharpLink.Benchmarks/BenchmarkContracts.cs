using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

[RpcContract]
public interface IBenchmarkRpc : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);
    [NonCancellable]
    ValueTask<string> EchoAsync(string value);
    [NonCancellable]
    ValueTask<BenchmarkPayload> EchoPayloadAsync(BenchmarkPayload payload);
    [NonCancellable]
    ValueTask<int> SumArrayAsync(int[] values);
    [NonCancellable]
    ValueTask<int> SumListAsync(List<int> values);
    [NonCancellable]
    ValueTask<int> SumMemoryAsync(Memory<byte> values);

    [Oneway]
    [NonCancellable]
    ValueTask PublishEventAsync(int code, long ticks, string tag);

    [NonCancellable]
    ValueTask<int> UploadNumbersAsync(IAsyncEnumerable<int> numbers);
    [NonCancellable]
    IAsyncEnumerable<int> DownloadNumbersAsync(int count);
    [NonCancellable]
    IAsyncEnumerable<string> DuplexAsync(IAsyncEnumerable<string> values);
    [NonCancellable]
    ValueTask<int> MergeStreamsAsync(IAsyncEnumerable<int> left, IAsyncEnumerable<int> right);
}

[MemoryPackable]
public partial class BenchmarkPayload
{
    public string Name { get; set; } = string.Empty;
    public int[] Values { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public Memory<byte> Buffer { get; set; }
}
