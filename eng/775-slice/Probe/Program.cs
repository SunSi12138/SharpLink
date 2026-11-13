using System.Net;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.BindingSlice;

internal static partial class Program
{
    private static readonly List<string> Passed = [];
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Passed.Add(name); Console.WriteLine("PASS " + name);
    }
    private static async Task Expect<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, name); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name);
    }
    private static async Task ExpectCode(Func<Task> action, SharpLinkErrorCode code, string name)
    {
        try { await action(); }
        catch (SharpLinkException failure) when (failure.Code == code) { Check(true, name); return; }
        throw new InvalidOperationException("Expected " + code + ": " + name);
    }

    public static async Task Main(string[] args)
    {
        var transport = args[0];
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await RunAsync(transport, watchdog.Token).WaitAsync(watchdog.Token);
        var report = new { transport, kind = "functional-only", passed = Passed.Count, cases = Passed,
            source = Environment.GetEnvironmentVariable("GITHUB_SHA"), runtime = Environment.Version.ToString() };
        Directory.CreateDirectory("artifacts/775-slice");
        await File.WriteAllTextAsync($"artifacts/775-slice/{transport}.json", JsonSerializer.Serialize(report));
        Console.WriteLine("RESULT " + JsonSerializer.Serialize(report));
    }

    private readonly struct ForbiddenStreamWriter : IRpcClientStreamWriter
    {
        internal static int Starts;
        public ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Starts); throw new InvalidOperationException("Producer must not start."); }
    }

    private static async Task RunAsync(string transport, CancellationToken token)
    {
        var service = new SliceService();
        var manifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Single(m => m.OwnerAssembly == typeof(ISliceRpc).Assembly);
        var contract = manifest.Contracts.Single(c => c.ContractType == typeof(ISliceRpc));
        var echoId = contract.Methods.Single(m => m.Name == nameof(ISliceRpc.EchoAsync)).MethodId;
        var notifyId = contract.Methods.Single(m => m.Name == nameof(ISliceRpc.NotifyAsync)).MethodId;
        var queuedEchoObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedOneWayObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverBuilder = SharpLinkServerBuilder.Create().UseAdmissionControl(o =>
        {
            o.Global.UseConcurrency(1);
            o.MaxQueuedCalls = 8;
            o.MaxQueuedBytes = 1024 * 1024;
            o.MaxQueueDelay = TimeSpan.FromSeconds(10);
            o.QueueOneWayCalls = true;
            o.UsePartition(context =>
            {
                if (Volatile.Read(ref service.Holding) != 0)
                {
                    if (context.MethodId == echoId) queuedEchoObserved.TrySetResult();
                    if (context.MethodId == notifyId) queuedOneWayObserved.TrySetResult();
                }
                return "slice";
            }, p => p.UseConcurrency(16));
        });
        var clientBuilder = SharpClientBuilder.Create().UseRequestTimeout().UseConnectionPool(o => { o.MinConnections = 1; o.MaxConnections = 1; });
        string? sharedName = null;
        if (transport == "tcp")
        {
            serverBuilder.UseTcp(0, IPAddress.Loopback);
            clientBuilder.UseTcp("127.0.0.1", ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port);
        }
        else if (transport == "sharedmemory")
        {
            var name = sharedName = "sharplink-775-slice-" + Guid.NewGuid().ToString("N");
            serverBuilder.UseTransport(new SharedMemoryServerTransportListener(name));
            clientBuilder.UseSharedMemory(name);
        }
        else throw new ArgumentException("Expected tcp or sharedmemory.");
        serverBuilder.ReplaceService<ISliceRpc>(_ => { Interlocked.Increment(ref service.Activations); return service; }, SharpLinkServiceLifetime.Call);
        await using var server = serverBuilder.Build();
        await using var client = (SharpLinkClient)clientBuilder.Build();
        var publicClient = (ISharpLinkClient)client;
        await CheckBindingTableAsync(server, contract);
        Check(typeof(ISharpLinkClient).GetMethods().All(m => m.Name is not ("Get" or "GetWithMetadata")), "no legacy factories");
        Check(!typeof(IRpcContractChannel).IsAssignableFrom(client.GetType()), "client is not a raw channel");
        Check(typeof(IRpcContractChannel).Assembly.GetType("SharpLink.Abstractions.IRpcChannel") is null, "no legacy channel type");
        var local = publicClient.GetLocalContract<ISliceRpc>();
        var rpc = publicClient.CreateProxy<ISliceRpc>();
        var manual = publicClient.CreateContractChannel(local);
        var site = (StaticBindingCallSite)manual;
        Check(ReferenceEquals(site.RuntimeContext, publicClient.RuntimeContext), "runtime context without raw cast");
        Check(site.Snapshot.Length == 0 && client.ReadyConnectionCount == 0, "factory local only before connect");
        await ExpectCode(async () => await rpc.Service.EchoAsync(new SlicePayload(7)), SharpLinkErrorCode.Unavailable, "unbound failfast");
        Check(SlicePayloadCodec.Serialized == 0, "unbound call does not serialize");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Expect<ArgumentOutOfRangeException>(() => rpc.PrepareAsync(TimeSpan.Zero, canceled.Token).AsTask(), "invalid prepare budget precedes cancellation");
            await Expect<OperationCanceledException>(() => rpc.PrepareAsync(TimeSpan.FromSeconds(1), canceled.Token).AsTask(), "prepare caller cancellation");
        }
        await ExpectCode(() => rpc.PrepareAsync(TimeSpan.FromMilliseconds(10), token).AsTask(),
            SharpLinkErrorCode.DeadlineExceeded, "prepare has finite budget before connect");
        var waiter = rpc.PrepareAsync(TimeSpan.FromSeconds(15), token).AsTask();
        using var oneWaiter = new CancellationTokenSource();
        var canceledWaiter = manual.PrepareAsync(TimeSpan.FromSeconds(15), oneWaiter.Token).AsTask();
        oneWaiter.Cancel();
        await Expect<OperationCanceledException>(() => canceledWaiter, "individual waiter canceled");
        Check(!waiter.IsCompleted, "other waiter still pending");
        await server.StartAsync(token);
        await client.ConnectAsync(token);
        await waiter;
        Check(site.Snapshot.Length > 0, "before-connect prepare reaches real BindAck");
        Check(service.Activations == 0, "Bind does not activate service");
        var target = site.Snapshot.Single();
        var sent = target.Connection.StaticBindings.Sent;
        var ready = rpc.PrepareAsync(TimeSpan.FromSeconds(1), token);
        Check(ready.IsCompletedSuccessfully, "already-ready prepare synchronous");
        await ready;
        Check(target.Connection.StaticBindings.Sent == sent && sent == 1, "same site and coalesced Bind");
        var metadataProxy = publicClient.CreateProxy<ISliceRpc>(new RpcProxyOptions
        { Metadata = new SharpLinkMetadata([new KeyValuePair<string, string>("slice", "captured")]) });
        await metadataProxy.PrepareAsync(TimeSpan.FromSeconds(1), token);
        Check(await metadataProxy.Service.MetadataAsync() == "captured", "metadata wrapper preserves exact site");
        Check(await rpc.Service.PingAsync() == 42, "generated unary over bound transport");
        Check(await rpc.Service.EchoAsync(new SlicePayload(91)) == 91, "generated custom codec");
        await rpc.Service.NotifyAsync(17);
        Check(await service.Notification.Task.WaitAsync(token) == 17, "OneWay receiver business completion");
        var ping = contract.Methods.Single(m => m.Name == nameof(ISliceRpc.PingAsync));
        var descriptor = new RpcMethodDescriptor(contract.ContractId, ping.MethodId, RpcMethodKind.Unary, true, false, false, null);
        var intCodec = publicClient.RuntimeContext.Codecs.GetCodec<int>();
        Check(await manual.InvokeUnaryAsync(descriptor, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance, intCodec, null, token) == 42,
            "handwritten scoped channel follows same route");
        var count = service.Invocations;
        var decoded = SlicePayloadCodec.Deserialized;
        var activations = service.Activations;
        await ExpectCode(async () => await manual.InvokeUnaryAsync(descriptor with { MethodId = long.MaxValue },
            new SlicePayload(3), new SlicePayloadCodec(), intCodec, null, token), SharpLinkErrorCode.Unimplemented, "unknown method rejected");
        Check(service.Invocations == count && service.Activations == activations && SlicePayloadCodec.Deserialized == decoded, "unknown method no business decode or activation");
        var serialized = SlicePayloadCodec.Serialized;
        await ExpectCode(async () => await manual.InvokeUnaryAsync(descriptor with { ContractId = long.MaxValue },
            new SlicePayload(3), new SlicePayloadCodec(), intCodec, null, token), SharpLinkErrorCode.FailedPrecondition, "receiver contract mismatch");
        Check(SlicePayloadCodec.Serialized == serialized, "local identity rejection before serialization");
        while (true)
        {
            var changed = (TaskCompletionSource)typeof(SharpLinkClient).GetField("_staticBindingChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
            if (Volatile.Read(ref site.RefreshState) == 0) break;
            await changed.Task.WaitAsync(token);
        }
        site.Snapshot = [target with { Token = target.Token ^ (1UL << 48) }];
        try
        {
            await ExpectCode(async () => await manual.InvokeUnaryAsync(descriptor, new SlicePayload(3), new SlicePayloadCodec(), intCodec, null, token),
                SharpLinkErrorCode.FailedPrecondition, "wrong full token rejected");
            Check(service.Invocations == count && service.Activations == activations && SlicePayloadCodec.Deserialized == decoded, "wrong token no business decode or activation");
        }
        finally { site.Snapshot = [target]; }
        var otherBuilder = SharpClientBuilder.Create().UseRequestTimeout().UseConnectionPool(o => { o.MinConnections = 1; o.MaxConnections = 1; });
        if (transport == "tcp") otherBuilder.UseTcp("127.0.0.1", ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port);
        else otherBuilder.UseSharedMemory(sharedName!);
        await using var other = (SharpLinkClient)otherBuilder.Build();
        await ExpectCode(() => { other.CreateContractChannel(local); return Task.CompletedTask; },
            SharpLinkErrorCode.FailedPrecondition, "foreign issuer rejected locally");
        await ExpectCode(() => { publicClient.GetLocalContract(typeof(string).Assembly, contract.ContractId); return Task.CompletedTask; },
            SharpLinkErrorCode.FailedPrecondition, "wrong local assembly cannot substitute contract");
        var otherProxy = other.CreateProxy<ISliceRpc>();
        var otherSite = (StaticBindingCallSite)other.CreateContractChannel(other.GetLocalContract<ISliceRpc>());
        await other.ConnectAsync(token);
        await otherProxy.PrepareAsync(TimeSpan.FromSeconds(10), token);
        var otherTarget = otherSite.Snapshot.Single();
        Check(target.Token == otherTarget.Token && !ReferenceEquals(target.Connection.Session, otherTarget.Connection.Session),
            "same numeric token belongs to distinct physical Sessions");
        Check(await rpc.Service.SessionAsync() != await otherProxy.Service.SessionAsync(), "scoped calls stay on their own Session");
        var writer = new ForbiddenStreamWriter();
        await ExpectCode(() => manual.InvokeClientStreamingAsync(descriptor, default(RpcEmptyRequest),
            RpcEmptyRequestCodec.Instance, intCodec, writer, null, token).AsTask(),
            SharpLinkErrorCode.FailedPrecondition, "unsupported client streaming fails closed");
        Check(ForbiddenStreamWriter.Starts == 0, "unsupported family never starts producer");
        using var call = new CancellationTokenSource();
        var held = rpc.Service.HoldAsync(call.Token).AsTask();
        await service.Held.Task.WaitAsync(token);
        var beforeQueuedDecode = SlicePayloadCodec.Deserialized;
        var queuedEcho = rpc.Service.EchoAsync(new SlicePayload(123)).AsTask();
        await queuedEchoObserved.Task.WaitAsync(token);
        Check(!queuedEcho.IsCompleted && SlicePayloadCodec.Deserialized == beforeQueuedDecode, "queued unary waits before business decode");
        await rpc.Service.NotifyAsync(99);
        await queuedOneWayObserved.Task.WaitAsync(token);
        Check(!service.QueuedNotification.Task.IsCompleted, "queued OneWay waits before business execution");
        call.Cancel();
        await Expect<OperationCanceledException>(() => held, "bound unary caller cancellation");
        Check(await queuedEcho.WaitAsync(token) == 123, "queued unary resumes captured bound pipeline");
        Check(await service.QueuedNotification.Task.WaitAsync(token) == 99, "queued OneWay resumes captured bound pipeline");
        await service.Exited.Task.WaitAsync(token);
        Check(target.Connection.StaticBindings.PendingCount == 0, "Bind operations reclaimed");
        await other.StopAsync(token);
        await client.StopAsync(token);
        var drained = client.FrameworkTaskSnapshotForDiagnostics;
        Check(drained.IsDrained && drained.ActiveTasks == 0 && drained.RejectedRegistrations == 0,
            "framework supervisor drained without rejected work");
        await ExpectCode(() => rpc.PrepareAsync(TimeSpan.FromSeconds(1), token).AsTask(), SharpLinkErrorCode.ConnectionClosed, "prepare wakes or rejects after stop");
    }
}
