using System.Collections.Frozen;
using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

// Explicit component declarations: cold preparation may query ResolveMethodFacts exactly once;
// request dispatch receives only the resulting owned table and an actually allocated Bind token.
internal interface IDeclaredServerFixtureStub : IRpcStub
{
    SharpLinkGeneratedMethodDescriptor DeclaredMethod { get; }
}

internal sealed class ServerDispatchContractFixture : IAsyncDisposable
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly SharpLinkServer _server;
    private readonly SharpLinkRuntimeContext _runtime;
    private readonly ServerServiceModuleRegistry _registry;
    private readonly ServerServiceSnapshot.Lease _original;
    private readonly List<ServiceRegistration> _created = [];
    private bool _disposed;

    internal ServerDispatchContractFixture(SharpLinkServer server)
    {
        _server = server;
        _runtime = (SharpLinkRuntimeContext)typeof(SharpLinkServer).GetField("_runtimeContext", Flags)!.GetValue(server)!;
        _registry = (ServerServiceModuleRegistry)typeof(SharpLinkServer).GetField("_serviceModuleRegistry", Flags)!.GetValue(server)!;
        _original = AcquireServices();
    }

    internal static SharpLinkGeneratedMethodDescriptor DeclareMethod(RpcMethodKind kind, bool supportsCancellation, long methodId = 1)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"dispatch-fixture:{methodId}:{kind}:{supportsCancellation}")));
        return new("Invoke", methodId, kind, supportsCancellation, "fixture-request", "fixture-response", fingerprint);
    }

    internal ServerServiceSnapshot.Lease AcquireServices()
    {
        lock (_registry.Gate) return _registry.CaptureServices();
    }

    internal void Publish(FrozenDictionary<long, ServiceRegistration> services)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var current = AcquireServices();
        var added = services.Values.Where(static item => item.Binding is null).ToArray();
        if (added.Any(static item => item.HasModule || item.DeclaredFacts is not null))
            throw new InvalidOperationException("Dispatch fixtures publish only fresh static component registrations.");
        _created.EnsureCapacity(checked(_created.Count + added.Length));
        using var storage = _runtime.CreateMetadataStorage();
        var manifest = PreparedGeneratedManifest.Capture(new FixtureManifest(added), storage);
        using var group = new ServerDeclaredMethodFactsQuota(_runtime.Domain).BeginGroup(manifest);
        var reservations = added.Select(service =>
        {
            var descriptor = manifest.Contracts.Single(item => item.ContractId == service.Stub.InterfaceHash);
            var reservation = group.ReserveContract(descriptor.ContractId);
            reservation.SetMethodCount(descriptor.Methods.Count);
            return (Service: service, Descriptor: descriptor, Reservation: reservation);
        }).ToArray();
        group.Seal();
        ServerServiceSnapshot? candidate = null;
        ServerServiceSnapshot? retired = null;
        var published = false;
        try
        {
            foreach (var item in reservations)
                item.Service.DeclaredFacts = ServerDeclaredMethodFacts.Prepare(item.Descriptor, item.Service.Stub, item.Reservation);
            group.PrepareCommit();
            var plans = Invoke("CreateBindingRegistrationPlans", services, Array.Empty<SharpLinkDynamicModule>(), manifest)!;
            candidate = new ServerServiceSnapshot(services, storage, storage.ReserveCollection(services.Count));
            lock (_registry.Gate)
            {
                if (!_registry.IsCurrentServices(current)) throw new InvalidOperationException("Dispatch fixture publication raced.");
                var generation = (long)Invoke("NextBindingPublicationGeneration")!;
                Invoke("PublishBindingRegistrations", plans, generation);
                group.Commit();
                retired = _registry.PublishServices(candidate);
                _registry.GenerationStorage = generation;
                published = true;
            }
            _created.AddRange(added);
        }
        finally
        {
            retired?.Dispose();
            if (!published)
            {
                candidate?.Dispose();
                foreach (var service in added)
                {
                    service.Binding?.Retire();
                    service.Binding = null;
                    service.DeclaredFacts?.Dispose();
                    service.DeclaredFacts = null;
                }
            }
        }
    }

    internal ulong Bind(ServerConnectionState connection, ServiceRegistration service)
    {
        var quota = (ServerBindingSlotQuota)typeof(SharpLinkServer).GetField("_bindingSlotQuota", Flags)!.GetValue(_server)!;
        var table = connection.InitializeBindings(quota);
        lock (_registry.Gate)
        {
            if (!_registry.ServicesStorage.Values.Any(item => ReferenceEquals(item, service)))
                throw new InvalidOperationException("Only the current exact registration may bind.");
            table.ObserveBindId(1);
            if (table.TryBind(1, service.Binding ?? throw new InvalidOperationException("Registration was not published."),
                    out var entry) != Protocol3BindStatus.Success || entry is null)
                throw new InvalidOperationException("The dispatch fixture's actual Bind failed.");
            table.CompleteBindId(1);
            return entry.Token;
        }
    }

    internal ValueTask DispatchSelected(ServerConnectionState connection, long requestId,
        ProtocolV2FrameFlags flags, ReadOnlySequence<byte> payload, bool captureAdmission = false)
    {
        var request = ServerRequestEnvelopeReader.Read(connection.Session, payload, flags,
            _runtime.Protocol.MaxMetadataBytes, _runtime.TimeProvider);
        if (!SharpLinkServer.TrySelectBoundMethod(connection.Bindings, unchecked((ulong)request.InterfaceHash),
                request.MethodHash, out var selected))
            throw new InvalidOperationException("The dispatch fixture must select its actual live binding.");
        var admission = captureAdmission ? _server.CaptureAdmissionProgramForTests(requestId) : null;
        // This deliberately exercises lower component dispatch, including its Unimplemented
        // projection for an actually selected table's UnknownMethod (not the outer NotFound gate).
        return (ValueTask)((flags & ProtocolV2FrameFlags.OneWay) != 0
            ? Invoke("DispatchOneWayRpc", connection, requestId, flags, payload, connection.CallCancellations,
                CancellationToken.None, admission, selected, request, null, false, 0, null)
            : Invoke("DispatchRpcAsync", connection, requestId, flags, payload, connection.CallCancellations,
                CancellationToken.None, admission, selected, request, null, false, null))!;
    }

    internal static async ValueTask CleanupOwnedAsync(Exception? primary, params Func<ValueTask>[] cleanup)
    {
        List<Exception>? failures = primary is null ? null : [primary];
        foreach (var release in cleanup)
        {
            try { await release().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException("Dispatch fixture operation and independent owner cleanup failed.", failures);
    }

    // Cold invalid-facts controls use the same manifest/domain reservation, without constructing
    // a server, token, provider or proxy. On rejection every scope must return to the caller's baseline.
    internal static void PrepareFactsForRejection(ColdMetadataDomain domain, IDeclaredServerFixtureStub stub)
    {
        using var storage = domain.BeginPreparation();
        var service = ServiceRegistration.CreateSingleton(typeof(ServerDispatchContractFixture), stub, new object(), ownsService: false);
        var manifest = PreparedGeneratedManifest.Capture(new FixtureManifest([service]), storage);
        using var group = new ServerDeclaredMethodFactsQuota(domain).BeginGroup(manifest);
        var descriptor = manifest.Contracts.Single();
        var reservation = group.ReserveContract(descriptor.ContractId);
        reservation.SetMethodCount(descriptor.Methods.Count);
        group.Seal();
        using var facts = ServerDeclaredMethodFacts.Prepare(descriptor, stub, reservation);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        List<Exception>? failures = null;
        try { Publish(_original.Services); }
        catch (Exception error) { (failures ??= []).Add(error); }
        _disposed = true;
        foreach (var service in _created)
        {
            try { service.Binding?.Retire(); await service.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        _created.Clear();
        _original.Dispose();
        if (failures is not null) throw new AggregateException(failures);
    }

    private object? Invoke(string name, params object?[] arguments)
    {
        try { return typeof(SharpLinkServer).GetMethod(name, Flags)!.Invoke(_server, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private sealed class FixtureManifest : ISharpLinkGeneratedAssemblyManifest
    {
        internal FixtureManifest(ServiceRegistration[] services)
        {
            OwnerAssembly = services.FirstOrDefault()?.ContractType.Assembly ?? typeof(ServerDispatchContractFixture).Assembly;
            if (services.Any(service => !ReferenceEquals(service.ContractType.Assembly, OwnerAssembly)))
                throw new InvalidOperationException("One component manifest owns one exact contract Assembly.");
            Contracts = services.Select(service =>
            {
                var stub = service.Stub as IDeclaredServerFixtureStub
                    ?? throw new InvalidOperationException("A dispatch fixture stub must declare its finite method domain.");
                var method = stub.DeclaredMethod;
                return new SharpLinkGeneratedContractDescriptor(service.ContractType, service.ContractType.FullName!,
                    stub.InterfaceHash, method.Fingerprint, [method],
                    static (_, _) => throw new NotSupportedException("This dispatch-only fixture has no proxy factory."),
                    static _ => throw new NotSupportedException("The fixture owns its explicit stub instance."));
            }).ToArray();
            CompileTimeDescriptor = "declared-dispatch-fixture:" + string.Join("|", Contracts.Select(contract =>
                $"{contract.ContractName}:{contract.ContractId}:{contract.Methods[0]}"));
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("declared-dispatch-fixture:" + CompileTimeDescriptor));
            RpcAssemblyHash = new RpcHash128(BinaryPrimitives.ReadUInt64BigEndian(hash), BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(8)));
        }
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "declared-dispatch-fixture";
        public Assembly OwnerAssembly { get; }
        public RpcHash128 RpcAssemblyHash { get; }
        public string CompileTimeDescriptor { get; }
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
