using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

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
    public Task DynamicInputDrainShouldHoldServiceUntilPhysicalDecodeExits(string lifetime, string method)
        => VerifyDynamicInputDrainAsync(lifetime, method, "success");

    [Test]
    [NotInParallel]
    [Arguments("Singleton", "throw")]
    [Arguments("Call", "throw")]
    [Arguments("Singleton", "cancel")]
    [Arguments("Call", "cancel")]
    [Arguments("Singleton", "replace")]
    [Arguments("Call", "replace")]
    public Task DynamicOneWayInputTerminalShouldRetainPhysicalOwner(string lifetime, string terminal)
        => VerifyDynamicInputDrainAsync(lifetime, "NotifyStreamAsync", terminal);

    private static async Task VerifyDynamicInputDrainAsync(string lifetime, string method, string terminal)
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var weak = await ExerciseDynamicInputDrainAsync(harness, lifetime, method, terminal)
            .ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        for (var attempt = 0; attempt < 10 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }
        Ensure(!weak.IsAlive, "retired input plugin collects while client and server remain active");
        var output = Environment.GetEnvironmentVariable("SHARPLINK_DRAIN_RESULTS");
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, $"{lifetime}-{method}-{terminal}.json"),
                JsonSerializer.Serialize(new { lifetime, method, terminal, alcCollected = true,
                    source = Environment.GetEnvironmentVariable("GITHUB_SHA") }));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ExerciseDynamicInputDrainAsync(
        DynamicHarness harness, string lifetime, string method, string terminal)
    {
        using var plugin = PluginBundle.Load($"dynamic-input-drain-{lifetime}-{method}");
        const string serviceProbe = "SharpLink.DynamicPlugin.OneWayDrainService";
        var codec = plugin.GetContractType("SharpLink.DynamicPlugin.DrainItemCodec");
        codec.GetMethod("Reset")!.Invoke(null, null);
        plugin.InvokeServiceStatic(serviceProbe, "Reset");
        plugin.ServiceAssembly.GetType(serviceProbe)!.GetProperty("Outcome")!.SetValue(null, terminal);
        await RegisterAllAsync(harness, plugin);
        var contract = plugin.GetContractType($"SharpLink.DynamicPlugin.IOneWayDrain{lifetime}");
        await WaitForRemoteContractManifestAsync(harness.Client, contract);
        object? proxy = GetProxy(harness.Client, contract);
        var module = GetDynamicModule(harness.Server, plugin.ServiceAssembly);
        Task call;
        DrainReplacementContext? replacementContext = null;
        Assembly? replacement = null;
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
                try { await plugin.GetServiceStaticTask(serviceProbe, "HandlerTask").WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (InvalidOperationException exception) when (
                    terminal == "throw" && exception.Message == "DYNAMIC_INPUT_HANDLER_FAILURE") { }
                catch (OperationCanceledException) when (terminal == "cancel") { }
            }
            var activeStreams = module.RemainingStreams;
            bool released;
            int calls;
            if (terminal == "replace")
            {
                replacementContext = new DrainReplacementContext(plugin.ContractAssembly);
                replacement = replacementContext.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
                var result = await harness.Server.ReplaceAssemblyAsync(
                    plugin.ServiceAssembly, replacement, TimeSpan.FromSeconds(2));
                Ensure(result.Succeeded, $"compatible service replacement: {result.Error}");
                released = result.ReferencesReleased;
                calls = result.RemainingCalls;
            }
            else
            {
                var result = await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2));
                released = result.ReferencesReleased;
                calls = result.RemainingCalls;
            }
            var disposed = plugin.GetServiceStaticInt(serviceProbe, "Disposed");
            var decodes = (int)codec.GetProperty("ActiveDecodes")!.GetValue(null)!;
            Console.WriteLine($"DYNAMIC_INPUT_DRAIN lifetime={lifetime} method={method} terminal={terminal} streams={activeStreams} released={released} disposed={disposed} activeDecodes={decodes} calls={calls}");
            if (method == "NotifyStreamAsync" && released && disposed > 0 && decodes == 1)
                throw new InvalidOperationException("DYNAMIC_ONEWAY_PHYSICAL_EXIT_V1: generated dynamic service retired while its input Codec was still executing.");
            Ensure(!released, "physical work must keep the old service module owned");
            Ensure(disposed == 0, "service disposal must follow physical input exit");
            Ensure(calls == 1, "one invocation owns the service module");
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
        // A timed-out retirement keeps its original result; its owned background cleanup
        // subsequently publishes Released rather than rewriting ReferencesReleased.
        await WaitUntilAsync(() => module.State == SharpLinkDynamicModuleState.Released);
        Ensure(plugin.GetServiceStaticInt(serviceProbe, "Disposed") == 1, "service disposes exactly once");
        Ensure((int)codec.GetProperty("ActiveDecodes")!.GetValue(null)! == 0, "decoder exited physically");
        Ensure(module.RemainingCalls == 0 && module.RemainingStreams == 0, "module counters balance");
        if (replacement is not null)
        {
            Ensure((await harness.Server.UnregisterAssemblyAsync(replacement, TimeSpan.FromSeconds(2))).ReferencesReleased,
                "replacement service cleanup");
            replacement = null;
            replacementContext!.Unload();
            replacementContext = null;
        }
        Ensure((await harness.Server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "server contract cleanup");
        Ensure((await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "client contract cleanup");
        proxy = null;
        contract = codec = null!;
        call = null!;
        return plugin.Unload();
    }

    private sealed class DrainReplacementContext(Assembly contract) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
            => AssemblyName.ReferenceMatchesDefinition(name, contract.GetName())
                ? contract
                : Default.Assemblies.FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(name, a.GetName()));
    }
}

