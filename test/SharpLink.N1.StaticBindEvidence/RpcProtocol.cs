using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.N1.StaticBindEvidence;

internal static partial class Program
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(3);
    private static ISharpLinkGeneratedAssemblyManifest Manifest => SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Single(m => m.OwnerAssembly == typeof(IProbe).Assembly);
    private static SharpLinkGeneratedContractDescriptor Contract => Manifest.Contracts.Single(c => c.ContractType == typeof(IProbe));
    private static RpcMethodDescriptor EchoFacts => Contract.Methods.Single(m => m.Name == nameof(IProbe.EchoAsync)).Facts;
    private static ContractBindRequest BindRequest => new(Contract.ContractId, Manifest.RpcAssemblyHash, RpcRegistrationRequirement.Compatible, null);
    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action, string name) where TException : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (TException exception) { Check(true, name); return exception; }
        throw new InvalidOperationException("ASSERT expected " + typeof(TException).Name + ": " + name);
    }
    private static async Task UntilAsync(Func<bool> condition, string name, Func<string>? observe = null)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start) > Guard)
            {
                var observation = observe?.Invoke() ?? "predicate=false; no task publication inferred";
                Console.Error.WriteLine("N1_GUARD_PREDICATE phase=" + name + " observed=" + observation);
                throw new TimeoutException("FIXTURE guard: " + name + "; observed=" + observation);
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
        Check(true, name);
    }
    private static async Task RunRpcAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("N1_SOURCE_SHA"))) throw new InvalidOperationException("FIXTURE exact source SHA is required.");
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("RPC_PROTOCOL requires the reviewed Linux hosted runner.");
        if (Environment.Version != new Version(10, 0, 15)) throw new InvalidOperationException("FIXTURE actual runtime must be 10.0.15.");
        Components();
        await PublicationComponentsAsync().ConfigureAwait(false);
        await RunCaseAsync("generated-pool-session-pairing", GeneratedAsync).ConfigureAwait(false);
        await RunCaseAsync("early-ack-publication", EarlyAckAsync).ConfigureAwait(false);
        await RunCaseAsync("waiter-cancel-shared-bind", CancelWaiterAsync, serverGate: new(ProtocolV2FrameType.BindAck, false)).ConfigureAwait(false);
        await RunCaseAsync("wire-token-facts-capacity", RawGuardsAsync).ConfigureAwait(false);
        await RunCaseAsync("queued-release-cut-physical-owner", QueuedReleaseAsync, queued: true).ConfigureAwait(false);
        await RunCaseAsync("disconnect-inflight-and-reconnect", DisconnectAsync, serverGate: new(ProtocolV2FrameType.BindAck, false)).ConfigureAwait(false);
        await RunCaseAsync("site-waiter-hard-bounds", SiteWaiterBoundsAsync).ConfigureAwait(false);
        await RunCaseAsync("operation-timeout-physical-close", TimeoutAsync, serverGate: new(ProtocolV2FrameType.BindAck, false)).ConfigureAwait(false);
    }
    private static async Task RunCaseAsync(string name, Func<Harness, Task> body, TransportFrameGate? serverGate = null, bool queued = false)
    {
        Console.WriteLine("N1_CASE_BEGIN " + name);
        Harness? harness = null; Exception? first = null;
        try { harness = await Harness.CreateAsync(serverGate, queued).ConfigureAwait(false); await body(harness).ConfigureAwait(false); }
        catch (Exception exception) { first = exception; }
        if (harness is not null)
        {
            try { await harness.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { first = first is null ? cleanup : new AggregateException(first, cleanup); }
        }
        if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        Console.WriteLine("N1_CASE_END " + name);
    }
    private static async Task GeneratedAsync(Harness harness)
    {
        var records = new TransportFrameGate((ProtocolV2FrameType)255, false);
        var client = harness.Client(records, 2);
        var proxy = client.CreateProxy<IProbe>();
        var before = ProbePayloadCodec.Serializes;
        var noRoute = await ThrowsAsync<SharpLinkException>(() => proxy.Service.EchoAsync(new() { Value = 1 }, default).AsTask(), "before-connect-failfast").ConfigureAwait(false);
        Check(noRoute.DetailCode == SharpLinkErrorDetails.ContractBinding.NoBoundRoute && ProbePayloadCodec.Serializes == before, "no-bound-route-before-business-codec");
        await client.ConnectAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false);
        await proxy.PrepareAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(await proxy.Service.EchoAsync(new() { Value = 13 }, default).ConfigureAwait(false) == 20, "generated-real-unary-result");
        Task<int>? blocked = null;
        try
        {
            blocked = proxy.Service.EchoAsync(new() { Value = 101 }, default).AsTask();
            await ProbeService.BlockEntered.Task.GuardedWaitAsync(Guard).ConfigureAwait(false);
            Check(await proxy.Service.EchoAsync(new() { Value = 17 }, default).ConfigureAwait(false) == 24, "picker-uses-other-ready-session");
        }
        finally { ProbeService.BlockRelease.TrySetResult(); }
        if (blocked is not null) await blocked.GuardedWaitAsync(Guard).ConfigureAwait(false);
        var notificationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeService.NotifyRelease = notificationRelease;
        try
        {
            await proxy.Service.NotifyAsync(new() { Value = 31 }).AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false);
            await ProbeService.Notified.Task.GuardedWaitAsync(Guard).ConfigureAwait(false);
            Check(!notificationRelease.Task.IsCompleted, "oneway-completes-without-business-ack");
        }
        finally { notificationRelease.TrySetResult(); ProbeService.NotifyRelease = null; }
        using (var cancellation = new CancellationTokenSource())
        {
            var call = proxy.Service.EchoAsync(new() { Value = 99 }, cancellation.Token).AsTask(); cancellation.Cancel();
            _ = await ThrowsAsync<OperationCanceledException>(() => call, "unary-original-caller-cancellation").ConfigureAwait(false);
        }
        var sent = records.Frames.Where(f => f.Outbound && f.Header.Type == ProtocolV2FrameType.Request).ToArray();
        var acks = records.Frames.Where(f => !f.Outbound && f.Header.Type == ProtocolV2FrameType.BindAck)
            .Select(f => (f.SessionId, Ack: ContractBindingWire.ReadAck(new(f.Payload)))).ToArray();
        Check(acks.Select(a => a.SessionId).Distinct().Count() == 2 && sent.Select(f => f.SessionId).Distinct().Count() == 2, "two-real-session-bindings-and-invocations");
        foreach (var frame in sent)
        {
            var token = BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload);
            Check(acks.Any(a => a.SessionId == frame.SessionId && a.Ack.Token == token), "request-token-paired-with-actual-session");
        }
        // Equal numeric tokens are valid if each Session owns its own entry. Pairing is the oracle.
        var otherClient = harness.Client();
        var foreign = Throws<SharpLinkException>(() => otherClient.CreateContractChannel(client.GetLocalContract<IProbe>()), "foreign-local-selector");
        Check(foreign.DetailCode == SharpLinkErrorDetails.ContractBinding.ForeignLocalContract, "foreign-selector-detail");
        await QualifiedStaticClusterAsync(harness).ConfigureAwait(false);
    }
    private static async Task QualifiedStaticClusterAsync(Harness harness)
    {
        var records = new TransportFrameGate((ProtocolV2FrameType)255, false);
        var selector = new PreferBSelector();
        var client = await harness.ClusterClientAsync(records, selector).ConfigureAwait(false);
        var proxy = client.CreateProxy<IProbe>();
        await client.ConnectAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false);
        Check(client.GetReadinessSnapshot().ReadyEndpoints == 2, "cluster-both-transport-endpoints-ready");
        await proxy.PrepareAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(await proxy.Service.EchoAsync(new() { Value = 6 }, default).ConfigureAwait(false) == 13, "cluster-qualified-candidate-not-global-ready-first");
        var notifications = ProbeService.Notifications;
        await proxy.Service.NotifyAsync(new() { Value = 32 }).ConfigureAwait(false);
        await UntilAsync(() => ProbeService.Notifications > notifications, "cluster-qualified-oneway-delivered").ConfigureAwait(false);
        Check(selector.Calls >= 2 && !selector.SawIneligible, "original-custom-picker-sees-only-qualified-A-not-ready-unbound-B");
        foreach (var request in records.Frames.Where(f => f.Outbound && f.Header.Type == ProtocolV2FrameType.Request))
        {
            var token = BinaryPrimitives.ReadUInt64LittleEndian(request.Payload);
            Check(records.Frames.Any(f => !f.Outbound && f.Header.Type == ProtocolV2FrameType.BindAck && f.SessionId == request.SessionId && ContractBindingWire.ReadAck(new(f.Payload)).Token == token), "cluster-request-reserves-actual-bound-session");
        }
    }
    private sealed class PreferBSelector : ISharpLinkEndpointSelector
    {
        private int _calls, _ineligible;
        internal int Calls => Volatile.Read(ref _calls);
        internal bool SawIneligible => Volatile.Read(ref _ineligible) != 0;
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            Interlocked.Increment(ref _calls); var selected = -1;
            for (var i = 0; i < context.Count; i++)
            {
                if (context[i].Endpoint.Id != "A") Interlocked.Exchange(ref _ineligible, 1);
                if ((context.ExcludedMask & (1UL << i)) == 0)
                { if (context[i].Endpoint.Id == "B") return i; selected = i; }
            }
            return selected;
        }
    }
    private static async Task EarlyAckAsync(Harness harness)
    {
        ProbeService.BlockEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeService.BlockRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TransportFrameGate(ProtocolV2FrameType.BindContract, true); harness.Gates.Add(gate);
        var client = harness.Client(gate, 2); var proxy = client.CreateProxy<IProbe>();
        await client.ConnectAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false);
        var prepare = proxy.PrepareAsync(TimeSpan.FromSeconds(5)).AsTask(); Task<int>? blocked = null;
        try
        {
            await gate.Entered.Task.GuardedWaitAsync(Guard).ConfigureAwait(false);
            await UntilAsync(() => ((SharpLinkClient)client).CaptureContractBindingDiagnostics().ReceivedUnpublishedAcks == 1, "one-real-ack-still-before-publication-return", () => ((SharpLinkClient)client).CaptureContractBindingDiagnostics().ToString()).ConfigureAwait(false);
            await prepare.GuardedWaitAsync(Guard).ConfigureAwait(false);
            Check(((SharpLinkClient)client).CaptureContractBindingDiagnostics().Operations == 1, "partial-pool-prepare-does-not-wait-for-other-bind");
            blocked = proxy.Service.EchoAsync(new() { Value = 101 }, default).AsTask();
            await ProbeService.BlockEntered.Task.GuardedWaitAsync(Guard).ConfigureAwait(false);
            Check(await proxy.Service.EchoAsync(new() { Value = 4 }, default).ConfigureAwait(false) == 11, "partial-pool-normal-picker-uses-confirmed-session");
            foreach (var frame in gate.Frames.Where(f => f.Outbound && f.Header.Type == ProtocolV2FrameType.Request))
            {
                Check(frame.SessionId != gate.HeldSessionId, "unpublished-session-not-a-picker-candidate");
                var token = BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload);
                Check(gate.Frames.Any(ack => !ack.Outbound && ack.Header.Type == ProtocolV2FrameType.BindAck && ack.SessionId == frame.SessionId && ContractBindingWire.ReadAck(new(ack.Payload)).Token == token), "partial-pool-request-pairs-confirmed-session-token");
            }
        }
        finally { gate.Release(); ProbeService.BlockRelease.TrySetResult(); }
        if (blocked is not null) Check(await blocked.GuardedWaitAsync(Guard).ConfigureAwait(false) == 108, "partial-pool-active-call-completes");
        await UntilAsync(() => ((SharpLinkClient)client).CaptureContractBindingDiagnostics() is { Operations: 0, PublishedTargets: 2 },
            "held-peer-bind-eventually-publishes-complete-pair", () => ((SharpLinkClient)client).CaptureContractBindingDiagnostics().ToString()).ConfigureAwait(false);
        Check(await proxy.Service.EchoAsync(new() { Value = 4 }, default).ConfigureAwait(false) == 11, "both-pool-targets-usable-after-publication");
    }
    private static async Task CancelWaiterAsync(Harness harness)
    {
        var client = harness.Client(); var proxy = client.CreateProxy<IProbe>();
        await client.ConnectAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var first = proxy.PrepareAsync(TimeSpan.FromSeconds(5), cancellation.Token).AsTask();
        var second = proxy.PrepareAsync(TimeSpan.FromSeconds(5)).AsTask();
        try
        {
            await harness.ServerGate!.Entered.Task.GuardedWaitAsync(Guard).ConfigureAwait(false);
            cancellation.Cancel();
            _ = await ThrowsAsync<OperationCanceledException>(() => first, "prepare-cancel-is-waiter-local").ConfigureAwait(false);
            Check(!second.IsCompleted && ((SharpLinkClient)client).CaptureContractBindingDiagnostics().Operations == 1, "shared-bind-remains-live");
        }
        finally { harness.ServerGate!.Release(); }
        await second.GuardedWaitAsync(Guard).ConfigureAwait(false);
        Check(await proxy.Service.EchoAsync(new() { Value = 2 }, default).ConfigureAwait(false) == 9, "shared-bind-survives-other-waiter-cancel");
    }
    private static async Task RawGuardsAsync(Harness harness)
    {
        foreach (var minor in new ushort[] { 4, 5 })
        {
            await using var old = await RawPeer.ConnectAsync(harness.Port).ConfigureAwait(false);
            Check(await old.HandshakeAsync(minor).ConfigureAwait(false) is null, "reject-wire-minor-" + minor);
        }
        var beforeDecode = ProbePayloadCodec.Deserializes; var beforeActivation = ProbeService.Created;
        await using (var peer = await RawPeer.ConnectAsync(harness.Port).ConfigureAwait(false))
        {
            Check(await peer.HandshakeAsync().ConfigureAwait(false) is not null, "wire6-real-success");
            var wrong = await peer.BindAsync(1, BindRequest with { Hash = new(9, 9) }).ConfigureAwait(false);
            Check(wrong.Status == ContractBindStatus.HashMismatch && wrong.Token == 0, "wrong-hash-structured-refusal");
            var tokens = new List<ulong>();
            for (ulong operation = 2; operation <= 65; operation++)
            {
                var ack = await peer.BindAsync(operation, BindRequest).ConfigureAwait(false);
                Check(ack.Status == ContractBindStatus.Success && ContractBindingWire.IsToken(ack.Token), "real-binding-slot-" + operation);
                tokens.Add(ack.Token);
            }
            Check(tokens.Distinct().Count() == 64, "64-creator-bind-tokens-distinct");
            Check((await peer.BindAsync(66, BindRequest).ConfigureAwait(false)).Status == ContractBindStatus.Capacity, "65th-session-binding-refused");
            await peer.RequestAsync(1, (99UL << 32) | 1, EchoFacts.MethodId, ProtocolV2FrameFlags.HasReturn).ConfigureAwait(false);
            await ExpectErrorAsync(peer, SharpLinkErrorDetails.ContractBinding.UnknownBinding).ConfigureAwait(false);
            await peer.RequestAsync(2, tokens[0], long.MaxValue, ProtocolV2FrameFlags.HasReturn).ConfigureAwait(false);
            await ExpectErrorAsync(peer, SharpLinkErrorDetails.ContractBinding.UnknownMethod).ConfigureAwait(false);
            await peer.SendAsync(ProtocolV2FrameType.BindingRelease, ProtocolV2FrameFlags.None, 0, ContractBindingWire.WriteRelease(tokens[0])).ConfigureAwait(false);
            var replacement = await peer.BindAsync(67, BindRequest).ConfigureAwait(false);
            Check(replacement.Status == ContractBindStatus.Success && (uint)replacement.Token == (uint)tokens[0] && replacement.Token != tokens[0], "released-slot-new-incarnation");
            await peer.SendAsync(ProtocolV2FrameType.BindingRelease, ProtocolV2FrameFlags.None, 0, ContractBindingWire.WriteRelease(tokens[0])).ConfigureAwait(false);
            await peer.RequestAsync(3, tokens[0], EchoFacts.MethodId, ProtocolV2FrameFlags.HasReturn).ConfigureAwait(false);
            await ExpectErrorAsync(peer, SharpLinkErrorDetails.ContractBinding.UnknownBinding).ConfigureAwait(false);
            await peer.RequestAsync(4, replacement.Token, EchoFacts.MethodId, ProtocolV2FrameFlags.OneWay).ConfigureAwait(false);
            await ExpectClosedAsync(peer, "known-shape-conflict-closes-session").ConfigureAwait(false);
        }
        await UntilAsync(() => harness.Diagnostics.Bindings == 0 && harness.Diagnostics.Operations == 0 && harness.Diagnostics.ProtectedRequests == 0, "raw-table-and-operation-physical-return").ConfigureAwait(false);
        Check(ProbePayloadCodec.Deserializes == beforeDecode && ProbeService.Created == beforeActivation, "negative-wire-guards-before-codec-and-activation");
    }
    private static async Task ExpectClosedAsync(RawPeer peer, string name)
    {
        try
        {
            var frame = await peer.ReadAsync().ConfigureAwait(false);
            if (frame.Header.Type == ProtocolV2FrameType.GoAway)
            {
                _ = await peer.ReadAsync().ConfigureAwait(false);
            }
        }
        catch (IOException) { Check(true, name); return; }
        catch (SocketException) { Check(true, name); return; }
        throw new InvalidOperationException("ASSERT expected closed TCP Session: " + name);
    }
    private static async Task ExpectErrorAsync(RawPeer peer, ushort detail)
    {
        var response = await peer.ReadAsync().ConfigureAwait(false);
        Check(response.Header.Type == ProtocolV2FrameType.Response && (response.Header.Flags & ProtocolV2FrameFlags.Error) != 0, "negative-request-response");
        var error = ProtocolV2PayloadCodec.ReadError(new(response.Payload), response.Header.Flags, new SharpLinkProtocolOptions().MaxErrorMessageBytes);
        Check(error.DetailCode == detail, "negative-request-detail-" + detail);
    }
    private static async Task SiteWaiterBoundsAsync(Harness harness)
    {
        var client = harness.Client(); var local = client.GetLocalContract<IProbe>(); var channels = new List<IRpcContractChannel>();
        var server = Guid.Parse("77500000-0000-0000-0000-000000000003"); var epoch = Guid.Parse("77500000-0000-0000-0000-000000000004");
        for (var i = 1; i <= 64; i++) channels.Add(client.CreateContractChannel(local, new() { Receipt = new(server, epoch, 0, Contract.ContractId, Manifest.RpcAssemblyHash, i) }));
        var capacity = Throws<SharpLinkException>(() => client.CreateContractChannel(local, new() { Receipt = new(server, epoch, 0, Contract.ContractId, Manifest.RpcAssemblyHash, 65) }), "65th-live-demand-site-refused");
        Check(capacity.DetailCode == 60104, "demand-hard-bound-detail");
        using var cancel = new CancellationTokenSource();
        var waiters = channels.Select(channel => channel.PrepareAsync(TimeSpan.FromSeconds(5), cancel.Token).AsTask()).ToArray();
        try
        {
            var rejected = await ThrowsAsync<SharpLinkException>(() => channels[0].PrepareAsync(TimeSpan.FromSeconds(5)).AsTask(), "65th-prepare-waiter-refused").ConfigureAwait(false);
            Check(rejected.DetailCode == 60104 && ((SharpLinkClient)client).CaptureContractBindingDiagnostics().PrepareWaiters == 64, "waiter-hard-bound-and-reservation");
        }
        finally { cancel.Cancel(); }
        foreach (var waiter in waiters) _ = await ThrowsAsync<OperationCanceledException>(() => waiter, "bounded-waiter-cancel-return").ConfigureAwait(false);
        await client.StopAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false);
        var snapshot = ((SharpLinkClient)client).CaptureContractBindingDiagnostics();
        Check(snapshot.PrepareWaiters == 0 && snapshot.DemandSites == 0 && snapshot.Bindings == 0 && snapshot.Operations == 0, "stopped-client-demand-waiter-quota-zero");
        GC.KeepAlive(channels);
    }
    private static async Task QueuedReleaseAsync(Harness harness)
    {
        ProbeService.QueueBlockEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeService.QueueBlockRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var records = new TransportFrameGate((ProtocolV2FrameType)255, false); var client = harness.Client(records);
        var proxy = client.CreateProxy<IProbe>(); await client.ConnectAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false);
        await proxy.PrepareAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var first = proxy.Service.EchoAsync(new() { Value = 102 }, default).AsTask();
        try
        {
            await ProbeService.QueueBlockEntered.Task.GuardedWaitAsync(Guard).ConfigureAwait(false);
            var sourceHead = records.Frames.Last(f => f.Outbound && f.Header.Type == ProtocolV2FrameType.Request);
            var decodes = ProbePayloadCodec.Deserializes; var handlers = ProbeService.Echoes;
            await using var peer = await RawPeer.ConnectAsync(harness.Port).ConfigureAwait(false);
            _ = await peer.HandshakeAsync().ConfigureAwait(false);
            var ack = await peer.BindAsync(1, BindRequest).ConfigureAwait(false);
            Check(ack.Status == ContractBindStatus.Success, "queued-peer-real-bind");
            var head = sourceHead.Payload.ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(head, ack.Token);
            await peer.SendAsync(ProtocolV2FrameType.Request, sourceHead.Header.Flags, 1, head).ConfigureAwait(false);
            await UntilAsync(() => harness.QueuedCalls == 1, "real-admission-queue-waiting").ConfigureAwait(false);
            Check(harness.Diagnostics.ProtectedRequests == 2, "queue-holds-original-physical-reader-before-permit");
            await peer.SendAsync(ProtocolV2FrameType.BindingRelease, ProtocolV2FrameFlags.None, 0, ContractBindingWire.WriteRelease(ack.Token)).ConfigureAwait(false);
            var response = await peer.ReadAsync().ConfigureAwait(false);
            var error = ProtocolV2PayloadCodec.ReadError(new(response.Payload), response.Header.Flags, new SharpLinkProtocolOptions().MaxErrorMessageBytes);
            Check(error.Code == SharpLinkErrorCode.Unavailable, "release-cut-rejects-waiting-business");
            await UntilAsync(() => harness.QueuedCalls == 0 && harness.Diagnostics.ProtectedRequests == 1, "queued-cancel-physical-owner-return").ConfigureAwait(false);
            Check(ProbePayloadCodec.Deserializes == decodes && ProbeService.Echoes == handlers, "cut-winner-no-typed-codec-or-handler");
        }
        finally { ProbeService.QueueBlockRelease.TrySetResult(); }
        Check(await first.GuardedWaitAsync(Guard).ConfigureAwait(false) == 109, "claim-winner-keeps-original-handler");
        await UntilAsync(() => harness.Diagnostics.ProtectedRequests == 0, "active-claim-preserved-through-original-completion").ConfigureAwait(false);
    }
    private static async Task DisconnectAsync(Harness harness)
    {
        var records = new TransportFrameGate((ProtocolV2FrameType)255, false); var client = harness.Client(records);
        var proxy = client.CreateProxy<IProbe>(); await client.ConnectAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false);
        var preparation = proxy.PrepareAsync(TimeSpan.FromSeconds(5)).AsTask();
        await harness.ServerGate!.Entered.Task.GuardedWaitAsync(Guard).ConfigureAwait(false);
        var healthy = harness.Client(); var healthyProxy = healthy.CreateProxy<IProbe>();
        await healthy.ConnectAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false); await healthyProxy.PrepareAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var oldSession = records.Connection!.Id;
        await records.Connection.DisposeAsync().ConfigureAwait(false); harness.ServerGate.Release();
        await preparation.GuardedWaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Check(await proxy.Service.EchoAsync(new() { Value = 10 }, default).ConfigureAwait(false) == 17, "reconnect-new-session-bind-with-original-prepare-budget");
        Check(await healthyProxy.Service.EchoAsync(new() { Value = 5 }, default).ConfigureAwait(false) == 12, "other-session-unaffected-by-control-close");
        var frame = records.Frames.Last(f => f.Outbound && f.Header.Type == ProtocolV2FrameType.Request);
        var token = BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload);
        Check(frame.SessionId != oldSession && records.Frames.Any(f => !f.Outbound && f.Header.Type == ProtocolV2FrameType.BindAck && f.SessionId == frame.SessionId && ContractBindingWire.ReadAck(new(f.Payload)).Token == token), "reconnect-does-not-transplant-old-binding");
    }
    private static async Task TimeoutAsync(Harness harness)
    {
        // Consume one second of the client operation's original ten-second budget before the server starts its own budget.
        var sendGate = new TransportFrameGate(ProtocolV2FrameType.BindContract, false); harness.Gates.Add(sendGate);
        var client = harness.Client(sendGate, reconnectDelay: TimeSpan.FromSeconds(30)); var proxy = client.CreateProxy<IProbe>();
        await client.ConnectAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false);
        var preparation = proxy.PrepareAsync(TimeSpan.FromSeconds(13)).AsTask();
        await sendGate.Entered.Task.GuardedWaitAsync(Guard).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false); sendGate.Release();
        await harness.ServerGate!.Entered.Task.GuardedWaitAsync(Guard).ConfigureAwait(false);
        var error = await ThrowsAsync<SharpLinkException>(() => preparation.GuardedWaitAsync(TimeSpan.FromSeconds(13)), "bind-original-ten-second-timeout").ConfigureAwait(false);
        Check(error.Code == SharpLinkErrorCode.DeadlineExceeded, "bind-timeout-cause");
        harness.ServerGate.Release();
        await UntilAsync(() => ((SharpLinkClient)client).CaptureContractBindingDiagnostics().Operations == 0, "timed-out-operation-physical-return").ConfigureAwait(false);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ISharpLinkServer _server;
        private readonly List<ISharpLinkClient> _clients = [];
        private readonly List<ISharpLinkServer> _extraServers = [];
        internal readonly List<TransportFrameGate> Gates = [];
        internal TransportFrameGate? ServerGate { get; }
        internal int Port { get; }
        internal int QueuedCalls => ((SharpLinkServer)_server).AdmissionStateKernelForTests!.QueuedCalls;
        internal ContractBindingDiagnosticSnapshot Diagnostics => ((SharpLinkServer)_server).CaptureContractBindingDiagnostics();
        private Harness(ISharpLinkServer server, int port, TransportFrameGate? gate) { _server = server; Port = port; ServerGate = gate; if (gate is not null) Gates.Add(gate); }
        internal static async Task<Harness> CreateAsync(TransportFrameGate? gate, bool queued)
        {
            var builder = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback);
            if (queued) builder.UseAdmissionControl(options =>
            {
                options.Global.UseConcurrency(1); options.MaxQueuedCalls = 1;
                options.MaxQueuedBytes = 64 * 1024; options.MaxQueueDelay = TimeSpan.FromSeconds(2);
            });
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            if (gate is not null) builder.UseTransport(new GatedListener(builder.Transport!, gate));
            var server = builder.Build();
            try { await server.StartAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false); return new(server, port, gate); }
            catch (Exception primary)
            {
                gate?.Release();
                try { await server.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
                throw;
            }
        }
        internal ISharpLinkClient Client(TransportFrameGate? gate = null, int connections = 1, TimeSpan? reconnectDelay = null)
        {
            var builder = SharpClientBuilder.Create().UseRequestTimeout(TimeSpan.FromSeconds(2))
                .UseConnectionPool(options => { options.MinConnections = connections; options.MaxConnections = connections; });
            var delay = reconnectDelay ?? TimeSpan.FromMilliseconds(50);
            builder.UseReconnectPolicy(new(delay, delay, 1, 1, 1, TimeSpan.Zero));
            if (gate is null) builder.UseTcp(IPAddress.Loopback.ToString(), Port);
            else { Gates.Add(gate); builder.UseTransport(new GatedClientFactory(new SocketClientTransportFactory(new IPEndPoint(IPAddress.Loopback, Port)), gate)); }
            var client = builder.Build(); _clients.Add(client); return client;
        }
        internal async Task<ISharpLinkClient> ClusterClientAsync(TransportFrameGate records, ISharpLinkEndpointSelector selector)
        {
            var builder = SharpLinkServerBuilder.Create().DisableAutomaticServiceRegistration().UseTcp(0, IPAddress.Loopback);
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port; var server = builder.Build(); _extraServers.Add(server);
            await server.StartAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false);
            Gates.Add(records);
            var endpoints = new[] { new SharpLinkEndpoint { Id = "A", Address = new SharpLinkTcpAddress("127.0.0.1", Port) },
                new SharpLinkEndpoint { Id = "B", Address = new SharpLinkTcpAddress("127.0.0.1", port) } };
            var client = SharpClientBuilder.Create().UseRequestTimeout(TimeSpan.FromSeconds(2))
                .UseEndpoints(endpoints, endpoint => new GatedClientFactory(new SocketClientTransportFactory(new IPEndPoint(IPAddress.Loopback, ((SharpLinkTcpAddress)endpoint.Address).Port)), records))
                .UseCluster(options => { options.MinReadyEndpoints = 2; options.MaxConnections = 2; options.MaxConnectionsPerEndpoint = 1; })
                .UseEndpointSelector(selector)
                .UseReconnectPolicy(new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), 1, 1, 1, TimeSpan.Zero)).Build();
            _clients.Add(client); return client;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var gate in Gates) gate.Release();
            ProbeService.NotifyRelease?.TrySetResult(); ProbeService.BlockRelease.TrySetResult(); ProbeService.QueueBlockRelease.TrySetResult();
            var failures = new List<Exception>();
            foreach (var client in _clients)
            {
                try { await client.StopAsync().AsTask().GuardedWaitAsync(Guard).ConfigureAwait(false); }
                catch (Exception exception) { failures.Add(exception); }
            }
            foreach (var extra in _extraServers)
            {
                try { await extra.StopAsync(TimeSpan.FromSeconds(3)).AsTask().GuardedWaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
                catch (Exception exception) { failures.Add(exception); }
                var diagnostic = ((SharpLinkServer)extra).CaptureContractBindingDiagnostics();
                Console.WriteLine("N1_EXTRA_SERVER_FINAL " + diagnostic);
                if (diagnostic.Bindings != 0 || diagnostic.Operations != 0 || diagnostic.ProtectedRequests != 0)
                    failures.Add(new InvalidOperationException("ASSERT extra endpoint physical ledger must be zero."));
            }
            try { await _server.StopAsync(TimeSpan.FromSeconds(3)).AsTask().GuardedWaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
            if (failures.Count == 0)
            {
                try { await UntilAsync(() => Diagnostics.Bindings == 0 && Diagnostics.Operations == 0 && Diagnostics.ProtectedRequests == 0, "server-final-physical-ledger-zero").ConfigureAwait(false); }
                catch (Exception exception) { failures.Add(exception); }
            }
            Console.WriteLine("N1_SERVER_FINAL " + Diagnostics);
            if (failures.Count != 0) throw new AggregateException("FIXTURE cleanup failures", failures);
        }
    }
}
