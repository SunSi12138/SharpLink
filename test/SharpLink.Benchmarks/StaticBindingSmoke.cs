#if SHARPLINK_STATIC_BINDING_PROTOTYPE
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static class StaticBindingSmoke
{
    private static int s_checks;
    private static void Check(bool condition, string message)
    {
        StaticBindingEvidence.Require(condition, message);
        s_checks++;
    }

    internal static async Task RunAsync()
    {
        WireChecks();
        foreach (var transport in new[] { "tcp", "sharedmemory" })
        {
            await using var fixture = await StaticBindingFixture.CreateAsync(transport, 4, prepare: false);
            var client = fixture.Client;
            var site = client.StaticBindingTestSite<IBenchmarkRpc>();
            var manifest = GlobalCatalogManifestSource.Instance.CreateSnapshot()
                .Single(m => m.Contracts.Any(c => c.ContractType == typeof(IBenchmarkRpc)));
            var contract = manifest.Contracts.Single(c => c.ContractType == typeof(IBenchmarkRpc));
            var stub = contract.StubFactory(client.RuntimeContext.Codecs);
            var generatedMethod = contract.Methods.Single(m => m.Name == nameof(IBenchmarkRpc.AddAsync));
            Check(stub.TryGetMethodDescriptor(generatedMethod.MethodId, out var method), "Generated method facts");
            var poison = new EmptyCodec();
            var response = client.RuntimeContext.Codecs.GetCodec<int>();
            await RejectAsync(() => client.InvokeStaticUnaryAsync(site, method, 0, poison, response, null).AsTask(),
                StaticBindingFailure.NoBoundRoute);
            Check(poison.Serialized == 0, "NoBoundRoute must precede serialization");
            await RejectAsync(() => fixture.Rpc.AddAsync(1, 2).AsTask(), StaticBindingFailure.NoBoundRoute);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
                client.PrepareStaticBindingAsync<IBenchmarkRpc>(timeout.Token)));
            var diagnostics = client.StaticBindingDiagnostics<IBenchmarkRpc>();
            Check(diagnostics == (4, 4L, 0), "Concurrent prepares merge one Bind per actual Session");
            Check(await fixture.Rpc.AddAsync(20, 22) == 42, "Unary after explicit Prepare");
            var targets = Volatile.Read(ref site.Snapshot);
            Check(targets.Select(t => t.Connection.Session).Distinct().Count() == 4, "Four physical Sessions");
            Check(targets.Select(t => t.Token).Distinct().Count() == 1, "Deliberately colliding Session-local tokens");
            foreach (var target in targets)
            {
                var wrongHash = new RpcHash128(site.Hash.High ^ 1UL, site.Hash.Low);
                await RejectAsync(() => target.Connection.StaticBindings.BindAsync(site.ContractId, wrongHash),
                    StaticBindingFailure.HashMismatch);
                Check(target.Connection.StaticBindings.PendingCount == 0, "Rejected Bind reclaimed");
                var corrupted = new StaticBindingCallSite(site.ContractId, site.Hash)
                { Snapshot = [target with { Token = target.Token ^ (1UL << 32) }] };
                // No business payload is emitted: admission must reject the binding, not decode it.
                await RejectAsync(() => client.InvokeStaticUnaryAsync(corrupted, method, 0, poison, response, null).AsTask(),
                    StaticBindingFailure.UnknownBinding);
                var one = new StaticBindingCallSite(site.ContractId, site.Hash) { Snapshot = [target] };
                await RejectAsync(() => client.InvokeStaticUnaryAsync(one,
                    method with { MethodId = long.MaxValue }, 0, poison, response, null).AsTask(),
                    StaticBindingFailure.UnknownMethod);
                Check(await fixture.Rpc.AddAsync(2, 3) == 5, "Rejected Unary must not poison shared Session");
            }
            var moved = 0;
            await RejectAsync(() => fixture.Rpc.UploadNumbersAsync(Producer()).AsTask(), StaticBindingFailure.UnsupportedShape);
            Check(moved == 0, "Unsupported stream producer must never start");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await client.PrepareStaticBindingAsync<IBenchmarkRpc>(cancelled.Token);
                throw new InvalidOperationException("Cancelled preparation succeeded");
            }
            catch (OperationCanceledException) { s_checks++; }
            fixture.Receiver.Reset(1024);
            for (var i = 0; i < 1024; i++)
                await fixture.Rpc.PublishEventAsync(i, System.Diagnostics.Stopwatch.GetTimestamp(), "binding");
            while (fixture.Receiver.Completed != 1024) await Task.Delay(1, timeout.Token);
            Check(fixture.Receiver.Seen.All(x => x == 1), "Every OneWay reached business code exactly once in this test");
            TableChecks(contract, stub, method);
            await client.StopAsync();
            try { await fixture.Rpc.AddAsync(1, 2); throw new InvalidOperationException("Stopped proxy executed"); }
            catch (SharpLinkException) { s_checks++; }
            foreach (var target in targets)
            {
                try
                {
                    await target.Connection.StaticBindings.BindAsync(site.ContractId, site.Hash);
                    throw new InvalidOperationException("Disconnected binding was reused");
                }
                catch (SharpLinkException) { s_checks++; }
                Check(target.Connection.StaticBindings.PendingCount == 0, "Disconnect reclaimed pending operations");
            }
            Console.WriteLine($"{transport}: static binding real-transport checks passed");
            async IAsyncEnumerable<int> Producer()
            {
                moved++;
                yield return 1;
                await Task.CompletedTask;
            }
        }
        Console.WriteLine($"STATIC_BINDING_SMOKE_PASS checks={s_checks}");
    }

    private static async Task RejectAsync(Func<Task> operation, StaticBindingFailure failure)
    {
        try { await operation().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (SharpLinkException exception) when (exception.DetailCode == (ushort)(60000 + (ushort)failure))
        { s_checks++; return; }
        throw new InvalidOperationException($"Expected structured failure {failure}");
    }

    private static void TableChecks(SharpLinkGeneratedContractDescriptor contract, IRpcStub stub, RpcMethodDescriptor method)
    {
        var table = new StaticBindingTable();
        var registrations = Enumerable.Range(0, 65).Select(_ => ServiceRegistration.CreateSingleton(
            typeof(IBenchmarkRpc), stub, new BenchmarkRpcService(), ownsService: false)).ToArray();
        var first = table.Install(contract.ContractId, registrations[0], contract, 1);
        Check(first.Failure == StaticBindingFailure.None && first.Token == 0x100000001UL, "Frozen token encoding");
        Check(table.Install(contract.ContractId, registrations[0], contract, 1) == first && table.Count == 1,
            "Duplicate registration does not consume capacity");
        for (var i = 1; i < 64; i++)
            Check(table.Install(contract.ContractId, registrations[i], contract, 1).Failure == StaticBindingFailure.None,
                "Bounded table accepts capacity");
        Check(table.Install(contract.ContractId, registrations[64], contract, 1).Failure == StaticBindingFailure.Capacity,
            "Table refuses capacity plus one");
        foreach (var token in new[] { 0UL, ulong.MaxValue, 0x200000001UL, 0x100000041UL })
            Check(table.Resolve(token, method.MethodId, ProtocolV2FrameFlags.HasReturn, out _, out _) ==
                StaticBindingFailure.UnknownBinding, "Untrusted sparse/stale token");
        Check(table.Resolve(first.Token, method.MethodId, ProtocolV2FrameFlags.HasReturn, out var registration, out _) ==
            StaticBindingFailure.None && ReferenceEquals(registration, registrations[0]), "Exact immutable registration");
        Check(table.Resolve(first.Token, method.MethodId, ProtocolV2FrameFlags.OneWay, out _, out _) ==
            StaticBindingFailure.UnsupportedShape, "Conflicting shape rejected");
        Check(table.Resolve(first.Token, long.MaxValue, ProtocolV2FrameFlags.HasReturn, out _, out _) ==
            StaticBindingFailure.UnknownMethod, "Unknown method rejected");
        var otherSession = new StaticBindingTable();
        var other = otherSession.Install(contract.ContractId, registrations[64], contract, 1);
        Check(other.Token == first.Token && otherSession.Resolve(other.Token, method.MethodId,
            ProtocolV2FrameFlags.HasReturn, out var otherRegistration, out _) == StaticBindingFailure.None &&
            ReferenceEquals(otherRegistration, registrations[64]), "Tokens have only Session-local meaning");
        table.Close();
        Check(table.Count == 0 && table.Resolve(first.Token, method.MethodId,
            ProtocolV2FrameFlags.HasReturn, out _, out _) == StaticBindingFailure.UnknownBinding, "Closed table releases entries");
        Check(table.Install(contract.ContractId, registrations[0], contract, 1).Failure == StaticBindingFailure.Unavailable,
            "Closed Session never reopens or recycles token");
        otherSession.Close();
    }

    private static void WireChecks()
    {
        Check(ProtocolV2Constants.Magic == 0xB7 && StaticBindingWire.RequestBytes == 24 && StaticBindingWire.AckBytes == 40,
            "Independent experimental wire grammar");
        var bytes = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, 123);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 456);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 789);
        for (var split = 1; split < bytes.Length; split++)
        {
            var first = new Segment(bytes.AsMemory(0, split));
            var last = first.Append(bytes.AsMemory(split));
            var decoded = StaticBindingWire.ReadRequest(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length));
            Check(decoded == (123L, new RpcHash128(456, 789)), "Fragmented Bind prefix");
        }
        for (var length = 0; length <= 25; length++)
        {
            if (length == 24) continue;
            try { StaticBindingWire.ReadRequest(new ReadOnlySequence<byte>(new byte[length])); }
            catch (SharpLinkProtocolViolationException) { s_checks++; continue; }
            throw new InvalidOperationException("Malformed Bind size accepted");
        }
    }

    private sealed class EmptyCodec : IRpcCodec<int>
    {
        internal int Serialized;
        public void Serialize(in int value, IBufferWriter<byte> buffer) => Serialized++;
        public int Deserialize(in ReadOnlySequence<byte> buffer) => throw new InvalidOperationException("Unexpected codec read");
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
        internal Segment(ReadOnlyMemory<byte> memory) { Memory = memory; }
    }
}
#endif
