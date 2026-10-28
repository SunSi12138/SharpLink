using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.ProtocolV2CrossVersion;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        PrintIdentity();
        if (args is ["identity"])
            return 0;
        if (args is ["server", var serverExpectation])
            return await RunServerAsync(serverExpectation == "reject").ConfigureAwait(false);
        if (args is ["client", var portText, var clientExpectation] && int.TryParse(portText, out var port))
            return await RunClientAsync(port, clientExpectation == "reject").ConfigureAwait(false);
        Console.Error.WriteLine("Usage: identity | server positive/reject | client PORT positive/reject");
        return 2;
    }

    private static void PrintIdentity()
    {
        var owner = typeof(Program).Assembly;
        var locator = owner.GetCustomAttribute<SharpLinkGeneratedAssemblyManifestAttribute>()
            ?? throw new InvalidOperationException("Missing generated manifest locator.");
        var manifest = (ISharpLinkGeneratedAssemblyManifest)Activator.CreateInstance(locator.ManifestType)!;
        foreach (var name in new[] { "Abstractions", "Runtime", "Client", "Server", "Sdk" })
            Assembly.Load("SharpLink." + name);
        var packages = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly != owner &&
                assembly.GetName().Name!.StartsWith("SharpLink.", StringComparison.Ordinal))
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .Select(assembly => new
            {
                name = assembly.GetName().Name,
                version = assembly.GetName().Version!.ToString(),
                informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                path = assembly.Location,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant(),
                mvid = assembly.ManifestModule.ModuleVersionId
            }).ToArray();
        Console.WriteLine("IDENTITY " + JsonSerializer.Serialize(new
        {
            packages,
            api = locator.ApiVersion,
            protocol = locator.ProtocolVersion,
            abi = locator.AbiIdentity,
            generator = locator.GeneratorVersion,
            rpcAssemblyHash = manifest.RpcAssemblyHash.ToString(),
            contractIds = manifest.Contracts.Select(contract => contract.ContractId).ToArray(),
            minor = ProtocolV2Constants.MinorVersion,
            minimumMinor = ProtocolV2Constants.MinimumCompatibleMinorVersion
        }));
    }

    private static async Task<int> RunServerAsync(bool expectRejection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback);
        var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        await using var server = builder.Build();
        var runTask = server.RunUntilStoppedAsync(timeout.Token).AsTask();
        Console.WriteLine($"SERVER_READY {port}");
        Console.Out.Flush();
        try
        {
            if (await Console.In.ReadLineAsync(timeout.Token).ConfigureAwait(false) != "STOP")
                throw new InvalidOperationException("Missing controller STOP; EOF is not success.");
            if (!expectRejection)
                await CrossVersionService.CompletionObserved.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            await timeout.CancelAsync().ConfigureAwait(false);
            try { await runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            var expectedInvocations = expectRejection ? 0 : 5;
            if (CrossVersionService.Invocations != expectedInvocations)
                throw new InvalidOperationException($"Expected {expectedInvocations} invocations, got {CrossVersionService.Invocations}.");
            Console.WriteLine(expectRejection ? "SERVER_REJECTION_PASS" : "SERVER_PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SERVER_FAIL {exception}");
            return 1;
        }
    }

    private static async Task<int> RunClientAsync(int port, bool expectRejection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = SharpClientBuilder.Create()
            .DisableRequestTimeout().UseTcp(IPAddress.Loopback.ToString(), port).Build();
        try
        {
            // A failed handshake, timeout, missing contract or ABI failure is NOT a hash-rejection pass.
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            Console.WriteLine("CLIENT_CONNECTED");
            ICrossVersionService service;
            try
            {
                service = client.Get<ICrossVersionService>();
            }
            catch (SharpLinkException exception) when (
                expectRejection && exception.Code == SharpLinkErrorCode.FailedPrecondition &&
                exception.Message.Contains("local RpcAssemblyHash=", StringComparison.OrdinalIgnoreCase) &&
                exception.Message.Contains("remote RpcAssemblyHash=", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("REJECTION " + JsonSerializer.Serialize(new
                {
                    code = exception.Code.ToString(), message = exception.Message
                }));
                Console.WriteLine("CLIENT_REJECTION_PASS");
                return 0;
            }
            if (expectRejection)
                throw new InvalidOperationException("Incompatible wire hash was accepted.");
            var result = await service.AddAsync(20, 22).ConfigureAwait(false);
            if (result != 42)
                throw new InvalidOperationException($"Unexpected unary result: {result}.");
            var upload = await service.SumAsync(ToAsyncEnumerable([3, 5, 7])).ConfigureAwait(false);
            if (upload != 15)
                throw new InvalidOperationException($"Unexpected upload sum: {upload}.");
            if (!(await CollectAsync(service.RangeAsync(4)).ConfigureAwait(false)).SequenceEqual([0, 1, 2, 3]))
                throw new InvalidOperationException("Unexpected server stream.");
            if (!(await CollectAsync(service.DoubleAsync(ToAsyncEnumerable([2, 4, 6]))).ConfigureAwait(false)).SequenceEqual([4, 8, 12]))
                throw new InvalidOperationException("Unexpected duplex stream.");
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
#if INCOMPATIBLE_WIRE_CONTRACT
    [NonCancellable]
    ValueTask<int> ExtraAsync();
#endif
}

[RpcService]
public sealed class CrossVersionService : ICrossVersionService
{
    internal static int Invocations;
#if INCOMPATIBLE_WIRE_CONTRACT
    public ValueTask<int> ExtraAsync()
    {
        Interlocked.Increment(ref Invocations);
        return ValueTask.FromResult(-1);
    }
#endif
    internal static TaskCompletionSource CompletionObserved { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<int> AddAsync(int left, int right)
    {
        Interlocked.Increment(ref Invocations);
        return ValueTask.FromResult(left + right);
    }

    public async ValueTask<int> SumAsync(IAsyncEnumerable<int> values)
    {
        Interlocked.Increment(ref Invocations);
        var sum = 0;
        await foreach (var value in values.ConfigureAwait(false))
            sum += value;
        return sum;
    }

    public async IAsyncEnumerable<int> RangeAsync(int count)
    {
        Interlocked.Increment(ref Invocations);
        for (var value = 0; value < count; value++)
        {
            yield return value;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> DoubleAsync(IAsyncEnumerable<int> values)
    {
        Interlocked.Increment(ref Invocations);
        await foreach (var value in values.ConfigureAwait(false))
            yield return value * 2;
    }

    public ValueTask CompleteAsync()
    {
        Interlocked.Increment(ref Invocations);
        CompletionObserved.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
