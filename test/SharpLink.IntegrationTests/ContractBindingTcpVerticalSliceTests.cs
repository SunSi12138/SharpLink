namespace SharpLink.IntegrationTests;

// Actual generated proxy + TCP source controls, not the earlier scalar MODEL suite.
public sealed class ContractBindingTcpVerticalSliceTests
{
    [Test]
    public async Task ColdProxyMustWaitForConfirmedBindingBeforeUnaryOrOneWayBusiness()
    {
        await using var harness = new Harness();
        var selected = NewGate();
        var release = NewGate();
        harness.Server.BeforeBindingResultForTesting = (_, _) => { selected.TrySetResult(); return release.Task; };
        var proxy = harness.Client.CreateProxy<IBindingVerticalService>();
        Ensure(harness.Client.State == SharpLinkConnectionState.Created, "local factory must not connect");
        await AssertNoBoundRoute(() => proxy.Service.EchoAsync(3));
        await harness.ConnectAsync();
        var preparation = proxy.PrepareAsync(TimeSpan.FromSeconds(10)).AsTask();
        try
        {
            await selected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertNoBoundRoute(() => proxy.Service.EchoAsync(4));
            Ensure(harness.Service.Calls == 0 && !preparation.IsCompleted,
                "unpublished binding must not dispatch business or complete Prepare");
        }
        finally { release.TrySetResult(); }
        await preparation;
        Ensure(await proxy.Service.EchoAsync(7) == 8, "bound generated unary result");
        await proxy.Service.NotifyAsync(9);
        Ensure(await harness.Service.Notification.Task.WaitAsync(TimeSpan.FromSeconds(5)) == 9,
            "plain OneWay has no acknowledgement but reaches exact bound registration");
        Ensure(harness.Service.Calls == 2, "exactly two admitted business invocations");
    }

