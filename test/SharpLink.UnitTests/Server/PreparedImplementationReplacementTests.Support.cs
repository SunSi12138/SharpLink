using System.Collections.Frozen;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed partial class PreparedImplementationReplacementTests
{
    // These local primitive tests deliberately bypass only the coordinator proof by reflection.
    // They do NOT claim remote quiescence; integrated protocol tests own that separate certificate.
    private static Task ReleaseLocally(SharpLinkServer.PreparedImplementationReplacement plan)
        => (Task)InvokePrivate(plan, "StartRelease", (Action)(static () => { }))!;

    private static void CommitLocally(SharpLinkServer.PreparedImplementationReplacement plan)
        => InvokePrivate(plan, "CommitPausedCore");

    private static object? InvokePrivate(object target, string method, params object?[] arguments)
    {
        try
        {
            return target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }

    private static async Task RejectAsync(Func<Task> action, string message)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        catch (AggregateException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PluginContext _contractContext;
        private PluginContext? _oldContext;
        private readonly PluginContext _newContext;
        private readonly ServiceProvider _services;
        private readonly List<ServerConnectionState> _connections = [];
        internal readonly ManifestView ContractManifest;
        internal readonly SharpLinkServer Server;
        internal readonly ServerServiceModuleRegistry Registry;
        internal readonly SharpLinkRuntimeContext Runtime;
        internal Assembly OldAssembly;
        internal readonly Assembly NewAssembly;
        internal Assembly ContractAssembly => ContractManifest.OwnerAssembly;
        internal bool FailStubPreparation;
        internal Action? DuringStubPreparation;

        private Fixture()
        {
            var path = PluginPath;
            _contractContext = new PluginContext();
            var contract = _contractContext.LoadFromAssemblyPath(Path.Combine(path, "SharpLink.DynamicPlugin.Contracts.dll"));
            var loaded = SharpLinkAssemblyManifestLoader.TryLoad(contract, out var contractManifest);
            Ensure(loaded.Succeeded, loaded.Error?.Message ?? "contract load failed");
            ContractManifest = new ManifestView(contractManifest!, stub => codecs =>
            {
                DuringStubPreparation?.Invoke();
                return FailStubPreparation
                    ? throw new InvalidOperationException("injected codec binding failure") : stub(codecs);
            });
            _oldContext = new PluginContext(contract);
            _newContext = new PluginContext(contract);
            OldAssembly = _oldContext.LoadFromAssemblyPath(Path.Combine(path, "SharpLink.DynamicPlugin.Services.dll"));
            NewAssembly = _newContext.LoadFromAssemblyPath(Path.Combine(path, "SharpLink.DynamicPlugin.Services.dll"));
            _services = new ServiceCollection().AddSingleton(TimeProvider.System).BuildServiceProvider();
            Server = (SharpLinkServer)SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(new FixedGeneratedManifestSource([ContractManifest]))
                .UseTransport(new IdleListener())
                .UseServiceProvider(_services)
                .Build();
            Registry = Field<ServerServiceModuleRegistry>(Server, "_serviceModuleRegistry");
            Runtime = Field<SharpLinkRuntimeContext>(Server, "_runtimeContext");
            var registered = Server.RegisterAssembly(OldAssembly);
            Ensure(registered.Succeeded, registered.Error?.Message ?? "service load failed");
        }

        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            await fixture.Server.StartAsync();
            return fixture;
        }

        internal SharpLinkServer.PreparedImplementationReplacement Prepare()
            => Server.PrepareImplementationReplacement([(OldAssembly, NewAssembly)]);

        internal SharpLinkDynamicModule OldModule => Registry.DynamicModules.TryGetValue(OldAssembly, out var module)
            ? module : throw new InvalidOperationException("old module missing");

        internal ServiceRegistration Registration(string contractName)
            => Registry.ServicesStorage.Values.Single(service => service.ContractType.Name == contractName);

        internal void CreateSingleton(string contractName)
        {
            Ensure(Registration(contractName).TryAcquireDynamicSingleton(false, out _, out var lease), "singleton admission");
            lease.Dispose();
        }

        internal async Task<ServerConnectionState> CreateConnectionService(string contractName)
        {
            var session = new RpcSession(new TestTransportConnection(),
                new RpcSessionCreationOptions(RpcSessionRole.Server, Runtime));
            var connection = new ServerConnectionState(session, new RpcSessionGeneratedServerBridge(session),
                new StripedLongMap<ServerCallCancellationState>(Runtime.Concurrency),
                CancellationToken.None, Runtime.TimeProvider);
            Ensure(connection.MarkReady(new SharpLinkAuthenticationContext("local-test")), "ready transition");
            var connections = Field<ServerConnectionRegistry>(Server, "_connectionRegistry");
            Ensure(connections.TryAdd(session.Id, connection), "connection registry insertion");
            _connections.Add(connection);
            var registration = Registration(contractName);
            Ensure(registration.Module!.TryAcquire(false, out var moduleLease), "module lease");
            var lease = await connection.AcquireServiceAsync(registration, moduleLease);
            await lease.DisposeAsync();
            return connection;
        }

        internal Task OldTask(string type, string property)
            => (Task)OldAssembly.GetType("SharpLink.DynamicPlugin." + type)!.GetProperty(property)!.GetValue(null)!;

        internal void OldStatic(string type, string method)
            => OldAssembly.GetType("SharpLink.DynamicPlugin." + type)!.GetMethod(method)!.Invoke(null, null);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal WeakReference UnloadOld()
        {
            var context = _oldContext!;
            var weak = new WeakReference(context);
            OldAssembly = null!;
            _oldContext = null;
            context.Unload();
            return weak;
        }

        public async ValueTask DisposeAsync()
        {
            if (OldAssembly is not null)
                OldStatic("RetiredConnectionService", "ReleaseDispose");
            try { await Server.StopAsync(TimeSpan.Zero); }
            catch { /* Failure tests assert the operation, not shutdown aggregation. */ }
            foreach (var connection in _connections)
            {
                try { await connection.CloseAsync(); }
                catch { }
            }
            await _services.DisposeAsync();
            _oldContext?.Unload();
            _newContext.Unload();
            _contractContext.Unload();
        }
    }

    private static string PluginPath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            var configuration = directory.Parent!.Name;
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
                directory = directory.Parent;
            return Path.Combine(directory!.FullName, "test", "SharpLink.DynamicServices", "bin", configuration, "net10.0");
        }
    }

    private sealed class PluginContext(Assembly? contract = null) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (contract is not null && AssemblyName.ReferenceMatchesDefinition(contract.GetName(), name))
                return contract;
            return Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), name));
        }
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManifestView : ISharpLinkGeneratedAssemblyManifest, ISharpLinkReferencedCodecDependencyManifest
    {
        private readonly ISharpLinkGeneratedAssemblyManifest _inner;
        private readonly IReadOnlyList<SharpLinkGeneratedContractDescriptor> _contracts;
        internal ManifestView(ISharpLinkGeneratedAssemblyManifest inner,
            Func<Func<IRpcCodecProvider, IRpcStub>, Func<IRpcCodecProvider, IRpcStub>>? wrap = null)
        {
            _inner = inner;
            _contracts = wrap is null ? inner.Contracts : inner.Contracts.Select(contract =>
                contract with { StubFactory = wrap(contract.StubFactory) }).ToArray();
        }
        public int ApiVersion => _inner.ApiVersion;
        public int ProtocolVersion => _inner.ProtocolVersion;
        public string GeneratorVersion => _inner.GeneratorVersion;
        public Assembly OwnerAssembly => _inner.OwnerAssembly;
        public RpcHash128 RpcAssemblyHash => EmptyHash ? default : OverrideHash ?? _inner.RpcAssemblyHash;
        internal RpcHash128? OverrideHash { get; set; }
        internal bool EmptyHash { get; set; }
        internal IReadOnlyList<string>? OverrideDependencies { get; set; }
        public string CompileTimeDescriptor => _inner.CompileTimeDescriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => _contracts;
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => _inner.Services;
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => _inner.Codecs;
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => _inner.ContractCodecs;
        public IReadOnlyList<string> Dependencies => OverrideDependencies ?? _inner.Dependencies;
        public IReadOnlyList<string> ContractDependencies => _inner.ContractDependencies;
        public IReadOnlyList<SharpLinkReferencedCodecDependency> ReferencedCodecDependencies
            => (_inner as ISharpLinkReferencedCodecDependencyManifest)?.ReferencedCodecDependencies ?? [];
    }
}
