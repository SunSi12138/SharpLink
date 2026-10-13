using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task ReceiptShouldEstablishAllRpcShapesBeforeDelayedDataManifest()
    {
        using var previous = PluginBundle.Load("receipt-previous");
        using var replacement = PluginBundle.Load("receipt-replacement", replacement: true);
        (long ContractId, RpcHash128 Hash) replacementIdentity = default;
        DynamicHarness? current = null;
        var controlService = new ManifestReadinessControl
        {
            ReceiptOperation = async _ =>
            {
                Ensure((await current!.Server.UnregisterAssemblyAsync(
                    previous.ServiceAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
                    "the control call retires the old service before its contract");
                Ensure((await current.Server.ReplaceAssemblyAsync(previous.ContractAssembly,
                    replacement.ContractAssembly, TimeSpan.FromSeconds(2))).Succeeded,
                    "the control call publishes a genuinely different generated hash");
                var publication = current.Server.RegisterAssembly(replacement.ServiceAssembly);
                Ensure(publication.Succeeded, $"replacement service publication: {publication.Error}");
                return ContractReceiptData.FromReceipt(publication.Receipts.Single(
                    receipt => receipt.ContractId == replacementIdentity.ContractId));
            }
        };
        await using var harness = await DynamicHarness.CreateAsync(control: controlService);
        current = harness;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Freeze all three runtimes before materializing collectible generated manifests.
        // Manifest construction can bootstrap the process catalog for later builders.
        await using var control = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), harness.Port).Build();
        await control.ConnectAsync(timeout.Token);
        var controlProxy = control.CreateProxy<IManifestReadinessControl>();
        var previousIdentity = ReadPublicContractIdentity(previous.ContractType);
        replacementIdentity = ReadPublicContractIdentity(replacement.ContractType);
        Ensure(harness.Client.RegisterAssembly(previous.ContractAssembly).Succeeded, "initial local registration");
        Ensure(harness.Server.RegisterAssembly(previous.ContractAssembly).Succeeded, "initial server contract");
        Ensure(harness.Server.RegisterAssembly(previous.ServiceAssembly).Succeeded, "initial server service");
        await harness.Client.WaitForRemoteContractAsync(
            previousIdentity.ContractId, previousIdentity.Hash, timeout.Token);
        var oldProxy = GetProxy(harness.Client, previous.ContractType);
        var broadcastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((SharpLinkServer)harness.Server).ContractManifestPublishBarrierForTesting = () =>
        {
            broadcastStarted.TrySetResult();
            return releaseBroadcast.Task;
        };
        try
        {
            var receipt = (await controlProxy.ChangeWithReceiptAsync(1, timeout.Token)).ToReceipt();
            await broadcastStarted.Task.WaitAsync(timeout.Token);
            Ensure(receipt.RpcAssemblyHash == replacementIdentity.Hash &&
                   receipt.RpcAssemblyHash != previousIdentity.Hash,
                "the control response carries the actual replacement identity over TCP");

            // The data route still qualifies H1. Only server admission can reject these requests.
            await InvokeValueTaskAsync(oldProxy, previous.ContractType, "NotifyAsync", 100, timeout.Token);
            await ExpectContractFailureAsync(
                () => InvokeValueTaskAsync<int>(oldProxy, previous.ContractType, "UnaryAsync", 6, timeout.Token),
                SharpLinkErrorCode.FailedPrecondition, SharpLinkErrorDetails.FailedPrecondition.ContractMismatch);
            Ensure(replacement.GetStaticInt("Created") == 0 && replacement.GetStaticInt("Notifications") == 0,
                "stale unary/oneway identity cannot activate or execute the replacement service");

            var producerStarted = false;
            async IAsyncEnumerable<int> RejectedProducer()
            {
                producerStarted = true;
                yield return 99;
                await Task.CompletedTask;
            }
            await ExpectContractFailureAsync(
                () => InvokeValueTaskAsync<int>(oldProxy, previous.ContractType, "ClientStreamAsync",
                    RejectedProducer(), timeout.Token),
                SharpLinkErrorCode.FailedPrecondition, SharpLinkErrorDetails.FailedPrecondition.ContractMismatch);
            Ensure(!producerStarted, "remote pre-business rejection must not start a client stream producer");

            Ensure((await harness.Client.ReplaceAssemblyAsync(previous.ContractAssembly,
                replacement.ContractAssembly, TimeSpan.FromSeconds(2))).Succeeded,
                "local proxy generation changes independently of manifest visibility");
            var ordinary = CreateReceiptProxy(harness.Client, replacement.ContractType, null);
            await ExpectContractFailureAsync(
                () => InvokeValueTaskAsync<int>(ordinary, replacement.ContractType, "UnaryAsync", 6, timeout.Token),
                SharpLinkErrorCode.Unavailable, SharpLinkErrorDetails.Unavailable.NoRoutableTarget);

            var proxy = CreateReceiptProxy(harness.Client, replacement.ContractType, new RpcProxyOptions
            {
                Receipt = receipt,
                Requirement = RpcRegistrationRequirement.ExactRegistration
            });
            Ensure(await InvokeValueTaskAsync<int>(proxy, replacement.ContractType,
                "UnaryAsync", 6, timeout.Token) == 7, "receipt unary completes without a manifest broadcast");
            Ensure(await InvokeValueTaskAsync<int>(proxy, replacement.ContractType,
                "ClientStreamAsync", ReceiptValues(), timeout.Token) == 6, "receipt client streaming");
            Ensure((await CollectAsync(InvokeStream(proxy, replacement.ContractType,
                "ServerStreamAsync", 3, timeout.Token))).SequenceEqual([0, 1, 2]), "receipt server streaming");
            Ensure((await CollectAsync(InvokeStream(proxy, replacement.ContractType,
                "DuplexAsync", ReceiptValues(), timeout.Token))).SequenceEqual([2, 4, 6]), "receipt duplex streaming");
            await InvokeValueTaskAsync(proxy, replacement.ContractType, "NotifyAsync", 5, timeout.Token);
            await replacement.GetStaticTask("NotificationReceived").WaitAsync(timeout.Token);
            Ensure(replacement.GetStaticInt("Notifications") == 5, "only the compatible one-way method ran");
            Ensure(!releaseBroadcast.Task.IsCompleted, "none of these calls needed the delayed broadcast");

            foreach (var foreign in new[]
            {
                receipt with { ServerInstanceId = Guid.NewGuid() },
                receipt with { RegistryEpoch = Guid.NewGuid() }
            })
            {
                var wrongTarget = CreateReceiptProxy(harness.Client, replacement.ContractType,
                    new RpcProxyOptions { Receipt = foreign });
                await ExpectContractFailureAsync(
                    () => InvokeValueTaskAsync<int>(wrongTarget, replacement.ContractType, "UnaryAsync", 6, timeout.Token),
                    SharpLinkErrorCode.Unavailable, SharpLinkErrorDetails.Unavailable.NoRoutableTarget);
            }
        }
        finally
        {
            releaseBroadcast.TrySetResult();
            ((SharpLinkServer)harness.Server).ContractManifestPublishBarrierForTesting = null;
        }
    }

    [Test]
    [NotInParallel]
    public async Task SameHashReplacementMustDistinguishMinimumVersionAndExactRegistration()
    {
        using var plugin = PluginBundle.Load("receipt-same-hash");
        await using var harness = await DynamicHarness.CreateAsync();
        var identity = ReadPublicContractIdentity(plugin.ContractType);
        Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded, "local contract registration");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded, "server contract registration");
        var first = harness.Server.RegisterAssembly(plugin.ServiceAssembly).Receipts.Single(
            receipt => receipt.ContractId == identity.ContractId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await harness.Client.WaitForRemoteContractAsync(identity.ContractId, identity.Hash, timeout.Token);
        var exact = CreateReceiptProxy(harness.Client, plugin.ContractType,
            new RpcProxyOptions { Receipt = first, Requirement = RpcRegistrationRequirement.ExactRegistration });
        Ensure((await harness.Server.UnregisterAssemblyAsync(plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "first service registration releases");
        var second = harness.Server.RegisterAssembly(plugin.ServiceAssembly).Receipts.Single(
            receipt => receipt.ContractId == identity.ContractId);
        Ensure(first.ServerInstanceId == second.ServerInstanceId && first.RegistryEpoch == second.RegistryEpoch &&
               first.RpcAssemblyHash == second.RpcAssemblyHash && first.Generation < second.Generation &&
               first.RegistrationId != second.RegistrationId, "same hash is not the same registration");
        await ExpectContractFailureAsync(
            () => InvokeValueTaskAsync<int>(exact, plugin.ContractType, "UnaryAsync", 6, timeout.Token),
            SharpLinkErrorCode.FailedPrecondition, SharpLinkErrorDetails.FailedPrecondition.ReceiptMismatch);
        Ensure(plugin.GetStaticInt("Created") == 0, "exact mismatch precedes instance activation");
        var minimum = CreateReceiptProxy(harness.Client, plugin.ContractType,
            new RpcProxyOptions { Receipt = first, Requirement = RpcRegistrationRequirement.MinimumVersion });
        Ensure(await InvokeValueTaskAsync<int>(minimum, plugin.ContractType,
            "UnaryAsync", 6, timeout.Token) == 7, "minimum version accepts the newer compatible registration");
    }

    private static async IAsyncEnumerable<int> ReceiptValues()
    {
        yield return 1;
        yield return 2;
        yield return 3;
        await Task.CompletedTask;
    }

    private static (long ContractId, RpcHash128 Hash) ReadPublicContractIdentity(Type contractType)
    {
        var locator = contractType.Assembly.GetCustomAttribute<SharpLinkGeneratedAssemblyManifestAttribute>()!;
        var manifest = (ISharpLinkGeneratedAssemblyManifest)Activator.CreateInstance(locator.ManifestType)!;
        return (manifest.Contracts.Single(contract => contract.ContractType == contractType).ContractId,
            manifest.RpcAssemblyHash);
    }

    private static object CreateReceiptProxy(ISharpLinkClient client, Type contractType, RpcProxyOptions? options)
        => typeof(ISharpLinkClient).GetMethod(nameof(ISharpLinkClient.CreateProxy))!
            .MakeGenericMethod(contractType).Invoke(client, [options])!;

    private static async Task ExpectContractFailureAsync(
        Func<ValueTask<int>> operation, SharpLinkErrorCode code, ushort detail)
    {
        try
        {
            _ = await operation();
        }
        catch (Exception exception)
        {
            var failure = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            Ensure(failure is SharpLinkException rpc && rpc.Code == code && rpc.DetailCode == detail,
                $"expected structured {code}/{detail}, received {failure}");
            return;
        }
        throw new InvalidOperationException($"Expected structured {code}/{detail} rejection.");
    }
}
