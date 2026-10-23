namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task ExplicitReplacementShouldWaitForNewGeneratedHashAcrossIndependentConnections()
    {
        using var previous = PluginBundle.Load("remote-contract-previous");
        using var replacement = PluginBundle.Load("remote-contract-replacement", replacement: true);
        DynamicHarness? current = null;
        var controlService = new ManifestReadinessControl
        {
            Operation = async _ =>
            {
                Ensure((await current!.Server.UnregisterAssemblyAsync(
                    previous.ServiceAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
                    "control removes the previous service dependant");
                Ensure((await current.Server.ReplaceAssemblyAsync(previous.ContractAssembly,
                    replacement.ContractAssembly, TimeSpan.FromSeconds(2))).Succeeded,
                    "control replaces the actual generated contract assembly");
                Ensure(current.Server.RegisterAssembly(replacement.ServiceAssembly).Succeeded,
                    "control registers the replacement service");
                return 2;
            }
        };
        await using var harness = await DynamicHarness.CreateAsync(control: controlService);
        current = harness;
        await RegisterAllAsync(harness, previous);
        var previousManifest = GetDynamicModule(harness.Client, previous.ContractAssembly).Manifest;
        var previousHash = previousManifest.RpcAssemblyHash;
        var previousId = previousManifest.Contracts.Single(c => c.ContractType == previous.ContractType).ContractId;
        await using var control = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), harness.Port).Build();
        await control.ConnectAsync();
        var controlProxy = control.Get<IManifestReadinessControl>();
        var broadcastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((SharpLinkServer)harness.Server).ContractManifestPublishBarrierForTesting = () =>
        {
            broadcastStarted.TrySetResult();
            return releaseBroadcast.Task;
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            Ensure(await controlProxy.ChangeAsync(2, timeout.Token) == 2,
                "replacement control reply completes before the data manifest broadcast");
            await broadcastStarted.Task.WaitAsync(timeout.Token);
            Ensure((await harness.Client.ReplaceAssemblyAsync(previous.ContractAssembly,
                replacement.ContractAssembly, TimeSpan.FromSeconds(2))).Succeeded,
                "local binding replacement succeeds independently of remote visibility");
            var module = GetDynamicModule(harness.Client, replacement.ContractAssembly);
            var contractId = module.Manifest.Contracts.Single(c => c.ContractType == replacement.ContractType).ContractId;
            Ensure(module.Manifest.RpcAssemblyHash != previousHash && contractId == previousId,
                "real generated replacement preserves the contract ID while changing its owning wire hash");
            var wait = harness.Client.WaitForRemoteContractAsync(
                contractId, module.Manifest.RpcAssemblyHash, previousHash, timeout.Token).AsTask();
            Ensure(!wait.IsCompleted, "only the explicitly declared previous hash may wait for replacement propagation");
            releaseBroadcast.TrySetResult();
            await wait;
            var proxy = GetProxy(harness.Client, replacement.ContractType);
            Ensure(await InvokeValueTaskAsync<int>(proxy, replacement.ContractType,
                "UnaryAsync", 6, timeout.Token) == 7,
                "the new generated proxy calls the replaced service over the original data connection");
        }
        finally
        {
            releaseBroadcast.TrySetResult();
            ((SharpLinkServer)harness.Server).ContractManifestPublishBarrierForTesting = null;
        }
    }

    [Test]
    [NotInParallel]
    public async Task IndependentControlReplyMustNotAcknowledgeDelayedDataManifest()
    {
        using var plugin = PluginBundle.Load("remote-contract-barrier");
        DynamicHarness? current = null;
        var controlService = new ManifestReadinessControl
        {
            Operation = _ =>
            {
                Ensure(current!.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded,
                    "control RPC registers the server contract");
                Ensure(current.Server.RegisterAssembly(plugin.ServiceAssembly).Succeeded,
                    "control RPC registers the server service");
                return ValueTask.FromResult(1);
            }
        };
        await using var harness = await DynamicHarness.CreateAsync(control: controlService);
        current = harness;
        await using var control = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), harness.Port).Build();
        await control.ConnectAsync();
        var controlProxy = control.Get<IManifestReadinessControl>();
        var broadcastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((SharpLinkServer)harness.Server).ContractManifestPublishBarrierForTesting = () =>
        {
            broadcastStarted.TrySetResult();
            return releaseBroadcast.Task;
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            Ensure(await controlProxy.ChangeAsync(1, timeout.Token) == 1,
                "independent control RPC completes while data broadcast remains held");
            await broadcastStarted.Task.WaitAsync(timeout.Token);
            Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded,
                "local data binding registers after the control reply");
            var module = GetDynamicModule(harness.Client, plugin.ContractAssembly);
            var contractId = module.Manifest.Contracts.Single(c => c.ContractType == plugin.ContractType).ContractId;
            var wait = harness.Client.WaitForRemoteContractAsync(
                contractId, module.Manifest.RpcAssemblyHash, timeout.Token).AsTask();
            Ensure(!wait.IsCompleted, "ordinary Ready and registration success do not complete the contract barrier");
            try
            {
                _ = GetProxy(harness.Client, plugin.ContractType);
                throw new Exception("Get must fail while the remote manifest is still missing");
            }
            catch (System.Reflection.TargetInvocationException exception)
                when (exception.InnerException is SharpLinkException { Code: SharpLinkErrorCode.FailedPrecondition })
            {
            }
            releaseBroadcast.TrySetResult();
            await wait;
            var proxy = GetProxy(harness.Client, plugin.ContractType);
            Ensure(await InvokeValueTaskAsync<int>(proxy, plugin.ContractType,
                    "UnaryAsync", 4, timeout.Token) == 5,
                "the public barrier permits a real TCP call without polling or reconnect");
        }
        finally
        {
            releaseBroadcast.TrySetResult();
            ((SharpLinkServer)harness.Server).ContractManifestPublishBarrierForTesting = null;
        }
    }
}

[RpcContract]
public interface IManifestReadinessControl : IService
{
    ValueTask<int> ChangeAsync(int phase, CancellationToken cancellationToken);
}

[RpcService]
public sealed class ManifestReadinessControl : IManifestReadinessControl
{
    internal Func<int, ValueTask<int>>? Operation { get; init; }

    public ValueTask<int> ChangeAsync(int phase, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Operation is { } operation ? operation(phase) : ValueTask.FromResult(0);
    }
}
