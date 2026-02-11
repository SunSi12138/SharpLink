using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MemoryPack;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

public interface IBenchmarkRpc : IService
{
    ValueTask<int> AddAsync(int left, int right);
    ValueTask<string> EchoAsync(string value);
    ValueTask<BenchmarkPayload> EchoPayloadAsync(BenchmarkPayload payload);
    ValueTask<int> SumArrayAsync(int[] values);
    ValueTask<int> SumListAsync(List<int> values);
    ValueTask<int> SumMemoryAsync(Memory<byte> values);

    [Oneway]
    ValueTask PublishEventAsync(int code, long ticks, string tag);

    ValueTask<int> UploadNumbersAsync(IAsyncEnumerable<int> numbers);
    IAsyncEnumerable<int> DownloadNumbersAsync(int count);
    IAsyncEnumerable<string> DuplexAsync(IAsyncEnumerable<string> values);
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
