using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.BindingTests;

internal static partial class Program
{
    private static async Task RunQuotaControlsAsync(string transport, CancellationToken token)
    {
        await ClientOperationsQuotaAsync(transport, token);
        await BindingQuotasAsync(transport, token);
        await ServerOperationsQuotaAsync(transport, token);
        await PrepareQuotaAsync(transport, token);
    }

    private static async Task ClientOperationsQuotaAsync(string transport, CancellationToken token)
    {
        await using var f = new ControlFixture(transport, connections: 2);
        await f.ConnectAsync(token);
        var connections = f.Connections;
        var pending = new List<Task<StaticBindingAck>>();
        var entered = new[] { Signal(), Signal() };
        for (var index = 0; index < connections.Length; index++)
        {
            var i = index;
            connections[i].StaticBindings.PublicationTestHook = async (_, phase, ct) =>
            {
                if (phase == StaticBindingPublicationPhase.BeforeSend)
                { entered[i].TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            };
            for (var method = 1; method <= 32; method++)
                pending.Add(connections[i].StaticBindings.BindAsync(-method, f.Hash));
        }
        await Task.WhenAll(entered.Select(signal => signal.Task)).WaitAsync(token);
        Check(f.Client.BindingBudget.Operations == 64 && f.Client.BindingBudget.Bindings == 64 &&
            connections.Sum(c => c.StaticBindings.PendingCount) == 64, "control operation quota is shared across physical client Sessions");
        Check(ReferenceEquals(pending[0], connections[0].StaticBindings.BindAsync(-1, f.Hash)) &&
            f.Client.BindingBudget.Operations == 64, "control coalescing at full quota needs no extra reservation");
        await ExpectCode(() => connections[0].StaticBindings.BindAsync(-1000, f.Hash), SharpLinkErrorCode.ResourceExhausted,
            "control sixty-fifth global client operation is rejected");
        Check(f.Client.BindingBudget.Bindings == 64 && f.Client.BindingBudget.Operations == 64,
            "control rejected operation rolls back provisional binding reservation");

        await using (var independent = new ControlFixture(transport))
        {
            await independent.ConnectAsync(token);
            await independent.Connections.Single().StaticBindings.BindAsync(independent.Contract.ContractId, independent.Hash).WaitAsync(token);
            Check(independent.Client.BindingBudget.Bindings == 1 && f.Client.BindingBudget.Operations == 64,
                "control another client progresses while first client quota is full");
        }

        await connections[0].StaticBindings.CloseAsync().WaitAsync(token);
        foreach (var task in pending.Take(32))
        {
            try { await task; }
            catch (SharpLinkException e) when (e.Code == SharpLinkErrorCode.Unavailable) { continue; }
            throw new InvalidOperationException("Sealed operation must fail Unavailable.");
        }
        Check(f.Client.BindingBudget.Operations == 32 && f.Client.BindingBudget.Bindings == 32 &&
            connections[1].StaticBindings.PendingCount == 32, "control one Session cleanup cannot release another Session quotas");
        await connections[1].StaticBindings.CloseAsync().WaitAsync(token);
        foreach (var task in pending.Skip(32))
        {
            try { await task; }
            catch (SharpLinkException e) when (e.Code == SharpLinkErrorCode.Unavailable) { continue; }
            throw new InvalidOperationException("Sealed operation must fail Unavailable.");
        }
        Check(f.Client.BindingBudget.Operations == 0 && f.Client.BindingBudget.Bindings == 0 &&
            connections.All(c => c.StaticBindings.PendingCount == 0), "control global client operation cleanup returns every reservation");
    }

    private static async Task BindingQuotasAsync(string transport, CancellationToken token)
    {
        await using var f = new ControlFixture(transport, connections: 2);
        await f.ConnectAsync(token);
        var connections = f.Connections;
        // Controlled occupancy uses real quota leases, then tests the final slot on real Bind.
        var clientFill = Enumerable.Range(0, 127).Select(_ => f.Client.BindingBudget.TryReserveBinding()!).ToArray();
        if (clientFill.Any(lease => lease is null)) throw new InvalidOperationException("Client filler must fit.");
        try
        {
            await connections[0].StaticBindings.BindAsync(f.Contract.ContractId, f.Hash).WaitAsync(token);
            Check(f.Client.BindingBudget.Bindings == 128, "control real Bind consumes final owner-wide client binding slot");
            await ExpectCode(() => connections[1].StaticBindings.BindAsync(f.Contract.ContractId, f.Hash),
                SharpLinkErrorCode.ResourceExhausted, "control another Session cannot exceed client binding cap");
            Check(connections[1].StaticBindings.Sent == 0 && f.Client.BindingBudget.Operations == 0,
                "control client binding refusal emits no control bytes");
        }
        finally { foreach (var lease in clientFill) lease.Dispose(); }
        await f.Client.StopAsync(token);
        await f.Server.StopAsync(TimeSpan.FromSeconds(5), token);
        Check(f.Client.BindingBudget.Bindings == 0 && f.Server.BindingBudget.Bindings == 0,
            "control connection shutdown returns both owners completed binding slots");

        await using var serverFixture = new ControlFixture(transport, connections: 2);
        await serverFixture.ConnectAsync(token);
        var registry = Field<ServerServiceModuleRegistry>(serverFixture.Server, "_serviceModuleRegistry");
        var registration = registry.ServicesStorage[serverFixture.Contract.ContractId];
        var tables = new[] { new StaticBindingTable(serverFixture.Server.BindingBudget), new StaticBindingTable(serverFixture.Server.BindingBudget) };
        try
        {
            for (var i = 0; i < 127; i++)
                if (tables[i / 64].Install(serverFixture.Contract.ContractId, registration, serverFixture.Contract, 0).Failure != StaticBindingFailure.None)
                    throw new InvalidOperationException("Server filler must fit.");
            var live = serverFixture.Connections;
            await live[0].StaticBindings.BindAsync(serverFixture.Contract.ContractId, serverFixture.Hash).WaitAsync(token);
            Check(serverFixture.Server.BindingBudget.Bindings == 128, "control real Bind consumes final owner-wide server binding slot");
            await ExpectCode(() => live[1].StaticBindings.BindAsync(serverFixture.Contract.ContractId, serverFixture.Hash),
                SharpLinkErrorCode.ResourceExhausted, "control server global binding refusal crosses real Ack boundary");
            Check(serverFixture.Server.BindingBudget.Bindings == 128 && live[1].StaticBindings.CompletedCount == 0 &&
                serverFixture.Service.Activations == 0, "control capacity Ack cannot activate service or publish failed route");
            tables[0].Close(); tables[0].Close(); // Exact-once release is tested, not assumed.
            await live[1].StaticBindings.BindAsync(serverFixture.Contract.ContractId, serverFixture.Hash).WaitAsync(token);
            Check(serverFixture.Server.BindingBudget.Bindings == 65 && live[1].StaticBindings.CompletedCount == 1,
                "control releasing one table enables new Bind without double release");
        }
        finally { foreach (var table in tables) table.Close(); }
        await serverFixture.Client.StopAsync(token);
        await serverFixture.Server.StopAsync(TimeSpan.FromSeconds(5), token);
        Check(serverFixture.Server.BindingBudget.Bindings == 0 && serverFixture.Server.BindingBudget.Operations == 0,
            "control server shutdown reclaims aggregate binding and Ack quotas");
    }

    private static async Task ServerOperationsQuotaAsync(string transport, CancellationToken token)
    {
        await using var f = new ControlFixture(transport);
        await f.ConnectAsync(token);
        // Occupy the exact shared server owner, without allocating 64 artificial transports.
        var occupancy = Enumerable.Range(0, 64).Select(_ => f.Server.BindingBudget.TryReserveOperation()!).ToArray();
        try
        {
            var binding = f.Connections.Single().StaticBindings.BindAsync(f.Contract.ContractId, f.Hash);
            await ExpectCode(() => binding, SharpLinkErrorCode.ResourceExhausted,
                "control exhausted server control quota sends bounded capacity Ack");
            Check(f.Server.BindingBudget.Operations == 64 && f.Server.BindingBudget.Bindings == 0 &&
                f.Service.Activations == 0, "control server control exhaustion retains bounded occupancy and zero business work");
        }
        finally { foreach (var lease in occupancy) lease.Dispose(); }
        await f.Client.StopAsync(token);
        await f.Server.StopAsync(TimeSpan.FromSeconds(5), token);
        Check(f.Server.BindingBudget.Operations == 0 && f.Client.BindingBudget.Operations == 0 &&
            f.Client.BindingBudget.Bindings == 0, "control capacity-refused Session returns client pending control quota");
    }

    private static async Task PrepareQuotaAsync(string transport, CancellationToken token)
    {
        await using var f = new ControlFixture(transport);
        var first = f.Client.CreateProxy<ISliceRpc>();
        var second = f.Client.CreateProxy<IQuotaRpc>();
        using var waiters = new CancellationTokenSource();
        var pending = Enumerable.Range(0, 64).Select(i =>
            (i % 2 == 0 ? first.PrepareAsync(TimeSpan.FromSeconds(20), waiters.Token) :
                second.PrepareAsync(TimeSpan.FromSeconds(20), waiters.Token)).AsTask()).ToArray();
        await ExpectCode(() => first.PrepareAsync(TimeSpan.FromSeconds(20), token).AsTask(), SharpLinkErrorCode.ResourceExhausted,
            "control Prepare waiter quota is shared across distinct sites");
        waiters.Cancel();
        foreach (var task in pending)
        {
            try { await task; }
            catch (OperationCanceledException) { continue; }
            throw new InvalidOperationException("Canceled Prepare must cancel.");
        }
        var recovered = first.PrepareAsync(TimeSpan.FromSeconds(20), token).AsTask();
        Check(!recovered.IsCompleted, "control canceled Prepare waiters return global capacity");
        await f.ConnectAsync(token);
        await recovered.WaitAsync(token);
        Check(await first.Service.PingAsync() == 42, "control recovered Prepare quota reaches actual generated RPC");
    }
}

