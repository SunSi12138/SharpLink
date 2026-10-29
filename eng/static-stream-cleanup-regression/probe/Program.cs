using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Server;

// Newly reconstructed ordinary regression, not O203C1 replay or performance evidence.
if (args.Length != 2 || args[0] is not ("static-none" or "static-cancellable" or "dynamic-none" or "static-deadline" or "static-enumerator-only" or "static-none-held") ||
    args[1] is not ("early-break" or "explicit-dispose" or "consumer-throw" or "enumerator-cancel"))
    throw new ArgumentException("CONDITION TERMINATION");
var condition = args[0];
var termination = args[1];
var dynamic = condition == "dynamic-none";
var noMethodToken = condition is "static-deadline" or "static-enumerator-only";
if ((condition == "static-enumerator-only") != (termination == "enumerator-cancel"))
    throw new ArgumentException("Enumerator-only condition requires its distinct enumerator-cancel termination.");
if (condition == "static-none-held" && termination != "explicit-dispose")
    throw new ArgumentException("Held ownership row requires explicit-dispose.");
var guard = TimeSpan.FromSeconds(15);
const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
Assembly LoadModule()
{
    var loaded = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "StaticStreamCleanupModule.dll"));
    RuntimeHelpers.RunModuleConstructor(loaded.ManifestModule.ModuleHandle);
    return loaded;
}
Assembly? module = dynamic ? null : LoadModule();
var serverBuilder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback)
    .UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2));
var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
var server = serverBuilder.Build();
var passThrough = new PassThrough();
var client = SharpClientBuilder.Create().UseTcp("127.0.0.1", port)
    .UseConnectionPool(options => { options.MinConnections = 1; options.MaxConnections = 1; })
    .DisableRequestTimeout().UseHeartbeat(TimeSpan.FromHours(1), TimeSpan.FromHours(2))
    .AddInterceptor(passThrough).Build();
