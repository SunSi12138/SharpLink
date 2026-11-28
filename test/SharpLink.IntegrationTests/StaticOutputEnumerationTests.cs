namespace SharpLink.IntegrationTests;

public sealed class StaticOutputEnumerationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp", false)]
    [Arguments("tcp", true)]
    [Arguments("shared-memory", false)]
    [Arguments("shared-memory", true)]
    public async Task GeneratedOutputIsSingleUseAndDoesNotStartBeforeFirstMove(string transport, bool duplex)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = budget.Token;
        var calls = new CallCounter();
        var serverBuilder = SharpLinkServerBuilder.Create().AddInterceptor(calls);
        var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout();
        if (transport == "tcp")
        {
            serverBuilder.UseTcp(0, IPAddress.Loopback);
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(),
                ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port);
        }
        else
        {
            var name = $"sharplink-output-enumeration-{Guid.NewGuid():N}";
            serverBuilder.UseSharedMemory(name);
            clientBuilder.UseSharedMemory(name);
        }
        await using var server = serverBuilder.Build();
        await using var client = clientBuilder.Build();
        await server.StartAsync(ct);
        await client.ConnectAsync(ct);
        var proxy = client.CreateProxy<ICompressionService>();
        await proxy.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        var logical = (ISharpLinkClientDrainInspector)client;
        var producerStarts = 0;

        var stream = NewCall();
        await using (var reader = stream.GetAsyncEnumerator(ct))
        {
            await RejectSecondEnumerator(stream, ct);
            Ensure(calls.Count == 0 && Volatile.Read(ref producerStarts) == 0 && logical.ActiveCallCount == 0,
                "Creating the result and taking its only enumerator must not start the RPC or input producer.");
            await ReadOne(reader);
        }
        await RejectSecondEnumerator(stream, ct);
        Ensure(calls.Count == 1 && Volatile.Read(ref producerStarts) == (duplex ? 1 : 0) && logical.ActiveCallCount == 0,
            "Completing the only enumeration dispatches once and releases the logical call.");

        var abandoned = NewCall();
        await abandoned.GetAsyncEnumerator(ct).DisposeAsync();
        await RejectSecondEnumerator(abandoned, ct);
        Ensure(calls.Count == 1 && Volatile.Read(ref producerStarts) == (duplex ? 1 : 0),
            "Even when disposed unused, the first enumerator consumes the claim without starting a call.");

        await using (var reader = NewCall().GetAsyncEnumerator(ct))
            await ReadOne(reader);
        Ensure(calls.Count == 2 && Volatile.Read(ref producerStarts) == (duplex ? 2 : 0) && logical.ActiveCallCount == 0,
            "Calling the generated method again creates a new, independent stream.");

        IAsyncEnumerable<byte[]> NewCall() => duplex
            ? proxy.Service.DuplexBytesAsync(Input())
            : proxy.Service.DownloadBytesAsync(1, 1);

        async IAsyncEnumerable<byte[]> Input()
        {
            Interlocked.Increment(ref producerStarts);
            await Task.Yield();
            yield return [42];
        }

        async Task ReadOne(IAsyncEnumerator<byte[]> reader)
        {
            Ensure(await reader.MoveNextAsync().AsTask().WaitAsync(ct) && reader.Current is [42],
                "The first enumerator receives the expected output.");
            Ensure(!await reader.MoveNextAsync().AsTask().WaitAsync(ct), "The stream reaches normal EOF.");
        }
    }

    private static async Task RejectSecondEnumerator(IAsyncEnumerable<byte[]> stream, CancellationToken ct)
    {
        IAsyncEnumerator<byte[]> unexpected;
        try { unexpected = stream.GetAsyncEnumerator(ct); }
        catch (InvalidOperationException error) when (error.Message == "A logical RPC stream can only be enumerated once.")
        {
            return;
        }
        await unexpected.DisposeAsync();
        throw new InvalidOperationException("A second GetAsyncEnumerator must fail synchronously.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class CallCounter : ISharpLinkServerInterceptor
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);

        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
        {
            Interlocked.Increment(ref _count);
            return next(context);
        }
    }
}
