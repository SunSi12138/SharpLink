using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.GroupPlugin;
using SharpLink.Runtime;

namespace SharpLink.BindingTests;

internal static partial class Program
{
    private sealed record ReceiptMessage(Guid Server, Guid Epoch, long Generation, long Contract,
        ulong High, ulong Low, long Registration)
    {
        internal ReceiptMessage(RpcRegistrationReceipt r) : this(r.ServerInstanceId, r.RegistryEpoch,
            r.Generation, r.ContractId, r.RpcAssemblyHash.High, r.RpcAssemblyHash.Low, r.RegistrationId) { }
        internal RpcRegistrationReceipt Receipt => new(Server, Epoch, Generation, Contract, new(High, Low), Registration);
    }

    private static ProtocolV2ContractManifest RemoteManifest(SharpLinkClient client)
        => Field<Dictionary<RpcSession, ProtocolV2ContractManifest>>(client, "_remoteContractManifests").Values.Single();
    private static ClientConnection DataConnection(SharpLinkClient client)
        => Field<ClientConnection[]>(client, "_readyConnections").Single();
    private static RpcProxyOptions Options(RpcRegistrationReceipt receipt, RpcRegistrationRequirement requirement)
        => new() { Receipt = receipt, Requirement = requirement };
    private static StaticBindingScope Scope(RpcRegistrationReceipt receipt, RpcRegistrationRequirement requirement)
        => new(requirement, receipt);
    private static async Task<RpcRegistrationReceipt> ReplaceViaControlAsync(GroupFixture f, RpcProxy<ISliceRpc> control,
        LoadedGroup old, LoadedGroup next, CancellationToken token)
    {
        f.ControlService.ReplaceGroup = async () =>
        {
            var replaced = await f.Server.ReplaceAssemblyWithReceiptsAsync(old.Assembly, next.Assembly, TimeSpan.FromSeconds(5), token);
            if (!replaced.Replacement.Succeeded) throw new InvalidOperationException("Control replacement failed.");
            return JsonSerializer.Serialize(new ReceiptMessage(replaced.Receipts.Single(receipt => receipt.ContractId == f.Contract().ContractId)));
        };
        try { return JsonSerializer.Deserialize<ReceiptMessage>(await control.Service.ReplaceGroupAsync())!.Receipt; }
        finally { f.ControlService.ReplaceGroup = null; }
    }

    // Invoke the real generic public factory for the collectible, changed-hash local type.
    // The test consumes only int results; no Type or delegate from the server ALC crosses here.
    private sealed class ReceiptProxy
    {
        private readonly object _handle;
        private readonly object _service;
        internal ReceiptProxy(SharpLinkClient client, Type contract, RpcProxyOptions options)
        {
            _handle = typeof(SharpLinkClient).GetMethod(nameof(SharpLinkClient.CreateProxy))!
                .MakeGenericMethod(contract).Invoke(client, [options])!;
            _service = _handle.GetType().GetProperty("Service")!.GetValue(_handle)!;
        }
        internal ValueTask PrepareAsync(CancellationToken token)
            => (ValueTask)_handle.GetType().GetMethod("PrepareAsync")!.Invoke(_handle, [TimeSpan.FromSeconds(5), token])!;
        internal ValueTask<int> GetAsync()
            => (ValueTask<int>)_service.GetType().GetMethod(nameof(IHotRpc.GetAsync))!.Invoke(_service, null)!;
    }

    private static async Task RunReceiptAsync(string transport, CancellationToken token)
    {
        ReceiptWireControls();
        await StaleManifestReceiptAsync(transport, token);
        await ReceiptLateAckAsync(transport, token);
        await ReceiptCommitInvalidationAsync(transport, token);
    }

