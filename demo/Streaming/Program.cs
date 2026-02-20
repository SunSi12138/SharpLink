using DemoBase;
using SharpLink.Sdk;
using MemoryPack;
using SharpLink.Runtime;

var port = DemoStream.GetFreePort();
using var cts = new CancellationTokenSource();

RpcCodecRegistry.Initialize(MemoryPackCodec.Resolver);

var server = DemoTcp.CreateServer<IStreamingService, StreamingService>(port);
var serverTask = DemoTcp.StartServerAsync(server, cts.Token);
var client = DemoTcp.CreateClient(port);

try
{
    await DemoTcp.EnsureConnectedAsync(client, cts.Token, "Failed to connect streaming demo client.");

    var streamService = client.Get<IStreamingService>();

    var uploadSum = await streamService.UploadNumbers(DemoStream.ToAsyncEnumerable([1, 2, 3, 4, 5], cancellationToken: cts.Token));
    Console.WriteLine($"1) Client->Server stream sum = {uploadSum}");

    var labels = await DemoStream.CollectAsync(streamService.DownloadLabels(3), cts.Token);
    Console.WriteLine($"2) Server->Client stream values = [{string.Join(", ", labels)}]");

    var duplex = await DemoStream.CollectAsync(streamService.Chat(DemoStream.ToAsyncEnumerable(["a", "b", "c"], cancellationToken: cts.Token)), cts.Token);
    Console.WriteLine($"3) Bidirectional stream values = [{string.Join(", ", duplex)}]");

    var multiSum = await streamService.MergeSums(
        DemoStream.ToAsyncEnumerable([1, 3, 5, 7], cancellationToken: cts.Token),
        DemoStream.ToAsyncEnumerable([2, 4, 6, 8], cancellationToken: cts.Token));
    Console.WriteLine($"4) Client multi-stream sum = {multiSum}");

    var mixed = await streamService.ScaleAndSum(3, DemoStream.ToAsyncEnumerable([2, 4, 6], cancellationToken: cts.Token));
    Console.WriteLine($"5) Mixed (scalar + stream) result = {mixed}");

    var classSum = await streamService.UploadClassItems(DemoStream.ToAsyncEnumerable([
        new BatchEnvelope { BatchId = 1, Source = "A", Values = [1, 2, 3] },
        new BatchEnvelope { BatchId = 2, Source = "B", Values = [4, 5] }
    ], cancellationToken: cts.Token));
    Console.WriteLine($"6) Class stream sum = {classSum}");

    var tupleItems = await DemoStream.CollectAsync(streamService.DownloadTupleItems(3), cts.Token);
    Console.WriteLine($"7) ValueTuple stream values = [{string.Join(", ", tupleItems.Select(x => $"({x.Index},{x.Label})"))}]");

    var structSum = await streamService.UploadStructPoints(DemoStream.ToAsyncEnumerable([
        new SamplePoint { X = 2, Y = 3 },
        new SamplePoint { X = 4, Y = 5 }
    ], cancellationToken: cts.Token));
    Console.WriteLine($"8) Struct stream sum = {structSum}");

    var arrayBatchSum = await streamService.SumArrayBatches(DemoStream.ToAsyncEnumerable([
        new[] { 1, 2, 3 },
        [4, 5, 6]
    ], cancellationToken: cts.Token));
    Console.WriteLine($"9) Array batch stream sum = {arrayBatchSum}");

    var listBatchSum = await streamService.SumListBatches(DemoStream.ToAsyncEnumerable([
        new List<int> { 1, 3, 5 },
        [2, 4, 6]
    ], cancellationToken: cts.Token));
    Console.WriteLine($"10) List batch stream sum = {listBatchSum}");

    var memoryBatchSum = await streamService.SumMemoryBatches(DemoStream.ToAsyncEnumerable([
        new Memory<byte>([1, 2, 3]),
        new Memory<byte>([4, 5, 6])
    ], cancellationToken: cts.Token));
    Console.WriteLine($"11) Memory batch stream sum = {memoryBatchSum}");
}
finally
{
    await DemoTcp.ShutdownAsync(cts, serverTask, client as IDisposable, server as IDisposable);
}

