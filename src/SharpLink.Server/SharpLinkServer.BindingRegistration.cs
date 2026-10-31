namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private readonly Protocol3IdSequence _bindingRegistrationIds = new();

    private void InitializeBindingRegistrationsLocked()
    {
        if (_services.Count != 0 && _registryGeneration == 0)
            _registryGeneration = 1;
        // Construction is still private here. All facts callbacks were completed by the builder;
        // this bounded metadata plan is created before any server registry lock is exposed.
        PublishBindingRegistrations(CreateBindingRegistrationPlans(_services, [], null), _registryGeneration);
    }

    private readonly record struct PreparedBindingMember(ServiceRegistration Registration, long ContractId, RpcHash128 Hash);
    private readonly record struct PreparedBindingGroup(PreparedBindingMember[] Members);

    // Called only by the cold preparation owner, outside registry locks. Manifest getters are
    // never invoked from final publication. Each member carries its CONTRACT manifest hash,
    // even when its service implementation belongs to a different assembly registration.
    private PreparedBindingGroup[] CreateBindingRegistrationPlans(
        IReadOnlyDictionary<long, ServiceRegistration> services,
        SharpLinkDynamicModule[] currentModules,
        ISharpLinkGeneratedAssemblyManifest? incoming)
    {
        var groups = new Dictionary<System.Reflection.Assembly, List<PreparedBindingMember>>(ReferenceEqualityComparer.Instance);
        foreach (var pair in services)
        {
            var registration = pair.Value;
            if (registration.Binding is not null)
                continue;
            if (registration.DeclaredFacts is null)
                throw new InvalidOperationException("A service must have complete cold declared facts before binding publication.");
            var contractAssembly = registration.ContractType.Assembly;
            var manifest = incoming is not null && ReferenceEquals(incoming.OwnerAssembly, contractAssembly)
                ? incoming
                : FindManifest(contractAssembly, currentModules)
                    ?? throw new InvalidOperationException("The exact contract manifest is unavailable.");
            var owner = registration.Module?.Assembly ?? contractAssembly;
            if (!groups.TryGetValue(owner, out var members))
                groups.Add(owner, members = []);
            members.Add(new(registration, pair.Key, manifest.RpcAssemblyHash));
        }
        return groups.Values.Select(static members => new PreparedBindingGroup(members.ToArray())).ToArray();
    }

    // Scalar metadata/allocation only. All fallible preparation precedes the old admission cut.
    private void PublishBindingRegistrations(PreparedBindingGroup[] plans, long generation)
    {
        if (plans.Length == 0)
            return;
        if (generation <= 0)
            throw new InvalidOperationException("A nonempty registration publication requires a positive generation.");
        foreach (var plan in plans)
        {
            if (!_bindingRegistrationIds.TryIssue(out var registrationId))
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                    "The server registration identity domain is exhausted; a new server epoch is required.");
            var group = new ServerBindingAdmissionGroup();
            var members = new ServerBindingRegistration[plan.Members.Length];
            for (var index = 0; index < members.Length; index++)
            {
                var member = plan.Members[index];
                members[index] = new(member.Registration, member.ContractId, member.Hash, registrationId,
                    generation, member.Registration.DeclaredFacts!, group);
            }
            group.Seal(members);
            for (var index = 0; index < members.Length; index++)
                plan.Members[index].Registration.Binding = members[index];
        }
    }

    private static void RetireBindingGroup(ServiceRegistration[] registrations)
    {
        foreach (var registration in registrations)
            registration.Binding?.Retire();
    }

    private long NextBindingPublicationGeneration()
    {
        if (_registryGeneration == long.MaxValue)
            throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted,
                "The registry publication domain is exhausted; a new server epoch is required.");
        return _registryGeneration + 1;
    }
}
