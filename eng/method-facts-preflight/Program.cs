using System.Collections;
using System.Net;
using System.Reflection;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

namespace MethodFactsPreflight;

// Deliberately separate from the measured service: no timing loop or performance samples.
public static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static async Task Main()
    {
        var rows = new List<object>();
        foreach (var transport in new[] { "tcp", "sharedmemory" })
        {
            var capture = new Capture();
            var builder = SharpLinkServerBuilder.Create().AddInterceptor(capture);
            var name = "facts-preflight-" + Guid.NewGuid().ToString("N");
            if (transport == "tcp") builder.UseTcp(0, IPAddress.Loopback); else builder.UseSharedMemory(name);
            var port = transport == "tcp" ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
            await using var server = builder.Build();
            await server.StartAsync();
            var cb = SharpClientBuilder.Create().DisableRequestTimeout();
            if (transport == "tcp") cb.UseTcp("127.0.0.1", port); else cb.UseSharedMemory(name);
            await using var client = cb.Build();
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(safety.Token);
            var rpc = client.Get<IPreflightRpc>();
            foreach (var timed in new[] { true, false })
            {
                var gate = new Gate();
                BlockingService.Current = gate;
                capture.Current = gate;
                var call = Consume(timed ? rpc.Tracked(1, safety.Token) : rpc.Plain(1));
                try
                {
                    var context = await gate.Context.Task.WaitAsync(safety.Token);
                    var serviceToken = await gate.Entered.Task.WaitAsync(safety.Token);
                    await gate.Blocked.Task.WaitAsync(safety.Token);
                    Check(!call.IsCompleted, "the inspected RPC is still blocked in its synchronous enumerator");
                    var registry = server.GetType().GetField("_connectionRegistry", Instance)!.GetValue(server)!;
                    var connection = ((IEnumerable)Get(registry, "Values")).Cast<object>().Single();
                    var map = Get(connection, "CallCancellations");
                    var activeRequestId = context.RequestId;
                    object?[] lookup = [activeRequestId, null];
                    var found = (bool)map.GetType().GetMethod("TryGetValue", Instance)!.Invoke(map, lookup)!;
                    Console.Error.WriteLine($"transport={transport}, timed={timed}, request={activeRequestId}, found={found}, contextCanBeCanceled={context.CancellationToken.CanBeCanceled}, serviceCanBeCanceled={serviceToken.CanBeCanceled}");
                    Check(found == timed, "request-id lookup must distinguish timed state from untimed no-state control");
                    var connectionToken = (CancellationToken)Get(connection, "ConnectionToken");
                    long? stateRequestId = null;
                    bool? stateTokenMatchesService = null;
                    bool? stateTokenMatchesContext = null;
                    if (found)
                    {
                        var state = lookup[1]!;
                        stateRequestId = (long)Get(state, "RequestId");
                        var stateToken = (CancellationToken)Get(state, "InvocationToken");
                        stateTokenMatchesService = stateToken == serviceToken;
                        stateTokenMatchesContext = stateToken == context.CancellationToken;
                        Check(stateRequestId == activeRequestId, "map entry belongs to the blocked active request");
                        Check(stateToken.CanBeCanceled && stateTokenMatchesService == true && stateTokenMatchesContext == true,
                            "state invocation token must be the token observed by both service and interceptor");
                        Check(stateToken != connectionToken, "per-call token must differ from connection lifetime token");
                    }
                    else
                    {
                        Check(context.CancellationToken == connectionToken, "untimed context uses connection lifetime token");
                        Check(!serviceToken.CanBeCanceled, "noncancellable service has no business cancellation parameter");
                    }
                    rows.Add(new { transport, declaredTimed = timed, activeRequestId, found, stateRequestId,
                        stateTokenMatchesService, stateTokenMatchesContext,
                        contextCanBeCanceled = context.CancellationToken.CanBeCanceled,
                        serviceCanBeCanceled = serviceToken.CanBeCanceled,
                        contextMatchesConnection = context.CancellationToken == connectionToken });
                }
                finally
                {
                    gate.Release.TrySetResult();
                    await call.WaitAsync(safety.Token);
                }
            }
            await client.StopAsync();
            await server.StopAsync(TimeSpan.FromSeconds(10));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { identity = SharpLinkGeneratedManifestVersions.AbiIdentity,
            serverMvid = typeof(SharpLinkServerBuilder).Assembly.ManifestModule.ModuleVersionId, rows },
            new JsonSerializerOptions { WriteIndented = true }));
    }
    private static object Get(object target, string name) => target.GetType().GetProperty(name, Instance)!.GetValue(target)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Consume(IAsyncEnumerable<int> stream)
    {
        var count = 0;
        await foreach (var item in stream) { Check(item == 1, "one real stream response"); count++; }
        Check(count == 1, "one response and successful stream completion");
    }
}
internal sealed class Gate
{
    internal readonly TaskCompletionSource<SharpLinkServerInvocationContext> Context = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<CancellationToken> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
internal sealed class Capture : ISharpLinkServerInterceptor
{
    internal Gate Current = null!;
    public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
    {
        Current.Context.SetResult(context);
        return next(context);
    }
}
[RpcContract]
public interface IPreflightRpc : IService
{
    [NonCancellable] IAsyncEnumerable<int> Plain(int count);
    [Timeout(3600)] IAsyncEnumerable<int> Tracked(int count, CancellationToken cancellationToken);
}
[RpcService]
public sealed class BlockingService : IPreflightRpc
{
    internal static Gate Current = null!;
    public IAsyncEnumerable<int> Plain(int count) { Current.Entered.SetResult(default); return Block(count); }
    public IAsyncEnumerable<int> Tracked(int count, CancellationToken cancellationToken)
    { Current.Entered.SetResult(cancellationToken); return Block(count); }
    private static IAsyncEnumerable<int> Block(int count) => new BlockingRange(count, Current);
    // Match M0's synchronously completing enumerator. An async gate would itself
    // force the server's async-continuation state and invalidate the no-state control.
    private sealed class BlockingRange(int count, Gate gate) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _index;
        public int Current => _index;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken token = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            if (_index == 0)
            {
                gate.Blocked.SetResult();
                if (!gate.Release.Task.Wait(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("Preflight inspector did not release the blocked request.");
            }
            return ValueTask.FromResult(++_index <= count);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
