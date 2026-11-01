namespace SharpLink.IntegrationTests;

public sealed partial class ContractBindingTcpVerticalSliceTests
{
    [Test]
    public async Task FailedBoundResultRetainsEntryOwnerUntilTheActualTcpIntakeFinally()
    {
        await using var harness = new Harness();
        var selected = new TaskCompletionSource<(ServerConnectionState Connection, ServerDeclaredMethodFacts Facts)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = NewGate();
        harness.Server.BeforeBindingResultForTesting = (connection, result) =>
        {
            Ensure(result.Status == Protocol3BindStatus.Success, "the real Bind installed a successful exact slot");
            var entry = connection.Bindings.Capture(result.Token)!;
            selected.TrySetResult((connection, entry.Registration.Facts!));
            return release.Task;
        };
        var proxy = harness.Client.CreateProxy<IBindingVerticalService>();
        await harness.ConnectAsync();
        var preparation = proxy.PrepareAsync(TimeSpan.FromSeconds(10)).AsTask();
        try
        {
            var (connection, facts) = await selected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(facts.OwnerCountForTesting == 2, "service plus one exact bound-entry facts owner");
            connection.FailBindingOperation(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed,
                "source-controlled result publication failure"));
            await harness.Client.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(connection.Bindings.IsClosed && facts.OwnerCountForTesting == 2,
                "logical close cannot refund the entry while actual intake awaits its result barrier");
            Ensure(!connection.SessionTask.IsCompleted && harness.Service.Calls == 0,
                "physical loop remains held and no business method runs");
            release.TrySetResult();
            await connection.SessionTask.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(facts.OwnerCountForTesting == 1 && harness.Service.Calls == 0,
                "failed result and loop finally release only the bound-entry owner exactly once");
            try { await preparation; throw new InvalidOperationException("failed Prepare unexpectedly completed"); }
            catch (SharpLinkException error)
            { Ensure(error.Code == SharpLinkErrorCode.ConnectionClosed, "stopped exact Prepare result"); }
        }
        finally
        {
            release.TrySetResult();
            try { await preparation; }
            catch (SharpLinkException) { }
        }
    }
}