string? failure = null;
var teardownFailures = new List<string>();
object? beforeStop = null;
Func<object>? inspectBeforeStop = null;
Action? releaseHeld = null;
object? heldMoveSnapshot = null, heldDisposeSnapshot = null;
try
{
    if (dynamic)
    {
        module = LoadModule();
        var sr = server.RegisterAssembly(module);
        var cr = client.RegisterAssembly(module);
        if (!sr.Succeeded || !cr.Succeeded)
            throw new InvalidOperationException($"Dynamic registration failed: {sr.Error}; {cr.Error}");
    }
    var manifest = module!.GetCustomAttribute<SharpLinkGeneratedAssemblyManifestAttribute>()
        ?? throw new InvalidOperationException("Current source generator manifest missing.");
    if (manifest.ApiVersion != 5 || manifest.ProtocolVersion != 2)
        throw new InvalidOperationException("Expected new source API5/protocol2, never official API4.");
    await server.StartAsync().AsTask().WaitAsync(guard);
    await client.ConnectAsync().AsTask().WaitAsync(guard);
    var service = module!.GetType("StaticStreamCleanup.CleanupService", true)!;
    var contract = module.GetType("StaticStreamCleanup.ICleanupStream", true)!;
    int Count(string name) => (int)service.GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
    TaskCompletionSource<bool> Signal(string name) => (TaskCompletionSource<bool>)service.GetField(name)!.GetValue(null)!;
    var waiting = Signal("Waiting");
    if (condition == "static-none-held") service.GetField("Hold")!.SetValue(null, true);
    releaseHeld = () => { Signal("WorkRelease").TrySetResult(true); Signal("DisposeRelease").TrySetResult(true); };
    var proxy = typeof(ISharpLinkClient).GetMethod("Get")!.MakeGenericMethod(contract).Invoke(client, null)!;
    var channel = proxy.GetType().GetField("_channel", flags)!.GetValue(proxy)!;
    var expectedChannel = dynamic ? "SharpLink.Client.SharpLinkModuleRpcChannel" : "SharpLink.Client.SharpLinkClient";
    if (channel.GetType().FullName != expectedChannel)
        throw new InvalidOperationException($"Wrong source path: {channel.GetType().FullName}");
    Func<CancellationToken, IAsyncEnumerable<int>> invoke;
    if (noMethodToken)
    {
        var noTokenInvoke = (Func<IAsyncEnumerable<int>>)contract.GetMethod(condition == "static-deadline" ? "ReadDeadlineAsync" : "ReadNoTokenAsync")!
            .CreateDelegate(typeof(Func<IAsyncEnumerable<int>>), proxy);
        invoke = _ => noTokenInvoke(); // Contract genuinely has no CancellationToken parameter.
    }
    else
        invoke = (Func<CancellationToken, IAsyncEnumerable<int>>)contract.GetMethod("ReadAsync")!
            .CreateDelegate(typeof(Func<CancellationToken, IAsyncEnumerable<int>>), proxy);
    using var control = condition == "static-cancellable" ? new CancellationTokenSource() : null;
    var callerToken = control?.Token ?? CancellationToken.None;
    object? connection = null;
    long requestId = 0;
    bool invocationTokenCancellable = false;
    void CaptureRequest()
    {
        var registry = Required(server, "_connectionRegistry");
        var connections = (Array)registry.GetType().GetMethod("SnapshotOwned", flags)!.Invoke(registry, null)!;
        if (connections.Length != 1 || Int(client, "ReadyConnectionCount") != 1)
            throw new InvalidOperationException("Expected exactly one live connection before termination.");
        connection = connections.GetValue(0)!;
        requestId = (long)Required(connection, "_lastAcceptedRequestId");
        if (requestId <= 0) throw new InvalidOperationException("No accepted request identity.");
        var call = InspectCall(connection, requestId);
        if (!call.Present) throw new InvalidOperationException("Active request map entry missing.");
        invocationTokenCancellable = call.Cancellable;
    }
    object Snapshot() => new { started = Count("Started"), completed = Count("Completed"), active = Count("Active"),
        invocationTokenCancellable, argumentCancellable = Count("ArgumentCancellable"), effectiveCancellable = Count("EffectiveCancellable"),
        cancellationObserved = Count("CancellationObserved"), moveNextUnwound = Count("MoveNextUnwound"),
        disposeStarted = Count("DisposeStarted"), disposeCompleted = Count("DisposeCompleted"), overlap = Count("Overlap"),
        inMoveNext = Count("InMoveNext"), serviceDisposed = Count("ServiceDisposed"),
        clientLogical = Int(client, "_activeLogicalInvocations"), clientPending = Int(client, "PendingCallCount"),
        clientCalls = Int(client, "ActiveClientCallCount"), clientStreams = Int(client, "ActiveClientStreamCount"),
        serverCalls = Int(server, "ActiveCallCountForDiagnostics"), serverAdmissions = Int(server, "PendingCallAdmissionsForDiagnostics"),
        callMapEntryPresent = connection is null ? (bool?)null : MapContains(connection, requestId), readyConnections = Int(client, "ReadyConnectionCount") };
    inspectBeforeStop = Snapshot;
    async Task Terminate()
    {
        if (condition == "static-none-held")
        {
            var reader = invoke(CancellationToken.None).GetAsyncEnumerator();
            if (!await reader.MoveNextAsync() || reader.Current != 0)
                throw new InvalidOperationException("Missing ordered first item.");
            await waiting.Task.WaitAsync(guard);
            CaptureRequest();
            await reader.DisposeAsync().AsTask().WaitAsync(guard);
            using var cancellationGuard = new CancellationTokenSource(guard);
            while (Count("CancellationObserved") != 1)
                await Task.Delay(10, cancellationGuard.Token);
            // Public disposal completed, but the uncooperative MoveNext is still physically owned.
            heldMoveSnapshot = Snapshot();
            if (Count("Active") != 1 || Count("Completed") != 0 || Count("InMoveNext") != 1 ||
                Count("DisposeStarted") != 0 || Count("ServiceDisposed") != 0 ||
                Int(server, "ActiveCallCountForDiagnostics") != 1 || Int(client, "ReadyConnectionCount") != 1)
                throw new InvalidOperationException("Held MoveNext lost physical request/service ownership or overlapped disposal.");
            Signal("WorkRelease").TrySetResult(true);
            await Signal("DisposeEntered").Task.WaitAsync(guard);
            heldDisposeSnapshot = Snapshot();
            if (Count("MoveNextUnwound") != 1 || Count("InMoveNext") != 0 || Count("DisposeStarted") != 1 ||
                Count("DisposeCompleted") != 0 || Count("Overlap") != 0 || Count("ServiceDisposed") != 0 ||
                Int(server, "ActiveCallCountForDiagnostics") != 1 || Int(client, "ReadyConnectionCount") != 1)
                throw new InvalidOperationException("Held Dispose lost physical request/service ownership or overlapped MoveNext.");
            Signal("DisposeRelease").TrySetResult(true);
        }
        else if (termination == "enumerator-cancel")
        {
            using var enumerationCancellation = new CancellationTokenSource();
            // Cancellation is supplied only by WithCancellation/GetAsyncEnumerator, never a method argument.
            await using var reader = invoke(CancellationToken.None).WithCancellation(enumerationCancellation.Token).GetAsyncEnumerator();
            if (!await reader.MoveNextAsync() || reader.Current != 0)
                throw new InvalidOperationException("Missing ordered first item.");
            await waiting.Task.WaitAsync(guard);
            CaptureRequest();
            async Task<bool> MoveNext() => await reader.MoveNextAsync();
            var pending = MoveNext();
            enumerationCancellation.Cancel();
            try
            {
                await pending;
                throw new InvalidOperationException("Enumerator-only cancellation did not terminate pending MoveNext.");
            }
            catch (OperationCanceledException) when (enumerationCancellation.IsCancellationRequested) { }
        }
        else if (termination == "explicit-dispose")
        {
            var reader = invoke(callerToken).GetAsyncEnumerator();
            try
            {
                if (!await reader.MoveNextAsync() || reader.Current != 0)
                    throw new InvalidOperationException("Missing ordered first item.");
                await waiting.Task.WaitAsync(guard);
                CaptureRequest();
            }
            finally { await reader.DisposeAsync(); }
        }
        else
        {
            var seen = 0;
            try
            {
                await foreach (var item in invoke(callerToken))
                {
                    if (item != 0 || ++seen != 1) throw new InvalidOperationException("Incorrect first item.");
                    await waiting.Task.WaitAsync(guard);
                    CaptureRequest();
                    if (termination == "consumer-throw") throw new ConsumerFailure();
                    break;
                }
            }
            catch (ConsumerFailure) when (termination == "consumer-throw") { }
            if (seen != 1) throw new InvalidOperationException("Missing first item.");
        }
    }
    await Terminate().WaitAsync(guard);
    // Never stop either endpoint to obtain the cancellation/finally witness.
    using var deadline = new CancellationTokenSource(guard);
    while (Count("ServiceDisposed") != 1 || Count("Completed") != 1 || Count("Active") != 0 || Count("CancellationObserved") != 1 ||
           Count("DisposeCompleted") != 1 || Int(client, "_activeLogicalInvocations") != 0 ||
           Int(client, "PendingCallCount") != 0 || Int(client, "ActiveClientCallCount") != 0 ||
           Int(client, "ActiveClientStreamCount") != 0 || Int(server, "ActiveCallCountForDiagnostics") != 0 ||
           Int(server, "PendingCallAdmissionsForDiagnostics") != 0 || MapContains(connection!, requestId))
        await Task.Delay(10, deadline.Token);
    if (!invocationTokenCancellable || Count("Started") != 1 || Count("ArgumentCancellable") != (noMethodToken ? 0 : 1) || Count("EffectiveCancellable") != 1 ||
        Count("DisposeStarted") != 1 || Count("MoveNextUnwound") != 1 || Count("InMoveNext") != 0 || Count("Overlap") != 0)
        throw new InvalidOperationException("Token ownership / exactly-once sequential producer cleanup assertion failed.");
    if (callerToken.IsCancellationRequested || !passThrough.Observed || Int(client, "ReadyConnectionCount") != 1)
        throw new InvalidOperationException("Control token must not be cancelled, interceptor must run, connection must stay ready.");
    beforeStop = Snapshot();
}
catch (Exception error)
{
    failure = error.ToString(); // Sticky: teardown is never a passing cleanup witness.
    try { beforeStop = inspectBeforeStop?.Invoke(); }
    catch (Exception snapshotError) { Console.Error.WriteLine("Pre-stop snapshot failed: " + snapshotError); }
    Console.Error.WriteLine($"PRE_STOP_FAILURE {condition}/{termination}: {failure}");
}
finally
{
    // Failure rescue only, after the sticky failure and snapshot; never a passing witness.
    releaseHeld?.Invoke();
    async Task Stop(string name, Func<Task> action)
    {
        try { await action().WaitAsync(guard); }
        catch (Exception error) { teardownFailures.Add(name + ": " + error); }
    }
    await Stop("server-stop", () => server.StopAsync(TimeSpan.Zero).AsTask());
    await Stop("client-stop", () => client.StopAsync().AsTask());
    await Stop("server-dispose", () => server.DisposeAsync().AsTask());
    await Stop("client-dispose", () => client.DisposeAsync().AsTask());
}
Console.WriteLine(JsonSerializer.Serialize(new { suite = "new-source-serverstream-cleanup", condition, termination,
    requestTimeout = "disabled", methodHasCancellationToken = !noMethodToken, methodDeadlineSeconds = condition == "static-deadline" ? 3600 : 0, passed = failure is null && teardownFailures.Count == 0, failure, heldMoveSnapshot, heldDisposeSnapshot, beforeStop, teardownFailures,
    coverage = "method token/None, long non-expiring method deadline, enum-only cancellation, static None held MoveNext then held Dispose; no actual expiry/duplex claim", historicalReplay = false }));
