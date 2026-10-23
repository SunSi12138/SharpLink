using System.Reflection;
using System.Runtime.Loader;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task QueuedCallRetiredByCompatibleReplacementShouldNotRetarget()
        => await VerifyQueuedModuleCancellationAsync(replaceWhileQueued: true);

    [Test]
    [NotInParallel]
    public async Task QueuedCallWithoutReplacementShouldObserveItsModuleCancellation()
        => await VerifyQueuedModuleCancellationAsync(replaceWhileQueued: false);

    private static async Task VerifyQueuedModuleCancellationAsync(bool replaceWhileQueued)
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("queued-replacement-original");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin);
        var proxy = GetProxy(harness.Client, plugin.ContractType);
        var server = (SharpLinkServer)harness.Server;
        harness.Server.EnableAdmissionControl(options =>
        {
            options.Global.UseConcurrency(1);
            options.MaxQueuedCalls = 1;
            options.MaxQueuedBytes = 64 * 1024;
            options.MaxQueueDelay = TimeSpan.FromSeconds(30);
        });
        var admission = server.CurrentAdmissionProgramForTests!;
        var holder = await admission.Controller.AcquireAsync(
            new SharpLinkAdmissionContext(101, 202, RpcMethodKind.Unary, "test-holder", null, null),
            1, false, default, CancellationToken.None);
        Ensure(holder.IsAcquired, "an external test holder must occupy the only admission permit");

        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        Assembly? replacementAssembly = null;
        Type? replacementType = null;
        Task<int>? queued = null;
        try
        {
            replacementAssembly = replacementContext.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
            replacementType = replacementAssembly.GetType(
                "SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            replacementType.GetMethod("Reset")!.Invoke(null, null);
            queued = InvokeValueTaskAsync<int>(proxy, plugin.ContractType, "BlockAsync", CancellationToken.None)
                .AsTask();
            await WaitUntilAsync(() => admission.Kernel.QueuedCalls == 1);

            var oldModule = GetDynamicModule(harness.Server, plugin.ServiceAssembly);
            Ensure(oldModule.RemainingCalls == 0,
                "an admission waiter must not hold an old module invocation lease");
            if (replaceWhileQueued)
            {
                var replacement = await harness.Server.ReplaceAssemblyAsync(
                    plugin.ServiceAssembly, replacementAssembly, TimeSpan.Zero);
                Ensure(replacement.Succeeded && replacement.ReferencesReleased,
                    "same-contract implementation replacement must publish while the waiter remains queued");
            }
            Ensure(!oldModule.ForcedCancellation.IsCancellationRequested,
                "a normally drained old module must not confuse the new-module cancellation oracle");

            if (replaceWhileQueued)
            {
                holder.Lease!.Dispose();
                try
                {
                    _ = await queued.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new Exception("assert failed: a queued call must not retarget a replacement");
                }
                catch (SharpLinkException exception)
                {
                    Ensure(exception.Code == SharpLinkErrorCode.Unavailable,
                        "a retired admission selection must report Unavailable");
                }
                Ensure(!((Task)replacementType.GetProperty("BlockStarted")!.GetValue(null)!).IsCompleted,
                    "the replacement must never execute an old queued call");
                Ensure(!plugin.GetStaticTask("BlockStarted").IsCompleted,
                    "a retired original registration must not execute");
                return;
            }

            var activeAssembly = plugin.ServiceAssembly;
            var activeType = activeAssembly.GetType(
                "SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            holder.Lease!.Dispose();
            await ((Task)activeType.GetProperty("BlockStarted")!.GetValue(null)!)
                .WaitAsync(TimeSpan.FromSeconds(5));
            var handlerToken = (CancellationToken)activeType.GetProperty("BlockCancellation")!
                .GetValue(null)!;
            Ensure(handlerToken.CanBeCanceled && !handlerToken.IsCancellationRequested,
                "the unchanged selected handler starts with a live cooperative cancellation token");
            var replacementModule = GetDynamicModule(harness.Server, activeAssembly);

            _ = await harness.Server.UnregisterAssemblyAsync(activeAssembly, TimeSpan.Zero);
            Ensure(replacementModule.ForcedCancellation.IsCancellationRequested,
                "selected module retirement must have synchronously requested forced cancellation");
            Ensure(handlerToken.IsCancellationRequested,
                "an unchanged queued call must observe its selected module's forced cancellation");
        }
        finally
        {
            holder.Lease!.Dispose();
            replacementType?.GetMethod("ReleaseBlock")!.Invoke(null, null);
            plugin.ReleaseBlock();
            if (queued is not null)
            {
                try { _ = await queued.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (SharpLinkException) { }
            }
            if (replacementAssembly is not null)
                _ = await harness.Server.UnregisterAssemblyAsync(replacementAssembly, TimeSpan.FromSeconds(2));
            _ = await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2));
            _ = await harness.Server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            _ = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            replacementContext.Unload();
        }
    }

    private sealed class SharedContractServiceLoadContext(Assembly contractAssembly)
        : AssemblyLoadContext("queued-replacement-shared-contract", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (AssemblyName.ReferenceMatchesDefinition(contractAssembly.GetName(), assemblyName))
                return contractAssembly;
            return Default.Assemblies.FirstOrDefault(candidate =>
                AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
        }
    }
}