[RpcContract]
public interface IStreamingService : IService
{
    ValueTask<int> UploadNumbers(IAsyncEnumerable<int> numbers);
    IAsyncEnumerable<string> DownloadLabels(int count);
    IAsyncEnumerable<string> Chat(IAsyncEnumerable<string> messages);
    ValueTask<int> MergeSums(IAsyncEnumerable<int> left, IAsyncEnumerable<int> right);
    ValueTask<int> ScaleAndSum(int factor, IAsyncEnumerable<int> numbers);
    ValueTask<int> UploadClassItems(IAsyncEnumerable<BatchEnvelope> items);
    IAsyncEnumerable<(int Index, string Label)> DownloadTupleItems(int count);
    ValueTask<int> UploadStructPoints(IAsyncEnumerable<SamplePoint> points);
    ValueTask<int> SumArrayBatches(IAsyncEnumerable<int[]> batches);
    ValueTask<int> SumListBatches(IAsyncEnumerable<List<int>> batches);
    ValueTask<int> SumMemoryBatches(IAsyncEnumerable<Memory<byte>> batches);
}

[RpcService]
public class StreamingService : IStreamingService
{
    public async ValueTask<int> UploadNumbers(IAsyncEnumerable<int> numbers)
    {
        var sum = 0;
        await foreach (var number in numbers)
        {
            sum += number;
        }

        return sum;
    }

    public async IAsyncEnumerable<string> DownloadLabels(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            yield return $"label-{i}";
            await Task.Delay(5);
        }
    }

    public async IAsyncEnumerable<string> Chat(IAsyncEnumerable<string> messages)
    {
        await foreach (var message in messages)
        {
            yield return $"server:{message}";
        }
    }

    public async ValueTask<int> MergeSums(IAsyncEnumerable<int> left, IAsyncEnumerable<int> right)
    {
        var sum = 0;
        await foreach (var number in left)
        {
            sum += number;
        }
        await foreach (var number in right)
        {
            sum += number;
        }

        return sum;
    }

    public async ValueTask<int> ScaleAndSum(int factor, IAsyncEnumerable<int> numbers)
    {
        var sum = 0;
        await foreach (var number in numbers)
        {
            sum += number * factor;
        }

        return sum;
    }

    public async ValueTask<int> UploadClassItems(IAsyncEnumerable<BatchEnvelope> items)
    {
        var sum = 0;
        await foreach (var item in items)
        {
            sum += item.BatchId;
            if (item.Values is not null)
            {
                sum += item.Values.Sum();
            }
        }

        return sum;
    }

    public async IAsyncEnumerable<(int Index, string Label)> DownloadTupleItems(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return (i, $"tuple-{i}");
            await Task.Delay(5);
        }
    }

    public async ValueTask<int> UploadStructPoints(IAsyncEnumerable<SamplePoint> points)
    {
        var sum = 0;
        await foreach (var point in points)
        {
            sum += point.X + point.Y;
        }

        return sum;
    }

    public async ValueTask<int> SumArrayBatches(IAsyncEnumerable<int[]> batches)
    {
        var sum = 0;
        await foreach (var batch in batches)
        {
            sum += batch.Sum();
        }

        return sum;
    }

    public async ValueTask<int> SumListBatches(IAsyncEnumerable<List<int>> batches)
    {
        var sum = 0;
        await foreach (var batch in batches)
        {
            sum += batch.Sum();
        }

        return sum;
    }

    public async ValueTask<int> SumMemoryBatches(IAsyncEnumerable<Memory<byte>> batches)
    {
        var sum = 0;
        await foreach (var batch in batches)
        {
            foreach (var value in batch.Span)
            {
                sum += value;
            }
        }

        return sum;
    }
}

[MemoryPackable]
public partial class BatchEnvelope
{
    public int BatchId { get; set; }
    public string Source { get; set; } = string.Empty;
    public List<int>? Values { get; set; }
}

[MemoryPackable]
public partial struct SamplePoint
{
    public int X { get; init; }
    public int Y { get; init; }
}
