using System.Reflection;

// Only used outside warmup and measurement. The shared inspectors below were
// checked in both pinned product sources; absence is a hard failure, not zero.
internal sealed class NormalChecks
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);
    private readonly object _client;
    private readonly object _server;
    private readonly Type _service;
    private readonly Func<int> _logicalCount;
    private readonly PropertyInfo _activeClientCalls;
    private readonly PropertyInfo _activeClientStreams;
    private readonly PropertyInfo _pendingCalls;
    private readonly PropertyInfo _activeServerCalls;

    internal NormalChecks(object client, object server, Type service, Func<int> logicalCount)
    {
        _client = client;
        _server = server;
        _service = service;
        _logicalCount = logicalCount;
        _activeClientCalls = Inspector(client, "ActiveClientCallCount");
        _activeClientStreams = Inspector(client, "ActiveClientStreamCount");
        _pendingCalls = Inspector(client, "PendingCallCount");
        _activeServerCalls = Inspector(server, "ActiveCallCountForDiagnostics");
    }

    private static PropertyInfo Inspector(object owner, string name)
        => owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
           ?? throw new InvalidOperationException($"Required normal drain inspector missing: {name}.");

    private int ReadCounter(string name)
        => (int)(_service.GetField(name, BindingFlags.Public | BindingFlags.Static)
           ?? throw new InvalidOperationException($"Required normal service counter missing: {name}."))
           .GetValue(null)!;

    private DrainSnapshot Snapshot() => new(
        _logicalCount(), (int)_activeClientCalls.GetValue(_client)!,
        (int)_activeClientStreams.GetValue(_client)!, (int)_pendingCalls.GetValue(_client)!,
        (int)_activeServerCalls.GetValue(_server)!, ReadCounter("NormalStarted"),
        ReadCounter("NormalCompleted"), ReadCounter("NormalActive"));

    internal async Task<DrainSnapshot> WaitForDrain(int expectedCalls)
    {
        using var deadline = new CancellationTokenSource(Guard);
        while (true)
        {
            var snapshot = Snapshot();
            if (snapshot == new DrainSnapshot(0, 0, 0, 0, 0, expectedCalls, expectedCalls, 0))
                return snapshot;
            await Task.Delay(10, deadline.Token).ConfigureAwait(false);
        }
    }

    internal async Task<(Dictionary<string, bool> Proof, DrainSnapshot Snapshot)> Run(
        Func<int, CancellationToken, IAsyncEnumerable<int>> invoke, int expectedItems, int expectedCalls)
    {
        if (ReadCounter("NormalStarted") != expectedCalls - 1 || ReadCounter("NormalCompleted") != expectedCalls - 1 ||
            ReadCounter("NormalActive") != 0)
            throw new InvalidOperationException("Normal controls must run exactly in their prescribed pre/post order.");
        async Task CompleteNormally()
        {
            var count = 0;
            long sum = 0;
            await foreach (var value in invoke(expectedItems, CancellationToken.None).ConfigureAwait(false))
            {
                if (value != count) throw new InvalidOperationException("Normal control item order mismatch.");
                count++;
                sum += value;
            }
            if (count != expectedItems || sum != (long)expectedItems * (expectedItems - 1) / 2)
                throw new InvalidOperationException("Normal control item count/checksum mismatch.");
        }

        await CompleteNormally().WaitAsync(Guard).ConfigureAwait(false);
        var snapshot = await WaitForDrain(expectedCalls).ConfigureAwait(false);
        return (new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["orderedValues"] = true, ["count"] = true, ["producerFinally"] = true,
            ["serverCleanup"] = true, ["clientCleanup"] = true, ["physicalDrain"] = true
        }, snapshot);
    }

    // This proves this fixture's producer and logical/call/stream/pending owners
    // have drained. It is not a general module-unload or arbitrary user-work gate.
    internal sealed record DrainSnapshot(
        int logicalInvocations, int activeClientCalls, int activeClientStreams,
        int pendingCalls, int activeServerCalls, int normalStarted,
        int normalCompleted, int normalActive);
}
