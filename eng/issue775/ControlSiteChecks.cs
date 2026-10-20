#if SHARPLINK_STATIC_BINDING_PROTOTYPE
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static class ControlSiteChecks
{
    internal static async Task RunAsync(string output)
    {
        var rows = new List<object>();
        var assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        foreach (var transport in new[] { "tcp", "sharedmemory" })
            foreach (var intercepted in new[] { false, true })
                foreach (var telemetry in new[] { false, true })
                {
                    var activities = 0;
                    using var listener = new ActivityListener
                    {
                        ShouldListenTo = source => telemetry && source.Name == "SharpLink.Client",
                        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                        ActivityStopped = _ => Interlocked.Increment(ref activities)
                    };
                    ActivitySource.AddActivityListener(listener);
                    await using var first = await Fixture.CreateAsync(transport, "first", intercepted);
                    await using var second = await Fixture.CreateAsync(transport, "second", intercepted);
                    foreach (var f in new[] { first, second })
                    {
                        await ExpectFailureAsync(() => f.Rpc.EchoAsync("unprepared").AsTask(),
                            e => e is SharpLinkException ex && ex.DetailCode == (ushort)(60000 + (ushort)StaticBindingFailure.NoBoundRoute));
                        // Check the documented failure value, not a hand-written numeric enum assumption.
                        Check(f.Service.Echoes == 0, "Unprepared call entered business code");
                        await f.Client.PrepareStaticBindingAsync<IBenchmarkRpc>(CancellationToken.None);
                        var before = f.Interceptor.Count;
                        Check(await f.Rpc.AddAsync(20, 22) == 42, "Retry response");
                        Check(f.Service.Attempts == 2, "Unary retry did not execute exactly two attempts");
                        Check(!intercepted || f.Interceptor.Count == before + 1, "Interceptor repeated per attempt");
                        Check(await f.Rpc.EchoAsync("ordinary") == f.Tag + ":ordinary", "Non-retry unary binding");
                    }
                    // Same generated contract, different servers/Sessions and invocation-local metadata.
                    var calls = Enumerable.Range(0, 16).SelectMany(i => new[]
                    {
                        VerifyEchoAsync(first, i), VerifyEchoAsync(second, i)
                    });
                    await Task.WhenAll(calls);
                    assertions += 32;
                    foreach (var f in new[] { first, second })
                    {
                        for (var i = 0; i < 16; i++) await f.Rpc.PublishEventAsync(i, 42, f.Tag);
                        await f.Service.OneWaysDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        Check(f.Service.OneWays == 16, "OneWay business completion count");
                        Check(f.Client.StaticBindingDiagnostics<IBenchmarkRpc>() == (2, 2L, 0), "Call rebound or lost pool binding");
                        await CancellationChecksAsync(f, intercepted);
                        assertions += intercepted ? 2 : 1;
                        await f.Client.StopAsync();
                        await f.Server.StopAsync(TimeSpan.FromSeconds(2));
                        Check(f.Client.ActiveClientCallCount == 0 && f.Server.ActiveCallCountForDiagnostics == 0,
                            "Invocation/admission not released");
                    }
                    Check(telemetry ? activities > 0 : activities == 0, "Telemetry path did not match the selected mode");
                    rows.Add(new { Transport = transport, Intercepted = intercepted, Telemetry = telemetry,
                        Activities = activities, Passed = true });
                }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            Passed = true, Checks = assertions, Cases = rows,
            ControlBytes = Unsafe.SizeOf<SharpLinkClient.ResolvedCallControl>(),
            Magic = (int)ProtocolV2Constants.Magic
        }));
    }

    private static async Task VerifyEchoAsync(Fixture fixture, int i)
    {
        var value = i.ToString();
        if (await fixture.Rpc.EchoAsync(value) != fixture.Tag + ":" + value)
            throw new InvalidOperationException("Concurrent call crossed a binding or metadata context.");
    }

    private static async Task CancellationChecksAsync(Fixture fixture, bool intercepted)
    {
        var client = fixture.Client;
        var site = client.StaticBindingTestSite<IBenchmarkRpc>();
        var manifest = GlobalCatalogManifestSource.Instance.CreateSnapshot()
            .Single(m => m.Contracts.Any(c => c.ContractType == typeof(IBenchmarkRpc)));
        var contract = manifest.Contracts.Single(c => c.ContractType == typeof(IBenchmarkRpc));
        var methodId = contract.Methods.Single(m => m.Name == nameof(IBenchmarkRpc.AddAsync)).MethodId;
        var stub = contract.StubFactory(client.RuntimeContext.Codecs);
        if (!stub.TryGetMethodDescriptor(methodId, out var method)) throw new InvalidOperationException("Method missing");
        var codec = new PoisonCodec();
        var response = client.RuntimeContext.Codecs.GetCodec<int>();
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await ExpectFailureAsync(() => client.InvokeStaticUnaryAsync(site, method, 0, codec,
                response, null, cts.Token).AsTask(), e => e is OperationCanceledException);
        }
        if (intercepted)
        {
            using var cts = new CancellationTokenSource();
            fixture.Interceptor.Block = true;
            var pending = client.InvokeStaticUnaryAsync(site, method, 0, codec, response, null, cts.Token).AsTask();
            await fixture.Interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            fixture.Interceptor.Release.TrySetResult();
            await ExpectFailureAsync(() => pending, e => e is OperationCanceledException ||
                e is SharpLinkException { Code: SharpLinkErrorCode.Cancelled });
        }
        if (codec.Writes != 0) throw new InvalidOperationException("Cancellation serialized business bytes");
    }

    private static async Task ExpectFailureAsync(Func<Task> call, Func<Exception, bool> matches)
    {
        try { await call().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) when (matches(error)) { return; }
        throw new InvalidOperationException("Expected rejection was not observed");
    }

    private sealed class PoisonCodec : IRpcCodec<int>
    {
        internal int Writes;
        public void Serialize(in int value, IBufferWriter<byte> writer) => Writes++;
        public int Deserialize(in ReadOnlySequence<byte> value) => throw new InvalidOperationException();
    }

    private sealed class Interceptor(string tag) : ISharpLinkClientInterceptor
    {
        internal int Count;
        internal bool Block;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        {
            Interlocked.Increment(ref Count);
            if (Block)
            {
                Entered.TrySetResult();
                await Release.Task;
            }
            await Task.Yield();
            context.Metadata = new SharpLinkMetadata(new KeyValuePair<string, string>("control-site", tag + "-intercepted"));
            return await next(context);
        }
    }

    private sealed class Observer(string tag, bool intercepted) : BenchmarkRpcService, IBenchmarkRpc
    {
        internal int Attempts, Echoes, OneWays;
        internal readonly TaskCompletionSource OneWaysDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void VerifyContext(bool timed)
        {
            var context = SharpLinkCallContext.Current;
            var expected = intercepted ? tag + "-intercepted" : tag;
            if (context is null || context.LocalRpcDeadline.HasValue != timed ||
                context.Metadata is null || !context.Metadata.Any(p => p.Key == "control-site" && p.Value == expected))
                throw new InvalidOperationException("Metadata or deadline lost at binding boundary");
        }
        ValueTask<int> IBenchmarkRpc.AddAsync(int left, int right)
        {
            VerifyContext(true);
            if (Interlocked.Increment(ref Attempts) == 1)
                throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "One deliberate transient failure");
            return ValueTask.FromResult(left + right);
        }
        ValueTask<string> IBenchmarkRpc.EchoAsync(string value)
        {
            VerifyContext(true);
            Interlocked.Increment(ref Echoes);
            return ValueTask.FromResult(tag + ":" + value);
        }
        ValueTask IBenchmarkRpc.PublishEventAsync(int code, long ticks, string value)
        {
            VerifyContext(false);
            if (value != tag || ticks != 42 || (uint)code >= 16) throw new InvalidOperationException("Wrong OneWay target");
            if (Interlocked.Increment(ref OneWays) == 16) OneWaysDone.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal required string Tag;
        internal required SharpLinkClient Client;
        internal required SharpLinkServer Server;
        internal required IBenchmarkRpc Rpc;
        internal required Observer Service;
        internal required Interceptor Interceptor;
        internal static async Task<Fixture> CreateAsync(string transport, string tag, bool intercepted)
        {
            var service = new Observer(tag, intercepted);
            var interceptor = new Interceptor(tag);
            var sb = SharpLinkServerBuilder.Create();
            var cb = SharpClientBuilder.Create().UseRequestTimeout(TimeSpan.FromSeconds(10))
                .UseConnectionPool(o => { o.MinConnections = 2; o.MaxConnections = 2; })
                .UseRetry(o => { o.MaxAttempts = 2; o.InitialBackoff = TimeSpan.Zero; o.MaxBackoff = TimeSpan.Zero; o.JitterRatio = 0; });
            if (intercepted) cb.AddInterceptor(interceptor);
            if (transport == "tcp")
            {
                sb.UseTcp(0, IPAddress.Loopback.ToString());
                cb.UseTcp(IPAddress.Loopback.ToString(), ((IPEndPoint)sb.Transport!.LocalEndPoint!).Port);
            }
            else
            {
                var name = "control-site-" + Guid.NewGuid().ToString("N");
                sb.UseTransport(new SharedMemoryServerTransportListener(name));
                cb.UseSharedMemory(name);
            }
            sb.ReplaceService<IBenchmarkRpc>(service);
            var server = (SharpLinkServer)sb.Build();
            var client = (SharpLinkClient)cb.Build();
            try
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await server.StartAsync(budget.Token);
                await client.ConnectAsync(budget.Token);
                while (client.ReadyConnectionCount != 2) await Task.Delay(1, budget.Token);
                return new Fixture { Tag = tag, Client = client, Server = server, Service = service,
                    Interceptor = interceptor, Rpc = client.GetWithMetadata<IBenchmarkRpc>(new SharpLinkMetadata(
                        new KeyValuePair<string, string>("control-site", tag))) };
            }
            catch { await client.DisposeAsync(); await server.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync() { await Client.DisposeAsync(); await Server.DisposeAsync(); }
    }
}
#endif
