using System.Collections;
using System.Reflection;

namespace SharpLink.IntegrationTests;

// Observes the actual connected sessions without replacing the flow controller or its scheduling.
internal sealed class OneWayDrainObservation(ISharpLinkClient client, ISharpLinkServer server)
{
    private readonly ClientConnection _connection = ReadyConnection(client);
    private readonly ServerConnectionState _peer =
        ServerRegistryTestAccessor.ActiveConnections((SharpLinkServer)server).Values.Single();

    private StreamFlowController Flow => Field<RpcSessionProtocolState>(_connection.Session, "_protocolState")
        .FlowController ?? throw new InvalidOperationException("The TCP session did not negotiate stream credit.");

    internal Task WaitForCreditBlockAsync(TimeSpan timeout)
    {
        var flow = Flow;
        var gate = Field<Lock>(flow, "_gate");
        var waiters = Field<ICollection>(flow, "_waiters");
        return WaitUntilAsync(() =>
        {
            lock (gate)
                return waiters.Count == 1;
        }, timeout, "The OneWay producer never blocked on SendCredit.");
    }

    internal async Task WaitForDrainAsync(TimeSpan timeout)
    {
        var flow = Flow;
        var window = _connection.Session.NegotiatedOptions!.ConnectionReceiveWindowBytes;
        await WaitUntilAsync(() => flow.SendConnectionCredit == window && flow.RetainedSendStreamCount == 0 &&
            _connection.PendingCalls.NStudyProducerCount == 0 && _peer.Session.StreamManager.ActiveStreamCount == 0, timeout,
            "The OneWay producer, credit and peer stream route did not drain.");
        if (!ReferenceEquals(ReadyConnection(client), _connection))
            throw new InvalidOperationException("Stream drain must leave the original TCP connection usable.");
    }

    private static ClientConnection ReadyConnection(ISharpLinkClient client) =>
        Field<ClientConnection[]>(client, "_readyConnections").Single();

    private static T Field<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new InvalidOperationException($"Missing diagnostic {instance.GetType().Name}.{name}."));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failure)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) >= timeout)
                throw new TimeoutException(failure);
            await Task.Delay(10);
        }
    }
}
