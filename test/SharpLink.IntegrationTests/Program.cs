using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.IntegrationTests;

public static class Program
{
    public static async Task<int> Main()
    {
        var port = GetFreePort();
        var cts = new CancellationTokenSource();
        var runToken = cts.Token;

        var server = SharpLinkServerBuilder.Create()
            .AddService<ITestService, TestService>()
            .UseTcp(port, IPAddress.Loopback.ToString())
            .UseSerializer(new MemoryPackSerializerAdaptor())
            .Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.Start(runToken);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(new MemoryPackSerializerAdaptor())
            .Build();

        try
        {
            var connected = await client.ConnectAsync(runToken);
            if (!connected) throw new Exception("client connect failed");

            var svc = client.Get<ITestService>();
            var add = await svc.AddAsync(10, 20);
            Assert(add == 30, "AddAsync");

            var echo = await svc.EchoAsync(new Person { Name = "s", Age = 1, Tags = ["x"] });
            Assert(echo is { Name: "s-r", Age: 2 }, "EchoAsync");

            var sum = await svc.UploadAsync(ToAsyncEnumerable([1, 2, 3, 4], runToken));
            Assert(sum == 10, "UploadAsync");

            var values = await CollectAsync(svc.DownloadAsync(3), runToken);
            Assert(values.SequenceEqual(["v-0", "v-1", "v-2"]), "DownloadAsync");

            await svc.NotifyAsync("ok");
            Console.WriteLine("[IntegrationTests] PASS");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[IntegrationTests] FAIL: {ex}");
            return 1;
        }
        finally
        {
            await cts.CancelAsync();
            (client as IDisposable)?.Dispose();
            (server as IDisposable)?.Dispose();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
            cts.Dispose();
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception($"assert failed: {name}");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> values, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> stream, CancellationToken ct)
    {
        var list = new List<T>();
        await foreach (var item in stream.WithCancellation(ct))
            list.Add(item);
        return list;
    }
}

public interface ITestService : IService
{
    ValueTask<int> AddAsync(int left, int right);
    ValueTask<Person> EchoAsync(Person person);
    ValueTask<int> UploadAsync(IAsyncEnumerable<int> values);
    IAsyncEnumerable<string> DownloadAsync(int count);
    [Oneway]
    ValueTask NotifyAsync(string message);
}

[RpcService]
public class TestService : ITestService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public ValueTask<Person> EchoAsync(Person person)
    {
        person.Name += "-r";
        person.Age += 1;
        return ValueTask.FromResult(person);
    }

    public async ValueTask<int> UploadAsync(IAsyncEnumerable<int> values)
    {
        var sum = 0;
        await foreach (var i in values) sum += i;
        return sum;
    }

    public async IAsyncEnumerable<string> DownloadAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return $"v-{i}";
            await Task.Yield();
        }
    }

    public ValueTask NotifyAsync(string message)
    {
        _ = message;
        return ValueTask.CompletedTask;
    }
}

[MemoryPackable]
public partial class Person
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public List<string> Tags { get; set; } = [];
}