return failure is null && teardownFailures.Count == 0 ? 0 : 1;

static object Required(object target, string name)
{
    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    return target.GetType().GetProperty(name, flags)?.GetValue(target)
        ?? target.GetType().GetField(name, flags)?.GetValue(target)
        ?? throw new InvalidOperationException($"Missing inspector {target.GetType().Name}.{name}");
}
static int Int(object target, string name) => (int)Required(target, name);
static bool MapContains(object connection, long requestId) => InspectCall(connection, requestId).Present;
static (bool Present, bool Cancellable) InspectCall(object connection, long requestId)
{
    var map = Required(connection, "CallCancellations");
    // Project only a bool while holding the existing lock; never retain a pooled call-state.
    var state = map.GetType().GetGenericArguments()[0];
    var callbackType = typeof(Func<,,>).MakeGenericType(typeof(long), state, typeof(bool));
    var callback = typeof(Presence).GetMethod(nameof(Presence.Capture))!.CreateDelegate(callbackType);
    object?[] arguments = [requestId, callback, null];
    var capture = map.GetType().GetMethod("TryCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(bool));
    var present = (bool)capture.Invoke(map, arguments)!;
    return (present, present && (bool)arguments[2]!);
}
sealed class ConsumerFailure : Exception { }
sealed class PassThrough : ISharpLinkClientInterceptor
{
    public bool Observed;
    public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
    {
        Observed = true;
        return next(context);
    }
}

static class Presence
{
    public static bool Capture(long requestId, object state)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var token = (CancellationToken)(state.GetType().GetProperty("InvocationToken", flags)?.GetValue(state)
            ?? throw new InvalidOperationException("InvocationToken inspector missing."));
        return token.CanBeCanceled;
    }
}
