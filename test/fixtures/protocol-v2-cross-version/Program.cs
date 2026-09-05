using System.Net;
using System.Net.Sockets;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.ProtocolV2CrossVersion;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "server", StringComparison.Ordinal))
            return await RunServerAsync().ConfigureAwait(false);
        if (args.Length == 2 &&
            string.Equals(args[0], "client", StringComparison.Ordinal) &&
            int.TryParse(args[1], out var port))
        {
            return await RunClientAsync(port).ConfigureAwait(false);
        }

        Console.Error.WriteLine("Usage: <server | client PORT>");
        return 2;
    }

    private static async Task<int> RunServerAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var builder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString());
        var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        await using var server = builder.Build();
        var runTask = server.RunAsync(timeout.Token).AsTask();
        Console.WriteLine($"SERVER_READY {port}");
        Console.Out.Flush();
        try
        {
            await CrossVersionService.CompletionObserved.Task
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            await timeout.CancelAsync().ConfigureAwait(false);
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            Console.WriteLine("SERVER_PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SERVER_FAIL {exception}");
            return 1;
        }
    }

    private static async Task<int> RunClientAsync(int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .Build();
        try
        {
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var result = await client.Get<ICrossVersionService>()
                .AddAsync(20, 22)
                .ConfigureAwait(false);
            if (result != 42)
                throw new InvalidOperationException($"Unexpected cross-version result: {result}.");

            var service = client.Get<ICrossVersionService>();
            var upload = await service.SumAsync(ToAsyncEnumerable([3, 5, 7])).ConfigureAwait(false);
            if (upload != 15)
                throw new InvalidOperationException($"Unexpected cross-version upload sum: {upload}.");

            var download = await CollectAsync(service.RangeAsync(4)).ConfigureAwait(false);
            if (!download.SequenceEqual([0, 1, 2, 3]))
                throw new InvalidOperationException("Unexpected cross-version server stream.");

            var duplex = await CollectAsync(service.DoubleAsync(
                ToAsyncEnumerable([2, 4, 6]))).ConfigureAwait(false);
            if (!duplex.SequenceEqual([4, 8, 12]))
                throw new InvalidOperationException("Unexpected cross-version duplex stream.");

            await service.CompleteAsync().ConfigureAwait(false);
            Console.WriteLine("CLIENT_PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CLIENT_FAIL {exception}");
            return 1;
        }
    }

    private static async IAsyncEnumerable<int> ToAsyncEnumerable(IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            yield return value;
            await Task.Yield();
        }
    }

    private static async Task<List<int>> CollectAsync(IAsyncEnumerable<int> values)
    {
        var result = new List<int>();
        await foreach (var value in values.ConfigureAwait(false))
            result.Add(value);
        return result;
    }
}

[RpcContract]
public interface ICrossVersionService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);
    [NonCancellable]
    ValueTask<int> SumAsync(IAsyncEnumerable<int> values);
    [NonCancellable]
    IAsyncEnumerable<int> RangeAsync(int count);
    [NonCancellable]
    IAsyncEnumerable<int> DoubleAsync(IAsyncEnumerable<int> values);
    [Oneway]
    [NonCancellable]
    ValueTask CompleteAsync();
}

[RpcService]
public sealed class CrossVersionService : ICrossVersionService
{
    internal static TaskCompletionSource CompletionObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public async ValueTask<int> SumAsync(IAsyncEnumerable<int> values)
    {
        var sum = 0;
        await foreach (var value in values.ConfigureAwait(false))
            sum += value;
        return sum;
    }

    public async IAsyncEnumerable<int> RangeAsync(int count)
    {
        for (var value = 0; value < count; value++)
        {
            yield return value;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> DoubleAsync(IAsyncEnumerable<int> values)
    {
        await foreach (var value in values.ConfigureAwait(false))
            yield return value * 2;
    }

    public ValueTask CompleteAsync()
    {
        CompletionObserved.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
