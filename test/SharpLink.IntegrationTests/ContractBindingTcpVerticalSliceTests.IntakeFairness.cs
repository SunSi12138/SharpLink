using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class ContractBindingTcpVerticalSliceTests
{
    [Test]
    public async Task GeneratedBusinessCallsAreServedWhileLargeTableMaintenanceKeepsBecomingDirty()
    {
        await using var harness = new Harness();
        var captured = new TaskCompletionSource<(ServerConnectionState Connection, Protocol3BindResult Result)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.BeforeBindingResultForTesting = (connection, result) =>
        { captured.TrySetResult((connection, result)); return Task.CompletedTask; };
        var proxy = harness.Client.CreateProxy<IBindingVerticalService>();
        await harness.ConnectAsync();
        await proxy.PrepareAsync(TimeSpan.FromSeconds(5));
        var (connection, result) = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(result.Status == Protocol3BindStatus.Success, "actual generated TCP Bind succeeded");
        var operations = typeof(ServerConnectionState).GetField("_bindingOperations", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while ((int)operations.GetValue(connection)! != 0) await Task.Delay(1, timeout.Token);
        var table = connection.Bindings;
        var registration = table.Capture(result.Token)!.Registration;
        // Cold component setup installs >256 exact entries on the already negotiated
        // actual TCP Session. No registry changes or synthetic dispatch loop are used.
        for (ulong id = 2; id <= 600; id++)
        {
            table.ObserveBindId(id);
            Ensure(table.TryBind(id, registration, out _) == Protocol3BindStatus.Success,
                "large exact bound table setup");
            table.CompleteBindId(id);
        }
        var started = NewGate();
        var slices = 0;
        var dirty = 1;
        connection.BindingMaintenanceSliceCompletedForTesting = () =>
        {
            if (Interlocked.Increment(ref slices) >= 2) started.TrySetResult();
            if (Volatile.Read(ref dirty) != 0) connection.RequestBindingMaintenance();
        };
        connection.RequestBindingMaintenance();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var value = 0; value < 4; value++)
                Ensure(await proxy.Service.EchoAsync(value).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == value + 1,
                    "actual generated business frame is served despite continuously dirty maintenance");
            Ensure(harness.Service.Calls == 4 && connection.Session.IsConnected,
                "finite successful business service without stopping maintenance first");
        }
        finally
        {
            Volatile.Write(ref dirty, 0);
            connection.BindingMaintenanceSliceCompletedForTesting = null;
        }
    }
}
