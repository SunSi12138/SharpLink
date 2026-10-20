#if SHARPLINK_STATIC_BINDING_PROTOTYPE
using System;
using System.Buffers;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

internal static class BridgeChecks
{
    internal static async Task RunAsync(string output)
    {
        var checks = 0;
        foreach (var transport in new[] { "tcp", "sharedmemory" })
        {
            await using var fixture = await StaticBindingFixture.CreateAsync(transport, 2, prepare: false);
            var client = fixture.Client;
            var site = client.StaticBindingTestSite<IBenchmarkRpc>();
            IRpcChannel channel = new StaticBindingRpcChannel(client, site);
            var manifest = GlobalCatalogManifestSource.Instance.CreateSnapshot()
                .Single(m => m.Contracts.Any(c => c.ContractType == typeof(IBenchmarkRpc)));
            var contract = manifest.Contracts.Single(c => c.ContractType == typeof(IBenchmarkRpc));
            var generated = contract.Methods.Single(m => m.Name == nameof(IBenchmarkRpc.PublishEventAsync));
            var stub = contract.StubFactory(client.RuntimeContext.Codecs);
            if (!stub.TryGetMethodDescriptor(generated.MethodId, out var method))
                throw new InvalidOperationException("Generated OneWay descriptor missing");
            checks += await CheckWriterAsync<RpcNoClientStreams>(channel, method);
            checks += await CheckWriterAsync<PoisonStreams>(channel, method);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await fixture.PrepareAsync(budget.Token);
            fixture.Receiver.Reset(1024);
            for (var i = 0; i < 1024; i++)
                await fixture.Rpc.PublishEventAsync(i, System.Diagnostics.Stopwatch.GetTimestamp(), "binding");
            while (fixture.Receiver.Completed != 1024) await Task.Delay(1, budget.Token);
            Require(fixture.Receiver.Seen.All(x => x == 1), "Generated OneWay lost/duplicated business work");
            Require(client.StaticBindingDiagnostics<IBenchmarkRpc>() == (2, 2L, 0), "Unexpected Bind or Session change");
            Require(await fixture.Rpc.AddAsync(2, 3) == 5, "Session poisoned by rejection or OneWay");
            checks += 3;
        }
        Require(PoisonStreams.Calls == 0, "Unsupported stream producer ran");
        await System.IO.File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            Passed = true, Checks = checks, TransportCount = 2, WriterTypes = 2,
            GeneratedReceived = 2048, StreamWrites = PoisonStreams.Calls,
            Magic = (int)ProtocolV2Constants.Magic
        }));
    }

    private static async Task<int> CheckWriterAsync<TStreams>(IRpcChannel channel, RpcMethodDescriptor method)
        where TStreams : struct, IRpcClientStreamWriter
    {
        var codec = new PoisonCodec();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        // Shape rejection must return a faulted ValueTask before null-codec or cancellation validation.
        foreach (var invalid in new[] { method with { HasClientStreams = true },
            method with { ClientStreamCount = 1 }, method with { ClientStreamCount = -1 } })
        {
            ValueTask result;
            try { result = channel.InvokeOneWayAsync(invalid, 0, (IRpcCodec<int>)null!, default(TStreams), null, cancelled.Token); }
            catch (Exception error) { throw new InvalidOperationException("Shape failure changed to a synchronous throw", error); }
            Require(result.IsFaulted, "Shape rejection was not already faulted");
            await ExpectAsync(result, e => e is SharpLinkException error && error.DetailCode == (ushort)(60000 + (ushort)StaticBindingFailure.UnsupportedShape));
        }
        Exception? observed = null;
        try { _ = channel.InvokeOneWayAsync(method, 0, (IRpcCodec<int>)null!, default(TStreams), null, cancelled.Token); }
        catch (Exception error) { observed = error; }
        Require(observed is ArgumentNullException, "Null codec must precede cancellation and throw synchronously");
        observed = null;
        try { _ = channel.InvokeOneWayAsync(method, 0, codec, default(TStreams), null, cancelled.Token); }
        catch (Exception error) { observed = error; }
        Require(observed is OperationCanceledException, "Pre-cancellation must throw synchronously");
        var pending = channel.InvokeOneWayAsync(method, 0, codec, default(TStreams), null);
        await ExpectAsync(pending, e => e is SharpLinkException error && error.DetailCode == (ushort)(60000 + (ushort)StaticBindingFailure.NoBoundRoute));
        Require(codec.Writes == 0, "Unprepared or cancelled call serialized business bytes");
        return 10;
    }

    private static async Task ExpectAsync(ValueTask value, Func<Exception, bool> expected)
    {
        try { await value.AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) when (expected(error)) { return; }
        throw new InvalidOperationException("Expected precise failure not observed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class PoisonCodec : IRpcCodec<int>
    {
        internal int Writes;
        public void Serialize(in int value, IBufferWriter<byte> writer) => Writes++;
        public int Deserialize(in ReadOnlySequence<byte> value) => throw new InvalidOperationException();
    }

    private readonly struct PoisonStreams : IRpcClientStreamWriter
    {
        internal static int Calls;
        public ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("Must not start a stream producer");
        }
    }
}
#endif