    [Test]
    public async Task EarlyPeerResultMustNotPublishBeforeEnqueueConfirmation()
    {
        await using var harness = new Harness();
        var early = NewGate();
        var confirmation = NewGate();
        harness.Client.BindingSessionCreatedForTesting = binding =>
        {
            binding.BeforePublicationConfirmationForTesting = () => confirmation.Task;
            binding.EarlyResultObservedForTesting = () => early.TrySetResult();
        };
        var proxy = harness.Client.CreateProxy<IBindingVerticalService>();
        await harness.ConnectAsync();
        var preparation = proxy.PrepareAsync(TimeSpan.FromSeconds(10)).AsTask();
        try
        {
            await early.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!preparation.IsCompleted, "validated early result is only buffered");
            await AssertNoBoundRoute(() => proxy.Service.EchoAsync(11));
            Ensure(harness.Service.Calls == 0, "early result cannot authorize business");
        }
        finally { confirmation.TrySetResult(); }
        await preparation;
        Ensure(await proxy.Service.EchoAsync(12) == 13, "confirmed original receiver becomes callable");
    }

    [Test]
    public async Task CancelledPrepareWaiterMustNotCancelSharedDemand()
    {
        await using var harness = new Harness();
        var selected = NewGate();
        var release = NewGate();
        harness.Server.BeforeBindingResultForTesting = (_, _) => { selected.TrySetResult(); return release.Task; };
        var proxy = harness.Client.CreateProxy<IBindingVerticalService>();
        await harness.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelled = proxy.PrepareAsync(TimeSpan.FromSeconds(10), cancellation.Token).AsTask();
        var retained = proxy.PrepareAsync(TimeSpan.FromSeconds(10)).AsTask();
        try
        {
            await selected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try { await cancelled; throw new InvalidOperationException("Prepare unexpectedly succeeded"); }
            catch (OperationCanceledException exception)
            { Ensure(exception.CancellationToken == cancellation.Token, "caller cancellation identity"); }
            Ensure(!retained.IsCompleted, "one waiter cannot cancel shared binding ownership");
        }
        finally { release.TrySetResult(); }
        await retained;
        Ensure(await proxy.Service.EchoAsync(15) == 16, "shared original demand survives waiter cancellation");
    }

    [Test]
    public async Task PrepareBeforeConnectMustWakeOnClientStop()
    {
        await using var harness = new Harness();
        var proxy = harness.Client.CreateProxy<IBindingVerticalService>();
        var preparation = proxy.PrepareAsync(TimeSpan.FromSeconds(30)).AsTask();
        await harness.Client.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await preparation.WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Stopped preparation unexpectedly succeeded");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.ConnectionClosed, "stop wins without waiting for Prepare timeout");
        }
        Ensure(harness.Service.Calls == 0, "local preparation/stop cannot dispatch business");
    }

    [Test]
    public async Task LaterBindCannotOvertakeAnUnconfirmedEarlierPublication()
    {
        await using var harness = new Harness();
        var early = NewGate();
        var secondWaiting = NewGate();
        var firstConfirmation = NewGate();
        var resultCount = 0;
        var confirmations = 0;
        harness.Server.BeforeBindingResultForTesting = (_, _) =>
        {
            Interlocked.Increment(ref resultCount);
            return Task.CompletedTask;
        };
        harness.Client.BindingSessionCreatedForTesting = binding =>
        {
            binding.BeforePublicationConfirmationForTesting = () =>
                Interlocked.Increment(ref confirmations) == 1 ? firstConfirmation.Task : Task.CompletedTask;
            binding.EarlyResultObservedForTesting = () => early.TrySetResult();
            binding.PublicationWaitStartedForTesting = id => { if (id == 2) secondWaiting.TrySetResult(); };
        };
        var first = harness.Client.CreateProxy<IBindingVerticalService>();
        var second = harness.Client.CreateProxy<IBindingSecondVerticalService>();
        await harness.ConnectAsync();
        var firstReady = first.PrepareAsync(TimeSpan.FromSeconds(10)).AsTask();
        var secondReady = second.PrepareAsync(TimeSpan.FromSeconds(10)).AsTask();
        try
        {
            await Task.WhenAll(early.Task, secondWaiting.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(Volatile.Read(ref resultCount) == 1, "ID2 has reached its publication wait but cannot overtake ID1");
            Ensure(!firstReady.IsCompleted && !secondReady.IsCompleted, "neither route is confirmed");
        }
        finally { firstConfirmation.TrySetResult(); }
        await Task.WhenAll(firstReady, secondReady);
        Ensure(Volatile.Read(ref resultCount) == 2, "each distinct contract demand gets exactly one Bind result");
        Ensure(await first.Service.EchoAsync(21) == 22 && await second.Service.EchoAsync(21) == 23,
            "both exact generated receivers become callable after ordered publication");
    }

    private static async Task AssertNoBoundRoute(Func<ValueTask<int>> action)
    {
        try { await action(); throw new InvalidOperationException("Unbound call unexpectedly succeeded"); }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable &&
                exception.DetailCode == SharpLinkErrorDetails.Unavailable.NoBoundRoute, "typed unbound route error");
        }
    }
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task? _serverTask;
        internal BindingVerticalService Service { get; } = new();
        internal BindingSecondVerticalService SecondService { get; } = new();
        internal SharpLinkServer Server { get; }
        internal SharpLinkClient Client { get; }
        internal Harness()
        {
            var server = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback).ReplaceService(Service).ReplaceService(SecondService);
            var port = ((IPEndPoint)server.Transport!.LocalEndPoint!).Port;
            Server = (SharpLinkServer)server.Build();
            Client = (SharpLinkClient)SharpClientBuilder.Create().DisableRequestTimeout()
                .UseTcp(IPAddress.Loopback.ToString(), port).Build();
        }
        internal async Task ConnectAsync()
        {
            _serverTask = Server.RunUntilStoppedAsync(_stop.Token).AsTask();
            await Client.ConnectAsync(_stop.Token);
        }
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _stop.CancelAsync();
            await Server.DisposeAsync();
            if (_serverTask is not null)
            {
                try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
            _stop.Dispose();
        }
    }
}

[RpcContract]
public interface IBindingVerticalService : IService
{
    [NonCancellable] ValueTask<int> EchoAsync(int value);
    [Oneway, NonCancellable] ValueTask NotifyAsync(int value);
}

[RpcService]
public sealed class BindingVerticalService : IBindingVerticalService
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public TaskCompletionSource<int> Notification { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask<int> EchoAsync(int value) { Interlocked.Increment(ref _calls); return ValueTask.FromResult(value + 1); }
    public ValueTask NotifyAsync(int value) { Interlocked.Increment(ref _calls); Notification.TrySetResult(value); return ValueTask.CompletedTask; }
}

[RpcContract]
public interface IBindingSecondVerticalService : IService
{
    [NonCancellable] ValueTask<int> EchoAsync(int value);
}

[RpcService]
public sealed class BindingSecondVerticalService : IBindingSecondVerticalService
{
    public ValueTask<int> EchoAsync(int value) => ValueTask.FromResult(value + 2);
}
