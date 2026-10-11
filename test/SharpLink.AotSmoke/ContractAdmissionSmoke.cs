using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.AotSmoke;

internal static class ContractAdmissionSmoke
{
    public static async Task VerifyAsync(ISharpLinkClient client, CancellationToken cancellationToken)
    {
        // Resolve only statically generated, rooted contract metadata; no runtime discovery fallback.
        var manifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot()
            .Single(static candidate => candidate.Contracts.Any(static contract => contract.ContractType == typeof(IAotService)));
        var contract = manifest.Contracts.Single(static candidate => candidate.ContractType == typeof(IAotService));
        var compatible = new ContractRequirement(contract.ContractId, manifest.RpcAssemblyHash);
        var snapshot = await client.WaitForContractAsync(
            compatible,
            new ContractWaitOptions { Timeout = TimeSpan.FromSeconds(5) },
            cancellationToken).ConfigureAwait(false);
        if (snapshot.State != ContractObservationState.ObservedCompatible || snapshot.CompatibleEndpoints != 1 ||
            client.GetContractReadiness(compatible).State != ContractObservationState.ObservedCompatible)
        {
            throw new Exception("unexpected AOT contract readiness");
        }

        var waitingProxy = client.CreateProxy<IAotService>(new RpcProxyOptions
        {
            Availability = RpcAvailability.WaitForCompatible,
            Requirement = compatible
        });
        if (await waitingProxy.PingAsync().ConfigureAwait(false) != "pong")
            throw new Exception("unexpected AOT WaitForCompatible result");

        var observed = snapshot.Endpoints.First(static endpoint => endpoint.State == ContractObservationState.ObservedCompatible);
        if (observed.ServerInstanceId == Guid.Empty || observed.RegistryEpoch == Guid.Empty ||
            observed.RegistrationId == Guid.Empty || observed.ManifestGeneration is not >= 0 ||
            observed.RpcAssemblyHash != manifest.RpcAssemblyHash)
        {
            throw new Exception("missing AOT scalar registration identity");
        }
        var receipt = new ContractRegistrationReceipt(
            observed.ServerInstanceId, observed.RegistryEpoch,
            observed.ManifestGeneration ?? throw new Exception("missing AOT manifest generation"),
            contract.ContractId, manifest.RpcAssemblyHash, observed.RegistrationId);
        foreach (var match in new[] { ContractRegistrationMatch.MinimumVersion, ContractRegistrationMatch.ExactRegistration })
        {
            var requirement = new ContractRequirement(receipt, match);
            var constrained = client.CreateProxy<IAotService>(new RpcProxyOptions { Requirement = requirement });
            if (client.GetContractReadiness(requirement).State != ContractObservationState.ObservedCompatible ||
                await constrained.PingAsync().ConfigureAwait(false) != "pong")
            {
                throw new Exception($"unexpected AOT receipt admission: {match}");
            }
        }

        // A same-contract, same-hash receipt from a different registration must fail closed.
        var missingRegistration = new ContractRequirement(
            receipt with { RegistrationId = Guid.NewGuid() }, ContractRegistrationMatch.ExactRegistration);
        var rejected = client.CreateProxy<IAotService>(new RpcProxyOptions { Requirement = missingRegistration });
        if (client.GetContractReadiness(missingRegistration).State != ContractObservationState.ObservedIncompatible)
            throw new Exception("AOT stale registration was observed as compatible");
        try
        {
            await rejected.PingAsync().ConfigureAwait(false);
            throw new Exception("AOT stale registration invocation unexpectedly succeeded");
        }
        catch (SharpLinkContractAdmissionException exception) when (exception.Code == SharpLinkErrorCode.Unavailable)
        {
            // This client-local exception proves rejection before a remote business invocation.
            if (exception.Snapshot.State != ContractObservationState.ObservedIncompatible)
                throw new Exception("unexpected AOT rejected-admission observation");
        }

        if (await waitingProxy.PingAsync().ConfigureAwait(false) != "pong")
            throw new Exception("AOT rejected admission damaged the valid proxy");
        Console.WriteLine("CONTRACT_ADMISSION_PASS");
    }
}
