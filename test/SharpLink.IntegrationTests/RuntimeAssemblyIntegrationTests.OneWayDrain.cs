using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("Singleton", "NotifyStreamAsync")]
    [Arguments("Call", "NotifyStreamAsync")]
    [Arguments("Singleton", "ReceiveStreamAsync")]
    [Arguments("Call", "ReceiveStreamAsync")]
    [Arguments("Singleton", "NotifyAsync")]
    [Arguments("Call", "NotifyAsync")]
    public async Task DynamicInputDrainShouldHoldServiceUntilPhysicalDecodeExits(string lifetime, string method)
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load($"dynamic-input-drain-{lifetime}-{method}");
        const string serviceProbe = "SharpLink.DynamicPlugin.OneWayDrainService";
        var codec = plugin.GetContractType("SharpLink.DynamicPlugin.DrainItemCodec");
        codec.GetMethod("Reset")!.Invoke(null, null);
        plugin.InvokeServiceStatic(serviceProbe, "Reset");
        await RegisterAllAsync(harness, plugin);
        var contract = plugin.GetContractType($"SharpLink.DynamicPlugin.IOneWayDrain{lifetime}");
        await WaitForRemoteContractManifestAsync(harness.Client, contract);
        object? proxy = GetProxy(harness.Client, contract);
        var module = GetDynamicModule(harness.Server, plugin.ServiceAssembly);
        Task call;
        if (method == "NotifyAsync")
            call = InvokeValueTaskAsync(proxy, contract, method, CancellationToken.None).AsTask();
        else
            call = InvokeValueTaskAsync(proxy, contract, method, codec.GetMethod("Items")!.Invoke(null, null)!, CancellationToken.None).AsTask();
        try
        {
            await plugin.GetServiceStaticTask(serviceProbe, "HandlerEntered").WaitAsync(TimeSpan.FromSeconds(5));
            if (method != "NotifyAsync")
            {
                await ((Task)codec.GetProperty("DecodeEntered")!.GetValue(null)!).WaitAsync(TimeSpan.FromSeconds(5));
                await plugin.GetServiceStaticTask(serviceProbe, "HandlerTask").WaitAsync(TimeSpan.FromSeconds(5));
            }
            var activeStreams = module.RemainingStreams;
            var result = await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2));
            var disposed = plugin.GetServiceStaticInt(serviceProbe, "Disposed");
            var decodes = (int)codec.GetProperty("ActiveDecodes")!.GetValue(null)!;
            Console.WriteLine($"DYNAMIC_INPUT_DRAIN lifetime={lifetime} method={method} streams={activeStreams} released={result.ReferencesReleased} disposed={disposed} activeDecodes={decodes} calls={result.RemainingCalls} remainingStreams={result.RemainingStreams}");
            if (method == "NotifyStreamAsync" && result.ReferencesReleased && disposed > 0 && decodes == 1)
                throw new InvalidOperationException("DYNAMIC_ONEWAY_PHYSICAL_EXIT_V1: generated dynamic service retired while its input Codec was still executing.");
            Ensure(!result.ReferencesReleased, "physical work must keep the old service module owned");
            Ensure(disposed == 0, "service disposal must follow physical input exit");
            Ensure(result.RemainingCalls == 1, "one invocation owns the service module");
            Ensure(activeStreams == (method == "NotifyAsync" ? 0 : 1),
                "only calls with stream ownership occupy the module stream census");
        }
        finally
        {
            codec.GetMethod("ReleaseDecode")!.Invoke(null, null);
            plugin.InvokeServiceStatic(serviceProbe, "ReleaseHandler");
        }
        try
        {
            await call.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (SharpLinkException exception) when (
            method == "ReceiveStreamAsync" && exception.Code == SharpLinkErrorCode.Unavailable)
        {
            // The two-second module grace elapsed while the physical decoder was held.
        }
        Ensure((await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "service releases after physical input exit");
        Ensure(plugin.GetServiceStaticInt(serviceProbe, "Disposed") == 1, "service disposes exactly once");
        Ensure((int)codec.GetProperty("ActiveDecodes")!.GetValue(null)! == 0, "decoder exited physically");
        Ensure(module.RemainingCalls == 0 && module.RemainingStreams == 0, "module counters balance");
        Ensure((await harness.Server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "server contract cleanup");
        Ensure((await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "client contract cleanup");
        proxy = null;
    }
}

