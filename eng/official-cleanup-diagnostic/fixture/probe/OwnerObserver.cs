using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal sealed class OwnerObserver(object client, object server, Type service, string output) : IDisposable
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly object gate = new();
    private readonly StreamWriter writer = new(new FileStream(output + ".stages.jsonl", FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    private int sequence;
    private CancellationToken argumentToken;
    private CancellationToken effectiveToken;
    private object? knownConnection;
    private readonly Dictionary<int, (CancellationToken Argument, CancellationToken Effective)> tokensByScenario = new();
    private readonly Dictionary<int, long> requestsByScenario = new();
    private int snapshotScenario;
    public string? ObservationFailure { get; private set; }
    public int Scenario { get; set; }

    public void ServiceEvent(int scenario, string stage, CancellationToken argument, CancellationToken effective)
    {
        // Observers never replace an RPC result or interfere with cancellation callbacks.
        try
        {
            lock (gate)
            {
                tokensByScenario[scenario] = (argument, effective);
                WriteCore(stage, scenario, null);
            }
        }
        catch (Exception error) { ObservationFailure ??= error.ToString(); }
    }

    private static object? Get(object target, string name)
        => target.GetType().GetProperty(name, Flags)?.GetValue(target)
            ?? target.GetType().GetField(name, Flags)?.GetValue(target);
    private static object Required(object target, string name)
        => Get(target, name) ?? throw new InvalidOperationException($"Missing owner member {target.GetType().FullName}.{name}");
    private int Counter(string name) => (int)service.GetField(name, BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
    public int LogicalCount => (int)Required(client, "_activeLogicalInvocations");
    public int Started => Counter("Started");
    public int Completed => Counter("Completed");
    public int Active => Counter("Active");

    private object Token(CancellationToken token) => new
    {
        canBeCanceled = token.CanBeCanceled, isCancellationRequested = token.IsCancellationRequested,
        sourceIdentity = typeof(CancellationToken).GetField("_source", Flags)?.GetValue(token) is { } source
            ? RuntimeHelpers.GetHashCode(source) : (int?)null
    };

    // Runs while the product's existing stripe lock protects the map entry. Return only
    // immutable scalar projections; never retain or mutate a pooled call-state reference.
    private Dictionary<string, object?> CaptureCall(long requestId, object state)
    {
        var token = (CancellationToken)Required(state, "InvocationToken");
        return new()
        {
            ["requestId"] = requestId,
            ["stateRequestId"] = Required(state, "RequestId"),
            ["generation"] = Required(state, "_leaseGeneration"),
            ["stateType"] = state.GetType().FullName,
            ["reason"] = Required(state, "Reason").ToString(),
            ["isAbandoned"] = Required(state, "IsAbandoned"),
            ["hasInvocationCancellationSource"] = Get(state, "_invocationCancellation") is not null,
            ["invocationToken"] = Token(token),
            ["argumentEqualsInvocationToken"] = argumentToken.Equals(token),
            ["effectiveEqualsInvocationToken"] = effectiveToken.Equals(token)
        };
    }

    private object Owners()
    {
        var registry = Required(server, "_connectionRegistry");
        var connections = (Array)registry.GetType().GetMethod("SnapshotOwned", Flags)!.Invoke(registry, null)!;
        if (connections.Length > 1) throw new InvalidOperationException("Expected one physical server connection.");
        if (connections.Length == 1) knownConnection = connections.GetValue(0);
        object? call = null;
        bool? present = null;
        long? lastRequest = null, latestAccepted = null;
        object? connectionTokenData = null;
        bool? argumentEqualsConnection = null, effectiveEqualsConnection = null;
        if (knownConnection is { } connection)
        {
            var connectionToken = (CancellationToken)Required(connection, "ConnectionToken");
            connectionTokenData = Token(connectionToken);
            argumentEqualsConnection = argumentToken.Equals(connectionToken);
            effectiveEqualsConnection = effectiveToken.Equals(connectionToken);
            var latest = (long)Required(connection, "_lastAcceptedRequestId");
            latestAccepted = latest;
            if (!requestsByScenario.ContainsKey(snapshotScenario) && tokensByScenario.ContainsKey(snapshotScenario))
                requestsByScenario[snapshotScenario] = latest;
            lastRequest = requestsByScenario.TryGetValue(snapshotScenario, out var observedRequest) ? observedRequest : latest;
            var map = Required(connection, "CallCancellations");
            var stateType = map.GetType().GetGenericArguments()[0];
            var callbackType = typeof(Func<,,>).MakeGenericType(typeof(long), stateType, typeof(Dictionary<string, object?>));
            var callback = GetType().GetMethod(nameof(CaptureCall), Flags)!.CreateDelegate(callbackType, this);
            var method = map.GetType().GetMethod("TryCapture", Flags)!.MakeGenericMethod(typeof(Dictionary<string, object?>));
            object?[] arguments = [lastRequest.Value, callback, null];
            present = (bool)method.Invoke(map, arguments)!;
            call = arguments[2];
        }
        return new
        {
            clientType = client.GetType().FullName, serverType = server.GetType().FullName,
            serverOwnedConnections = connections.Length,
            clientPendingCalls = Required(client, "PendingCallCount"),
            clientActiveCalls = Required(client, "ActiveClientCallCount"),
            clientActiveStreams = Required(client, "ActiveClientStreamCount"),
            serverActiveCalls = Required(server, "ActiveCallCountForDiagnostics"),
            serverPendingAdmissions = Required(server, "PendingCallAdmissionsForDiagnostics"),
            serverConnectionType = knownConnection?.GetType().FullName,
            lastAcceptedRequestId = latestAccepted, scenarioRequestId = lastRequest, requestIdSource = requestsByScenario.ContainsKey(snapshotScenario) ? "service-entry-last-accepted-single-caller" : "latest-accepted-before-service-entry", callMapEntryPresent = present,
            connectionToken = connectionTokenData, argumentEqualsConnectionToken = argumentEqualsConnection,
            effectiveEqualsConnectionToken = effectiveEqualsConnection, callState = call,
            serviceArgumentToken = Token(argumentToken), serviceEffectiveToken = Token(effectiveToken)
        };
    }

    public void SafeWrite(string stage, object? detail = null)
    {
        try { Write(stage, detail); }
        catch (Exception error) { ObservationFailure ??= error.ToString(); }
    }

    public void Write(string stage, object? detail = null)
    {
        lock (gate) WriteCore(stage, Scenario, detail);
    }

    private void WriteCore(string stage, int scenario, object? detail)
    {
        snapshotScenario = scenario;
        var tokens = tokensByScenario.GetValueOrDefault(scenario);
        argumentToken = tokens.Argument;
        effectiveToken = tokens.Effective;
        object? owners;
        try { owners = Owners(); }
        catch (Exception error)
        {
            ObservationFailure ??= error.ToString();
            owners = new { observationError = error.ToString() };
        }
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            sequence = ++sequence, timestampUtc = DateTimeOffset.UtcNow, scenario, stage,
            Started, Completed, Active, clientLogical = LogicalCount, owners, detail
        }));
    }

    public void Dispose() { lock (gate) writer.Dispose(); }
}
