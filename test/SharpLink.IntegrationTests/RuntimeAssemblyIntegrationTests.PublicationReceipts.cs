using System.Runtime.CompilerServices;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task ServerRegistrationReceiptResultMustBeAtomicAndImmutable()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("atomic-registration-receipt");
        plugin.ResetServiceState();
        Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded, "client local contract");
        var contractOnly = harness.Server.RegisterAssemblyWithReceipts(plugin.ContractAssembly);
        Ensure(contractOnly.Registration.Succeeded && contractOnly.Receipts.Count == 0,
            "a contract-only publication has no fictitious service receipt");
        var result = harness.Server.RegisterAssemblyWithReceipts(plugin.ServiceAssembly);
        Ensure(result.Registration.Succeeded && result.Receipts.Count > 0, "service publication returns receipts");
        Ensure(result.Receipts.Select(static receipt => receipt.ContractId).SequenceEqual(
                result.Receipts.Select(static receipt => receipt.ContractId).Order()),
            "the immutable publication inventory is ContractId ordered");
        var module = GetDynamicModule(harness.Client, plugin.ContractAssembly);
        var contract = module.Manifest.Contracts.Single(item => item.ContractType == plugin.ContractType);
        var receipt = result.Receipts.Single(item => item.ContractId == contract.ContractId);
        Ensure(receipt == harness.Server.GetRegistrationReceipt(plugin.ServiceAssembly, contract.ContractId) &&
               receipt.RpcAssemblyHash == module.Manifest.RpcAssemblyHash && plugin.GetStaticInt("Created") == 0,
            "the receipt identifies this Service publication with its separate Contract hash and no activation");
        var rejected = harness.Server.RegisterAssemblyWithReceipts(plugin.ServiceAssembly);
        Ensure(!rejected.Registration.Succeeded && rejected.Receipts.Count == 0 &&
               rejected.Registration.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
            "a rejected duplicate cannot mint another receipt");
        var list = (IList<RpcRegistrationReceipt>)result.Receipts;
        Ensure(list.IsReadOnly, "the returned inventory must not expose a mutable array");
        try { list[0] = default; throw new Exception("assert failed: publication receipts must be immutable"); }
        catch (NotSupportedException) { }
        var handle = CreateLocalProxyHandle(harness.Client, typeof(ISharpLinkClient), plugin.ContractType,
            new RpcProxyOptions { Receipt = receipt, Requirement = RpcRegistrationRequirement.ExactRegistration });
        await PrepareHandleAsync(handle);
        Ensure(plugin.GetStaticInt("Created") == 0, "receipt binding does not activate business");
        Ensure(await InvokeValueTaskAsync<int>(GetHandleService(handle), plugin.ContractType,
            "UnaryAsync", 41, CancellationToken.None) == 42, "the returned receipt prepares a real exact receiver");
    }

    [Test]
    [NotInParallel]
    public async Task ServerReplacementReceiptsMustNotResolveLatestAfterOldDrain()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("atomic-replacement-receipt");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin);
        var proxy = await CreatePreparedServiceAsync(harness.Client, plugin.ContractType);
        var context2 = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var context3 = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var assembly2 = context2.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
        var assembly3 = context3.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
        var contractId = GetDynamicModule(harness.Client, plugin.ContractAssembly).Manifest.Contracts
            .Single(item => item.ContractType == plugin.ContractType).ContractId;
        Task<int>? blocked = null;
        try
        {
            blocked = InvokeValueTaskAsync<int>(proxy, plugin.ContractType,
                "BlockIgnoringCancellationAsync", CancellationToken.None).AsTask();
            await plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));
            var replacing = harness.Server.ReplaceAssemblyWithReceiptsAsync(plugin.ServiceAssembly,
                assembly2, TimeSpan.FromSeconds(5)).AsTask();
            var expected2 = harness.Server.GetRegistrationReceipt(assembly2, contractId);
            Ensure(!replacing.IsCompleted, "R1 physical work holds the first result wait after R2 publication");
            var latest = await harness.Server.ReplaceAssemblyWithReceiptsAsync(assembly2,
                assembly3, TimeSpan.FromSeconds(5));
            EnsureReplacementReleased(latest.Replacement, "idle R2 replacement");
            var expected3 = latest.Receipts.Single(item => item.ContractId == contractId);
            Ensure(expected3.RegistrationId != expected2.RegistrationId && expected3.Generation > expected2.Generation,
                "R3 has its own exact service publication, not a global-generation alias");
            Ensure(!replacing.IsCompleted && plugin.GetStaticInt("Created") == 1,
                "publishing R3 cannot complete or retarget the held original R1 call");
            plugin.ReleaseBlock();
            Ensure(await blocked.WaitAsync(TimeSpan.FromSeconds(2)) == 43, "R1 completes on its original owner");
            var original = await replacing.WaitAsync(TimeSpan.FromSeconds(5));
            EnsureReplacementReleased(original.Replacement, "held R1 replacement");
            Ensure(original.Receipts.Single(item => item.ContractId == contractId) == expected2,
                "the delayed result returns R2 captured at publication, never the current R3");
            var stale = CreateLocalProxyHandle(harness.Client, typeof(ISharpLinkClient), plugin.ContractType,
                new RpcProxyOptions { Receipt = expected2, Requirement = RpcRegistrationRequirement.ExactRegistration });
            try { await PrepareHandleAsync(stale); throw new Exception("assert failed: retired R2 must not bind R3"); }
            catch (SharpLinkException exception)
            { Ensure(exception.Code == SharpLinkErrorCode.FailedPrecondition, "old exact receipt is not upgraded"); }
            var live = CreateLocalProxyHandle(harness.Client, typeof(ISharpLinkClient), plugin.ContractType,
                new RpcProxyOptions { Receipt = expected3, Requirement = RpcRegistrationRequirement.ExactRegistration });
            await PrepareHandleAsync(live);
            Ensure(await InvokeValueTaskAsync<int>(GetHandleService(live), plugin.ContractType,
                "UnaryAsync", 41, CancellationToken.None) == 42, "R3 receipt prepares its exact new registration");
        }
        finally
        {
            plugin.ReleaseBlock();
            if (blocked is not null) await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            context2.Unload();
            context3.Unload();
        }
    }

    [Test]
    [NotInParallel]
    public async Task ServerReceiptResultsMustNotRootReleasedCollectibleContexts()
    {
        var (publication, tracked) = await PublishReceiptThenUnloadAsync();
        for (var attempt = 0; attempt < 30 && tracked.AnyAlive; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(50);
        }
        Ensure(!tracked.AnyAlive, $"retained scalar publication result roots old owners: {tracked.AliveNames}");
        Ensure(publication.Registration.Succeeded && publication.Receipts.Count > 0,
            "the original immutable result remains usable after its collectible owners unload");
        GC.KeepAlive(publication);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(SharpLinkServerAssemblyRegistrationResult Publication, TrackedWeakReferences Tracked)>
        PublishReceiptThenUnloadAsync()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("retained-publication-receipt-unload");
        Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded, "receipt ALC client registration");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded, "receipt ALC contract registration");
        var publication = harness.Server.RegisterAssemblyWithReceipts(plugin.ServiceAssembly);
        Ensure(publication.Registration.Succeeded, "receipt ALC service registration");
        var tracked = CapturePluginRuntimeObjects(harness.Client, plugin.ContractAssembly);
        tracked.Add("ReceiptContractAssembly", plugin.ContractAssembly);
        tracked.Add("ReceiptServiceAssembly", plugin.ServiceAssembly);
        EnsureUnregisterReleased(await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2)), "receipt ALC service");
        EnsureUnregisterReleased(await harness.Server.UnregisterAssemblyAsync(plugin.ContractAssembly,
            TimeSpan.FromSeconds(2)), "receipt ALC contract");
        EnsureUnregisterReleased(await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly,
            TimeSpan.FromSeconds(2)), "receipt ALC client");
        tracked.Add("ReceiptAssemblyLoadContext", plugin.Unload());
        return (publication, tracked);
    }
}