    private static void ReceiptWireControls()
    {
        var bytes = new byte[StaticBindingWire.RequestBytes];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), 3);
        try { StaticBindingWire.ReadRequest(new(bytes)); throw new InvalidOperationException("Unknown requirement accepted."); }
        catch (SharpLinkProtocolViolationException) { Check(true, "receipt wire rejects unknown requirement"); }
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), 1);
        try { StaticBindingWire.ReadRequest(new(bytes)); throw new InvalidOperationException("Missing receipt accepted."); }
        catch (SharpLinkProtocolViolationException) { Check(true, "receipt wire rejects strict requirement without receipt"); }
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), 0);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), 1);
        try { StaticBindingWire.ReadRequest(new(bytes)); throw new InvalidOperationException("Truncated receipt accepted."); }
        catch (SharpLinkProtocolViolationException) { Check(true, "receipt wire rejects presence length mismatch"); }
        Check(!typeof(RpcRegistrationReceipt).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => !field.FieldType.IsValueType), "receipt contains only scalar values without collectible roots");
    }

    private static async Task StaleManifestReceiptAsync(string transport, CancellationToken token)
    {
        await using var f = new GroupFixture(transport);
        var old = new LoadedGroup(GroupPath(), 1, new GroupSignals());
        var nextSignals = new GroupSignals();
        var next = new LoadedGroup(GroupPath(true), 2, nextSignals);
        var last = new LoadedGroup(GroupPath(true), 3, new GroupSignals());
        var local = new LoadedGroup(GroupPath(true), 0, new GroupSignals());
        var unrelated = new LoadedGroup(Path.GetFullPath("test/SharpLink.BindingTests/GroupPluginUnrelated/bin/Release/net10.0/SharpLink.GroupPluginUnrelated.dll"), 4, new GroupSignals());
        if (!f.Server.RegisterAssembly(old.Assembly).Succeeded) throw new InvalidOperationException("Register failed.");
        var control = f.Client.CreateProxy<ISliceRpc>();
        await f.ConnectAsync(token); await control.PrepareAsync(TimeSpan.FromSeconds(5), token);
        var data = f.CreateClient(local.Manifest);
        var contract = local.Manifest.Contracts.Single(c => c.ContractType.Name == nameof(IHotRpc));
        var selector = data.GetLocalContract(local.Assembly, contract.ContractId);
        var ordinary = (StaticBindingCallSite)data.CreateContractChannel(selector);
        Check(ordinary.Snapshot.Targets.Length == 0 && data.ReadyConnectionCount == 0, "receipt local factory remains independent of Connect");
        await data.ConnectAsync(token);
        var dataConnection = DataConnection(data);
        var beforeManifest = RemoteManifest(data);
        Check(beforeManifest.ServerInstanceId != Guid.Empty && beforeManifest.RegistryEpoch != Guid.Empty &&
            beforeManifest.Contracts[contract.ContractId] == old.Manifest.RpcAssemblyHash &&
            beforeManifest.Contracts[contract.ContractId] != local.Manifest.RpcAssemblyHash,
            "receipt data handshake verifies peer while retaining old hash");
        Check(f.Connections.All(c => !ReferenceEquals(c.Session, dataConnection.Session)), "receipt control and data use distinct physical Sessions");
        await ExpectCode(() => ordinary.PrepareAsync(TimeSpan.FromSeconds(5), token).AsTask(), SharpLinkErrorCode.FailedPrecondition,
            "receipt ordinary path cannot bypass stale Manifest compatibility");
        Check(dataConnection.StaticBindings.Sent == 0, "receipt ordinary mismatch emits no Bind");
        var broadcastEntered = Signal(); var broadcastRelease = Signal();
        f.Server.ContractManifestPublishBarrierForTesting = async () => { broadcastEntered.TrySetResult(); await broadcastRelease.Task; };
        try
        {
            var receipt = await ReplaceViaControlAsync(f, control, old, next, token);
            await broadcastEntered.Task.WaitAsync(token);
            Check(receipt.ServerInstanceId == beforeManifest.ServerInstanceId && receipt.RegistryEpoch == beforeManifest.RegistryEpoch &&
                receipt.RpcAssemblyHash == local.Manifest.RpcAssemblyHash && receipt.Generation > beforeManifest.Generation,
                "receipt control RPC returns exact published replacement identity");
            Check(ReferenceEquals(RemoteManifest(data), beforeManifest), "receipt control response does not wait for data Manifest broadcast");
            var exactOptions = Options(receipt, RpcRegistrationRequirement.ExactRegistration);
            var exact = (StaticBindingCallSite)data.CreateContractChannel(selector, exactOptions);
            var duplicate = data.CreateContractChannel(selector, exactOptions);
            Check(ReferenceEquals(exact, duplicate) && !ReferenceEquals(exact, ordinary), "receipt site key preserves full immutable requirement");
            var generated = new ReceiptProxy(data, contract.ContractType, exactOptions);
            await generated.PrepareAsync(token);
            Check(await generated.GetAsync() == 2 && exact.Snapshot.Targets.Length == 1,
                "receipt generated Service and Prepare share exact changed hash site");
            var ack = await dataConnection.StaticBindings.BindAsync(contract.ContractId, local.Manifest.RpcAssemblyHash,
                Scope(receipt, RpcRegistrationRequirement.ExactRegistration));
            Check(ack.RegistrationId == receipt.RegistrationId && ack.Generation == receipt.Generation &&
                dataConnection.StaticBindings.Sent == 1, "receipt exact BindAck pins own registration and coalesces duplicate factories");
            Check(ReferenceEquals(RemoteManifest(data), beforeManifest), "receipt real bound RPC succeeds while data Manifest remains old");
            var method = contract.Methods.Single(m => m.Name == nameof(IHotRpc.GetAsync));
            var descriptor = new RpcMethodDescriptor(contract.ContractId, method.MethodId, RpcMethodKind.Unary, true, false, false, null);
            Check(await exact.InvokeUnaryAsync(descriptor, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance,
                data.RuntimeContext.Codecs.GetCodec<int>(), null, token) == 2,
                "receipt handwritten channel uses same exact registration as generated service");

            var minimum = (StaticBindingCallSite)data.CreateContractChannel(selector, Options(receipt, RpcRegistrationRequirement.MinimumVersion));
            var compatible = (StaticBindingCallSite)data.CreateContractChannel(selector, Options(receipt, RpcRegistrationRequirement.Compatible));
            await minimum.PrepareAsync(TimeSpan.FromSeconds(5), token); await compatible.PrepareAsync(TimeSpan.FromSeconds(5), token);
            Check(!ReferenceEquals(exact, minimum) && !ReferenceEquals(minimum, compatible) && dataConnection.StaticBindings.CompletedCount == 3,
                "receipt equal hash with distinct requirements never weakens coalescing key");
            var altered = receipt with { Generation = receipt.Generation + 100, RegistrationId = receipt.RegistrationId + 100 };
            var otherCompatible = (StaticBindingCallSite)data.CreateContractChannel(selector, Options(altered, RpcRegistrationRequirement.Compatible));
            await otherCompatible.PrepareAsync(TimeSpan.FromSeconds(5), token);
            Check(!ReferenceEquals(compatible, otherCompatible) && dataConnection.StaticBindings.CompletedCount == 4,
                "receipt original scalar receipt remains part of Compatible site and Bind key");

            var sent = dataConnection.StaticBindings.Sent;
            var wrongServer = data.CreateContractChannel(selector, Options(receipt with { ServerInstanceId = Guid.NewGuid() }, RpcRegistrationRequirement.ExactRegistration));
            var wrongEpoch = data.CreateContractChannel(selector, Options(receipt with { RegistryEpoch = Guid.NewGuid() }, RpcRegistrationRequirement.ExactRegistration));
            await ExpectCode(() => wrongServer.PrepareAsync(TimeSpan.FromSeconds(5), token).AsTask(), SharpLinkErrorCode.FailedPrecondition,
                "receipt wrong server fails explicit Prepare without waiting for timeout");
            await ExpectCode(() => wrongEpoch.PrepareAsync(TimeSpan.FromSeconds(5), token).AsTask(), SharpLinkErrorCode.FailedPrecondition,
                "receipt wrong epoch fails explicit Prepare without waiting for timeout");
            Check(dataConnection.StaticBindings.Sent == sent, "receipt wrong peer eligibility emits no Bind or business work");
            await ExpectCode(() => { data.CreateContractChannel(selector, Options(receipt with { ContractId = long.MaxValue }, RpcRegistrationRequirement.Compatible)); return Task.CompletedTask; },
                SharpLinkErrorCode.FailedPrecondition, "receipt wrong local contract fails at factory");
            await ExpectCode(() => { data.CreateContractChannel(selector, Options(receipt with { RpcAssemblyHash = new(1, 2) }, RpcRegistrationRequirement.Compatible)); return Task.CompletedTask; },
                SharpLinkErrorCode.FailedPrecondition, "receipt wrong local hash fails at factory");
            await ExpectCode(() => { data.CreateContractChannel(selector, new RpcProxyOptions { Requirement = RpcRegistrationRequirement.MinimumVersion }); return Task.CompletedTask; },
                SharpLinkErrorCode.FailedPrecondition, "receipt strict requirement without receipt fails locally");
            await ExpectCode(() => { data.CreateContractChannel(selector, new RpcProxyOptions { Requirement = (RpcRegistrationRequirement)99 }); return Task.CompletedTask; },
                SharpLinkErrorCode.FailedPrecondition, "receipt invalid requirement fails locally");

            var gets = nextSignals.Count("get");
            var activations = nextSignals.Count("activate");
            await ExpectCode(() => dataConnection.StaticBindings.BindAsync(contract.ContractId, local.Manifest.RpcAssemblyHash,
                Scope(receipt with { ServerInstanceId = Guid.NewGuid() }, RpcRegistrationRequirement.ExactRegistration)), SharpLinkErrorCode.FailedPrecondition,
                "receipt server independently rejects forged peer identity on wire");
            await ExpectCode(() => dataConnection.StaticBindings.BindAsync(contract.ContractId, local.Manifest.RpcAssemblyHash,
                Scope(receipt with { RegistryEpoch = Guid.NewGuid() }, RpcRegistrationRequirement.ExactRegistration)), SharpLinkErrorCode.FailedPrecondition,
                "receipt server independently rejects forged epoch on wire");
            await ExpectCode(() => dataConnection.StaticBindings.BindAsync(contract.ContractId, local.Manifest.RpcAssemblyHash,
                Scope(receipt with { RegistrationId = receipt.RegistrationId + 1 }, RpcRegistrationRequirement.ExactRegistration)), SharpLinkErrorCode.FailedPrecondition,
                "receipt server rejects wrong exact registration despite matching hash");
            var future = receipt with { Generation = f.Registry.GenerationStorage + 1 };
            if (!f.Server.RegisterAssembly(unrelated.Assembly).Succeeded) throw new InvalidOperationException("Unrelated registration failed.");
            Check(f.Registry.GenerationStorage >= future.Generation && f.Module(next).BindingPublicationGeneration == receipt.Generation,
                "receipt unrelated publication advances global generation but not bound registration");
            await ExpectCode(() => dataConnection.StaticBindings.BindAsync(contract.ContractId, local.Manifest.RpcAssemblyHash,
                Scope(future, RpcRegistrationRequirement.MinimumVersion)), SharpLinkErrorCode.FailedPrecondition,
                "receipt MinimumVersion compares own registration not global generation");
            Check(nextSignals.Count("get") == gets && nextSignals.Count("activate") == activations &&
                dataConnection.StaticBindings.PendingCount == 0 && data.BindingBudget.Operations == 0,
                "receipt refused controls perform zero activation and return operation quotas");
            await ExpectCode(() => { f.Server.GetRegistrationReceipt(old.Assembly, contract.ContractId); return Task.CompletedTask; },
                SharpLinkErrorCode.FailedPrecondition, "receipt cannot be captured from superseded assembly owner");

            var oldTarget = exact.Snapshot.Targets.Single();
            var invalidated = Signal();
            dataConnection.StaticBindings.InvalidationObservedTestHook = () => invalidated.TrySetResult();
            var lastReceipt = await ReplaceViaControlAsync(f, control, next, last, token);
            await invalidated.Task.WaitAsync(token);
            var stale = new StaticBindingCallSite(data, selector, exact.Scope) { Snapshot = StaticBindingSnapshot.Create(exact.Snapshot.Topology, [oldTarget]) };
            await ExpectCode(() => stale.InvokeUnaryAsync(descriptor, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance,
                data.RuntimeContext.Codecs.GetCodec<int>(), null, token).AsTask(), SharpLinkErrorCode.Unavailable,
                "receipt old binding cannot retarget same hash replacement");
            await WaitUntilAsync(data, () => exact.Snapshot.Targets.Length == 0, token);
            await ExpectCode(() => exact.PrepareAsync(TimeSpan.FromSeconds(5), token).AsTask(), SharpLinkErrorCode.FailedPrecondition,
                "receipt ExactRegistration never automatically downgrades after replacement");
            await minimum.PrepareAsync(TimeSpan.FromSeconds(5), token);
            var minimumAck = await dataConnection.StaticBindings.BindAsync(contract.ContractId, local.Manifest.RpcAssemblyHash, minimum.Scope);
            Check(minimumAck.RegistrationId == lastReceipt.RegistrationId && minimumAck.Generation >= receipt.Generation,
                "receipt MinimumVersion may bind newer registration without changing old token meaning");
            Check(await minimum.InvokeUnaryAsync(descriptor, default(RpcEmptyRequest), RpcEmptyRequestCodec.Instance,
                data.RuntimeContext.Codecs.GetCodec<int>(), null, token) == 3,
                "receipt newly confirmed minimum binding invokes newer generation");
            Check(ReferenceEquals(RemoteManifest(data), beforeManifest), "receipt repeated replacement still does not require Manifest broadcast");
        }
        finally { broadcastRelease.TrySetResult(); f.Server.ContractManifestPublishBarrierForTesting = null; }
        await data.StopAsync(token); local.Drop();
        await UnregisterAndDropGroupAsync(f, last, token); await UnregisterAndDropGroupAsync(f, unrelated, token);
        old.Drop(); next.Drop();
    }

    private static async Task WaitUntilAsync(SharpLinkClient client, Func<bool> condition, CancellationToken token)
    {
        while (true)
        {
            var changed = Field<TaskCompletionSource>(client, "_staticBindingChanged");
            if (condition()) return;
            await changed.Task.WaitAsync(token);
        }
    }

    private static async Task ReceiptLateAckAsync(string transport, CancellationToken token)
    {
        await using var f = new GroupFixture(transport);
        var old = new LoadedGroup(GroupPath(), 1, new GroupSignals());
        var next = new LoadedGroup(GroupPath(), 2, new GroupSignals());
        if (!f.Server.RegisterAssembly(old.Assembly).Succeeded) throw new InvalidOperationException("Register failed.");
        var receipt = f.Server.GetRegistrationReceipt(old.Assembly, f.Contract().ContractId);
        await f.ConnectAsync(token);
        var connection = f.Connections[0];
        var entered = Signal(); var release = Signal(); var invalidated = Signal();
        f.Server.BeforeBindingAckTestHook = async (_, _, _, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        connection.StaticBindings.InvalidationObservedTestHook = () => invalidated.TrySetResult();
        try
        {
            var binding = connection.StaticBindings.BindAsync(receipt.ContractId, receipt.RpcAssemblyHash, Scope(receipt, RpcRegistrationRequirement.ExactRegistration));
            await entered.Task.WaitAsync(token);
            var result = await ReplaceGroupAtBoundary(f, old, next, token);
            await invalidated.Task.WaitAsync(token);
            Check(result.Succeeded && !binding.IsCompleted, "receipt registration retires before held exact BindAck arrives");
            old.Drop();
            GCheck(Collected(old.Weak), "receipt", "late exact Ack and receipt caches leave old ALC collectible");
            release.TrySetResult();
            await ExpectCode(() => binding, SharpLinkErrorCode.Unavailable, "receipt invalidated late exact Ack cannot publish readiness");
            f.Server.BeforeBindingAckTestHook = null;
            await ExpectCode(() => connection.StaticBindings.BindAsync(receipt.ContractId, receipt.RpcAssemblyHash,
                Scope(receipt, RpcRegistrationRequirement.ExactRegistration)), SharpLinkErrorCode.FailedPrecondition,
                "receipt retired exact requirement cannot reuse weaker same hash binding");
            var ack = await connection.StaticBindings.BindAsync(receipt.ContractId, receipt.RpcAssemblyHash,
                Scope(receipt, RpcRegistrationRequirement.MinimumVersion)).WaitAsync(token);
            Check(ack.RegistrationId != receipt.RegistrationId && ack.Generation >= receipt.Generation &&
                await f.CallAsync(0, ack, nameof(IHotRpc.GetAsync), token) == 2,
                "receipt late Ack recovery requires fresh minimum Bind confirmation");
            Check(connection.StaticBindings.PendingCount == 0 && f.Client.BindingBudget.Operations == 0,
                "receipt late refusal and recovery reclaim operation reservations");
        }
        finally { release.TrySetResult(); f.Server.BeforeBindingAckTestHook = null; }
        await UnregisterAndDropGroupAsync(f, next, token);
    }

    private static async Task ReceiptCommitInvalidationAsync(string transport, CancellationToken token)
    {
        await using var f = new GroupFixture(transport);
        var old = new LoadedGroup(GroupPath(), 1, new GroupSignals());
        var next = new LoadedGroup(GroupPath(), 2, new GroupSignals());
        if (!f.Server.RegisterAssembly(old.Assembly).Succeeded) throw new InvalidOperationException("Register failed.");
        var receipt = f.Server.GetRegistrationReceipt(old.Assembly, f.Contract().ContractId);
        await f.ConnectAsync(token);
        var entered = Signal(); var release = Signal(); var commits = 0;
        f.Client.BeforeBindingSiteCommitTestHook = async () =>
        { if (Interlocked.Increment(ref commits) == 1) { entered.TrySetResult(); await release.Task; } };
        try
        {
            var options = Options(receipt, RpcRegistrationRequirement.ExactRegistration);
            var proxy = f.Client.CreateProxy<IHotRpc>(options);
            var site = (StaticBindingCallSite)f.Client.CreateContractChannel(f.Client.GetLocalContract<IHotRpc>(), options);
            var prepare = proxy.PrepareAsync(TimeSpan.FromSeconds(5), token).AsTask();
            await entered.Task.WaitAsync(token);
            Check(f.Connections.All(c => c.StaticBindings.CompletedCount == 1) && site.Snapshot.Targets.Length == 0 && !prepare.IsCompleted,
                "receipt committed Acks remain unready while actual site publication is held");
            var invalidated = new[] { Signal(), Signal() };
            for (var i = 0; i < 2; i++) { var index = i; f.Connections[i].StaticBindings.InvalidationObservedTestHook = () => invalidated[index].TrySetResult(); }
            await ReplaceGroupAtBoundary(f, old, next, token);
            await Task.WhenAll(invalidated.Select(signal => signal.Task)).WaitAsync(token);
            Check(f.Connections.All(c => c.StaticBindings.CompletedCount == 0) && site.Snapshot.Targets.Length == 0,
                "receipt invalidation detaches confirmed state before delayed site commit");
            old.Drop();
            GCheck(Collected(old.Weak), "receipt", "delayed site publication retains only scalar targets and old ALC is collectible");
            release.TrySetResult();
            await ExpectCode(() => prepare, SharpLinkErrorCode.FailedPrecondition,
                "receipt delayed site commit cannot resurrect invalidated exact readiness");
            await ExpectCode(() => proxy.Service.GetAsync().AsTask(), SharpLinkErrorCode.Unavailable,
                "receipt invalidated generated service stays fail fast without business replay");
            var minimum = f.Client.CreateProxy<IHotRpc>(Options(receipt, RpcRegistrationRequirement.MinimumVersion));
            await minimum.PrepareAsync(TimeSpan.FromSeconds(5), token);
            Check(await minimum.Service.GetAsync() == 2 && f.Client.BindingBudget.Operations == 0,
                "receipt post commit race recovery confirms fresh minimum binding and returns quotas");
        }
        finally { release.TrySetResult(); f.Client.BeforeBindingSiteCommitTestHook = null; }
        await UnregisterAndDropGroupAsync(f, next, token);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static ValueTask<SharpLinkAssemblyReplacementResult> ReplaceGroupAtBoundary(
        GroupFixture fixture, LoadedGroup old, LoadedGroup next, CancellationToken token)
        // Synchronous forwarding pops the Assembly-argument frame before the caller can
        // check collection, even when all subsequent awaits complete synchronously.
        => fixture.Server.ReplaceAssemblyAsync(old.Assembly, next.Assembly, TimeSpan.FromSeconds(5), token);
}
