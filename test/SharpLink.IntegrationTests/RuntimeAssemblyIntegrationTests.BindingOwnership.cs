namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SeparatedDynamicServiceBindingMustUseContractHashAndServiceReceiptOwner(bool exactReceipt)
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load($"bound-separated-owners-{exactReceipt}");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin);
        var clientModule = GetDynamicModule(harness.Client, plugin.ContractAssembly);
        var serverModule = GetDynamicModule(harness.Server, plugin.ServiceAssembly);
        var contract = clientModule.Manifest.Contracts.Single(item => item.ContractType == plugin.ContractType);
        Ensure(clientModule.Manifest.RpcAssemblyHash != serverModule.Manifest.RpcAssemblyHash,
            "the separated-owner counterexample must have genuinely different assembly hashes");

        var receipt = harness.Server.GetRegistrationReceipt(plugin.ServiceAssembly, contract.ContractId);
        Ensure(receipt.RpcAssemblyHash == clientModule.Manifest.RpcAssemblyHash &&
               receipt.Generation == serverModule.BindingPublicationGeneration,
            "receipt pairs the Contract hash with the actual Service registration generation");
        try
        {
            harness.Server.GetRegistrationReceipt(plugin.ContractAssembly, contract.ContractId);
            throw new Exception("a Contract-only assembly must not claim ownership of the Service registration");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.FailedPrecondition,
                "wrong receipt owner is a definite precondition failure");
        }

        var options = exactReceipt ? new RpcProxyOptions
        {
            Receipt = receipt,
            Requirement = RpcRegistrationRequirement.ExactRegistration
        } : null;
        var handle = CreateLocalProxyHandle(harness.Client, typeof(ISharpLinkClient), plugin.ContractType, options);
        await PrepareHandleAsync(handle);
        Ensure(plugin.GetStaticInt("Created") == 0, "Bind must not activate the separated Service");
        Ensure(await InvokeValueTaskAsync<int>(GetHandleService(handle), plugin.ContractType,
                "UnaryAsync", 41, CancellationToken.None) == 42,
            "the exact prepared receiver must execute its separately registered Service");
    }
}
