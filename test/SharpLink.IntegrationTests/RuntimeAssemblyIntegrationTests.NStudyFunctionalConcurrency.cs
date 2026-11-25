using System.Collections.Concurrent;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyPreparedUnaryAndOneWayReachActualConcurrency(string transport)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = guard.Token;
        var service = new NStudyFunctionalConcurrencyProbe();
        var builder = SharpLinkServerBuilder.Create().ReplaceService<INStudyFunctionalConcurrencyProbe>(service);
        var memoryName = transport == "shared-memory" ? $"sharplink-f22-{Guid.NewGuid():N}" : null;
        if (memoryName is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memoryName);
        var port = memoryName is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;
        var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout();
        if (memoryName is null)
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
        else
            clientBuilder.UseSharedMemory(memoryName);

        await using var server = (SharpLinkServer)builder.Build();
        await using var client = (SharpLinkClient)clientBuilder.Build();
        await server.StartAsync(ct);
        await client.ConnectAsync(ct);
        var proxy = client.CreateProxy<INStudyFunctionalConcurrencyProbe>();
        var logicalCalls = (ISharpLinkClientDrainInspector)client;
        try
        {
            await proxy.Service.EchoAsync(1, ct);
            throw new InvalidOperationException("A call before Prepare must not reach the service.");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.Unavailable) { }
        Ensure(logicalCalls.ActiveCallCount == 0, "local rejection leaves no logical owner or pending reservation");
        await proxy.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        var connection = NStudyClientConnection(client);
        var peer = NStudyServerConnection(server);
        var session = connection.Session;
        var initialBinds = session.NStudyBindOperationForDiagnostics;
        Ensure(initialBinds == 1 &&
               (memoryName is null ? session.RemoteEndPoint is IPEndPoint : session.Input is SharedMemoryPipeReader),
            "the generated proxy uses the selected actual transport and one prepared binding");

        foreach (var count in new[] { 1, 8 })
        {
            var ids = Enumerable.Range(1, count).ToArray();
            var unary = service.Begin(count);
            try
            {
                var calls = ids.Select(id => proxy.Service.EchoAsync(id, ct)).ToArray();
                await unary.AllEntered.Task.WaitAsync(ct);
                Ensure(calls.All(call => !call.IsCompleted) && unary.Completed.IsEmpty && logicalCalls.ActiveCallCount == count,
                    "all distinct Unary requests actually reached the service while replies are withheld");
                unary.Release.TrySetResult();
                await WaitForNStudyAsync(() => calls.All(call => call.IsCompleted), ct);
                Ensure(logicalCalls.ActiveCallCount == 0, "logical ownership ends before the application consumes the results");
                var responses = count == 1
                    ? new[] { await calls[0] }
                    : await Task.WhenAll(calls.Select(call => call.AsTask())).WaitAsync(ct);
                Ensure(responses.SequenceEqual(ids.Select(id => id + 100)) &&
                       unary.Completed.Keys.Order().SequenceEqual(ids), "Unary returns the exact results once");
            }
            finally { unary.Release.TrySetResult(); }
            await WaitForNStudyAsync(() => peer.ActiveCalls == 0 && connection.PendingCalls.ActiveCount == 0, ct);
            Console.WriteLine($"F22_PASS transport={transport} shape=unary concurrency={count} completed={count}");

            var oneWay = service.Begin(count);
            try
            {
                var sends = ids.Select(id => proxy.Service.NotifyAsync(id, ct).AsTask()).ToArray();
                await oneWay.AllEntered.Task.WaitAsync(ct);
                await Task.WhenAll(sends).WaitAsync(ct);
                Ensure(oneWay.Completed.IsEmpty, "OneWay local completion does not wait for remote handler completion");
                oneWay.Release.TrySetResult();
                await oneWay.AllCompleted.Task.WaitAsync(ct);
                Ensure(oneWay.Completed.Keys.Order().SequenceEqual(ids), "every distinct OneWay handler physically finishes once");
            }
            finally { oneWay.Release.TrySetResult(); }
            await WaitForNStudyAsync(() => peer.ActiveCalls == 0 && connection.PendingCalls.ActiveCount == 0, ct);
            Console.WriteLine($"F22_PASS transport={transport} shape=oneway concurrency={count} completed={count}");
        }

        var cancelled = service.Begin(1);
        using (var caller = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var call = proxy.Service.EchoAsync(1, caller.Token);
            try
            {
                await cancelled.AllEntered.Task.WaitAsync(ct);
                Ensure(logicalCalls.ActiveCallCount == 1, "the cancelled call was active at the service");
                caller.Cancel();
                try
                {
                    await call;
                    throw new InvalidOperationException("The caller must observe its cancellation.");
                }
                catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
                Ensure(logicalCalls.ActiveCallCount == 0, "cancellation releases the same logical count once");
            }
            finally { cancelled.Release.TrySetResult(); }
        }

        await WaitForNStudyAsync(() => peer.NStudyBindings.Snapshot() == (1, 0, 0) &&
            connection.ActiveCallCount == 0 && connection.PendingCalls.NStudyProducerCount == 0 &&
            session.QueuedSendBytes == 0 && session.StreamManager.ActiveStreamCount == 0 &&
            peer.Session.StreamManager.ActiveStreamCount == 0, ct);
        Ensure(ReferenceEquals(NStudyClientConnection(client), connection) &&
               session.NStudyBindOperationForDiagnostics == initialBinds, "neither load shape reconnects or implicitly prepares");
    }
}

[RpcContract]
public interface INStudyFunctionalConcurrencyProbe : IService
{
    ValueTask<int> EchoAsync(int id, CancellationToken cancellationToken);

    [Oneway]
    ValueTask NotifyAsync(int id, CancellationToken cancellationToken);
}

[RpcService]
public sealed class NStudyFunctionalConcurrencyProbe : INStudyFunctionalConcurrencyProbe
{
    private Batch? _batch;

    internal Batch Begin(int count)
    {
        var batch = new Batch(count);
        Volatile.Write(ref _batch, batch);
        return batch;
    }

    public async ValueTask<int> EchoAsync(int id, CancellationToken cancellationToken)
    {
        await NotifyAsync(id, cancellationToken);
        return id + 100;
    }

    public async ValueTask NotifyAsync(int id, CancellationToken cancellationToken)
    {
        var batch = Volatile.Read(ref _batch)!;
        if (!batch.Entered.TryAdd(id, 0))
            throw new InvalidOperationException($"Duplicate invocation: {id}");
        if (batch.Entered.Count == batch.Count)
            batch.AllEntered.TrySetResult();
        await batch.Release.Task.WaitAsync(cancellationToken);
        if (!batch.Completed.TryAdd(id, 0))
            throw new InvalidOperationException($"Duplicate completion: {id}");
        if (batch.Completed.Count == batch.Count)
            batch.AllCompleted.TrySetResult();
    }

    internal sealed class Batch(int count)
    {
        internal int Count => count;
        internal readonly ConcurrentDictionary<int, byte> Entered = new();
        internal readonly ConcurrentDictionary<int, byte> Completed = new();
        internal readonly TaskCompletionSource AllEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource AllCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
