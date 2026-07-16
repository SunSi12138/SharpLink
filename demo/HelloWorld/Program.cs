using SharpLink.Sdk;
using System.Runtime.InteropServices;
using DemoBase;
using MemoryPack;
using SharpLink.Runtime;

const int port = 19090;

using var cts = new CancellationTokenSource();


var server = DemoTcp.CreateServer<IHelloService, HelloService>(port);
var serverTask = DemoTcp.StartServerAsync(server, cts.Token);
var client = DemoTcp.CreateClient(port);

try
{
    await DemoTcp.EnsureConnectedAsync(client, cts.Token, "Failed to connect to SharpLink server.");

    var hello = client.Get<IHelloService>();
    await hello.Notify();

    var ping = await hello.Ping();
    Console.WriteLine($"Ping: {ping}");

    var add = await hello.Add(12, 30);
    Console.WriteLine($"Add: {add}");

    var mix = await hello.ComposeGreeting("SharpLink", 7, true, 99.5, DateTime.UtcNow);
    Console.WriteLine($"ComposeGreeting: {mix}");

    var noArgValue = await hello.GetServerCode();
    Console.WriteLine($"GetServerCode: {noArgValue}");

    var point = new BlittablePoint { X = 11, Y = 31 };
    var pointSum = await hello.SumPoint(point);
    Console.WriteLine($"SumPoint: {pointSum}");

    var tupleValue = (A: 10, B: 20, C: 30L);
    var tupleSum = await hello.SumTuple(tupleValue);
    Console.WriteLine($"SumTuple: {tupleSum}");

    var mixed = await hello.MixPointAndTuple(point, tupleValue);
    Console.WriteLine($"MixPointAndTuple: {mixed}");

    var arraySum = await hello.SumArray([1, 2, 3, 4, 5]);
    Console.WriteLine($"SumArray: {arraySum}");

    var jagged = new[] { new[] { 1, 2 }, new[] { 3, 4, 5 }, new[] { 6 } };
    var jaggedSum = await hello.SumJaggedArray(jagged);
    Console.WriteLine($"SumJaggedArray: {jaggedSum}");

    var person = new UserProfile
    {
        Name = "SharpLink",
        Age = 6,
        Tags = ["rpc", "demo", "say hello to sharplink"],
    };
    var personEcho = await hello.EchoUser(person);
    Console.WriteLine($"EchoUser: {personEcho.Name}/{personEcho.Age} [{string.Join(", ", personEcho.Tags)}]");

    var listResult = await hello.ReverseList([9, 7, 5, 3, 1]);
    Console.WriteLine($"ReverseList: [{string.Join(", ", listResult)}]");

    var mem = new Memory<byte>([1, 2, 3, 4, 5, 6]);
    var memResult = await hello.ProcessMemory(mem);
    Console.WriteLine($"ProcessMemory Length: {memResult.Length}, First={memResult.Span[0]}, Last={memResult.Span[^1]}");
}
finally
{
    await DemoTcp.ShutdownAsync(cts, serverTask, client, server);
}

[RpcService]
public class HelloService : IHelloService
{
    public ValueTask Notify()
    {
        Console.WriteLine("[Server] Notify called");
        return ValueTask.CompletedTask;
    }

    public ValueTask<string> Ping() => ValueTask.FromResult("PONG");

    public ValueTask<int> Add(int left, int right) => ValueTask.FromResult(left + right);

    public ValueTask<string> ComposeGreeting(string name, int level, bool enabled, double score, DateTime timestamp)
        => ValueTask.FromResult($"Hello {name}, level={level}, enabled={enabled}, score={score:F1}, ts={timestamp:O}");

    public ValueTask<int> GetServerCode() => ValueTask.FromResult(2026);

    public ValueTask<int> SumPoint(BlittablePoint point) => ValueTask.FromResult(point.X + point.Y);

    public ValueTask<long> SumTuple((int A, int B, long C) value)
        => ValueTask.FromResult((long)value.A + value.B + value.C);

    public ValueTask<long> MixPointAndTuple(BlittablePoint point, (int A, int B, long C) value)
        => ValueTask.FromResult((long)point.X + point.Y + value.A + value.B + value.C);

    public ValueTask<int> SumArray(int[] values) => ValueTask.FromResult(values.Sum());

    public ValueTask<int> SumJaggedArray(int[][] values)
    {
        var sum = 0;
        foreach (var arr in values)
        {
            sum += arr.Sum();
        }
        return ValueTask.FromResult(sum);
    }

    public ValueTask<UserProfile> EchoUser(UserProfile user)
    {
        user.Name += "-echo";
        user.Age += 1;
        user.Tags.Add("server");
        return ValueTask.FromResult(user);
    }

    public ValueTask<List<int>> ReverseList(List<int> values)
    {
        values.Reverse();
        return ValueTask.FromResult(values);
    }

    public ValueTask<Memory<byte>> ProcessMemory(Memory<byte> data)
    {
        var copy = data.ToArray();
        Array.Reverse(copy);
        return ValueTask.FromResult<Memory<byte>>(copy);
    }
}

[RpcContract]
public interface IHelloService : IService
{
    ValueTask Notify();
    ValueTask<string> Ping();
    ValueTask<int> Add(int left, int right);
    ValueTask<string> ComposeGreeting(string name, int level, bool enabled, double score, DateTime timestamp);
    ValueTask<int> GetServerCode();
    ValueTask<int> SumPoint(BlittablePoint point);
    ValueTask<long> SumTuple((int A, int B, long C) value);
    ValueTask<long> MixPointAndTuple(BlittablePoint point, (int A, int B, long C) value);
    ValueTask<int> SumArray(int[] values);
    ValueTask<int> SumJaggedArray(int[][] values);
    ValueTask<UserProfile> EchoUser(UserProfile user);
    ValueTask<List<int>> ReverseList(List<int> values);
    ValueTask<Memory<byte>> ProcessMemory(Memory<byte> data);
}

[StructLayout(LayoutKind.Sequential)]
public struct BlittablePoint
{
    public int X;
    public int Y;
}

[MemoryPackable]
public partial class UserProfile
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public List<string> Tags { get; set; } = [];
}
