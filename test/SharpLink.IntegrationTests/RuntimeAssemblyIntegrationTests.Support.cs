using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    private sealed class TrackedWeakReferences
    {
        private readonly List<(string Name, WeakReference Reference)> _items = [];

        internal int Count => _items.Count;
        internal bool AnyAlive => _items.Any(static item => item.Reference.IsAlive);
        internal string AliveNames => string.Join(", ", _items
            .Where(static item => item.Reference.IsAlive)
            .Select(static item => item.Name)
            .Distinct(StringComparer.Ordinal));

        internal void Add(string name, object value)
            => _items.Add((name, value as WeakReference ?? new WeakReference(value, trackResurrection: false)));
    }

    // Cold route/factory controls must not connect or wait for remote binding.
    private static object CreateMultiClusterProxy(ISharpLinkMultiClusterClient client, Type contractType)
    {
        var handle = typeof(ISharpLinkMultiClusterClient).GetMethod(nameof(ISharpLinkMultiClusterClient.CreateProxy))!
            .MakeGenericMethod(contractType).Invoke(client, [null])
            ?? throw new InvalidOperationException("Multi-cluster proxy handle was null.");
        return handle.GetType().GetProperty(nameof(RpcProxy<IService>.Service))!.GetValue(handle)
            ?? throw new InvalidOperationException("Multi-cluster service was null.");
    }

    private static Task<object> CreatePreparedMultiClusterProxyAsync(
        ISharpLinkMultiClusterClient client, Type contractType)
        => (Task<object>)typeof(RuntimeAssemblyIntegrationTests).GetMethod(
            nameof(CreatePreparedMultiClusterProxyCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(contractType).Invoke(null, [client])!;

    private static async Task<object> CreatePreparedMultiClusterProxyCoreAsync<TContract>(
        ISharpLinkMultiClusterClient client) where TContract : IService
    {
        var handle = client.CreateProxy<TContract>();
        await handle.PrepareAsync(TimeSpan.FromSeconds(5));
        return handle.Service;
    }

    private sealed class PluginBundle : IDisposable
    {
        private PluginLoadContext? _context;

        private PluginBundle(
            PluginLoadContext context,
            Assembly contractAssembly,
            Assembly? serviceAssembly,
            Type contractType,
            Type? serviceType)
        {
            _context = context;
            ContractAssembly = contractAssembly;
            ServiceAssembly = serviceAssembly ?? contractAssembly;
            ContractType = contractType;
            ServiceType = serviceType;
        }

        internal Assembly ContractAssembly { get; private set; }
        internal Assembly ServiceAssembly { get; private set; }
        internal Type ContractType { get; private set; }
        private Type? ServiceType { get; set; }

        internal static PluginBundle Load(string contextName, bool loadService = true, bool replacement = false)
        {
            var directory = GetPluginOutputDirectory(replacement);
            var context = new PluginLoadContext(contextName, directory);
            var contract = context.LoadFromAssemblyPath(
                Path.Combine(directory, "SharpLink.DynamicPlugin.Contracts.dll"));
            Assembly? service = null;
            Type? serviceType = null;
            if (loadService)
            {
                service = context.LoadFromAssemblyPath(
                    Path.Combine(directory, "SharpLink.DynamicPlugin.Services.dll"));
                serviceType = service.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            }
            return new PluginBundle(
                context,
                contract,
                service,
                contract.GetType("SharpLink.DynamicPlugin.IDynamicPluginService", throwOnError: true)!,
                serviceType);
        }

        internal void ResetServiceState() => InvokeStatic("Reset");

        internal void ReleaseBlock() => InvokeStatic("ReleaseBlock");

        internal void ReleaseSynchronousBlock() => InvokeStatic("ReleaseSynchronousBlock");

        internal void ReleaseRejectResponse() => InvokeStatic("ReleaseRejectResponse");

        internal int GetStaticInt(string propertyName)
            => (int)(ServiceType!.GetProperty(propertyName)!.GetValue(null) ?? -1);

        internal Task GetStaticTask(string propertyName)
            => (Task)(ServiceType!.GetProperty(propertyName)!.GetValue(null) ??
                throw new InvalidOperationException($"Static task '{propertyName}' was null."));

        internal Type GetContractType(string typeName)
            => ContractAssembly.GetType(typeName, throwOnError: true)!;

        internal int GetServiceStaticInt(string typeName, string propertyName)
            => (int)(GetServiceType(typeName).GetProperty(propertyName)!.GetValue(null) ?? -1);

        internal Task GetServiceStaticTask(string typeName, string propertyName)
            => (Task)(GetServiceType(typeName).GetProperty(propertyName)!.GetValue(null) ??
                throw new InvalidOperationException($"Static task '{propertyName}' was null."));

        internal void InvokeServiceStatic(string typeName, string methodName)
            => GetServiceType(typeName).GetMethod(methodName)!.Invoke(null, null);

        private void InvokeStatic(string methodName)
            => ServiceType!.GetMethod(methodName)!.Invoke(null, null);

        private Type GetServiceType(string typeName)
            => ServiceAssembly.GetType(typeName, throwOnError: true)!;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal WeakReference Unload()
        {
            var context = _context ?? throw new ObjectDisposedException(nameof(PluginBundle));
            var weak = new WeakReference(context, trackResurrection: false);
            ContractAssembly = null!;
            ServiceAssembly = null!;
            ContractType = null!;
            ServiceType = null;
            _context = null;
            context.Unload();
            return weak;
        }

        public void Dispose()
        {
            if (_context is not null)
                _ = Unload();
        }

        private static string GetPluginOutputDirectory(bool replacement)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
                directory = directory.Parent;
            if (directory is null)
                throw new DirectoryNotFoundException("SharpLink workspace root was not found.");
            return Path.Combine(
                directory.FullName,
                "test",
                replacement ? "SharpLink.ReplacementServices" : "SharpLink.DynamicServices",
                "bin",
                "Release",
                "net10.0");
        }
    }

    private sealed class PluginLoadContext(string name, string directory)
        : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var shared = Default.Assemblies.FirstOrDefault(candidate =>
                AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
            if (shared is not null)
                return shared;
            var path = Path.Combine(directory, $"{assemblyName.Name}.dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private static (long ContractId, string Fingerprint) AssertCoordinatorRoute(
        SharpLinkMultiClusterClient client, Type contractType, SharpLinkClusterSlot expectedSlot,
        ISharpLinkClient expectedChild, Assembly expectedOwner, Type? absentContractType = null)
    {
        var acquire = typeof(SharpLinkMultiClusterClient).GetMethod("AcquireSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Coordinator owned snapshot accessor is missing.");
        using var snapshot = (MultiClusterSnapshot)(acquire.Invoke(client, null)
            ?? throw new InvalidOperationException("Coordinator owned snapshot was null."));
        Ensure(snapshot.Clusters.TryGetValue(expectedSlot.Key, out var slot) &&
               ReferenceEquals(slot, expectedSlot) && ReferenceEquals(slot.Client, expectedChild),
            "coordinator retains the exact original slot and child");
        Ensure(snapshot.Routes.TryGetValue(contractType, out var route), "the exact contract route is published");
        if (route is null) throw new InvalidOperationException("Expected coordinator route was null.");
        Ensure(ReferenceEquals(route.ContractType, contractType) && ReferenceEquals(route.Slot, expectedSlot) &&
               ReferenceEquals(route.Slot.Client, expectedChild) && ReferenceEquals(route.OwnerAssembly, expectedOwner),
            "the published route retains exact contract, slot, child and assembly identity");
        Ensure(route.ContractId != 0 && !string.IsNullOrWhiteSpace(route.Fingerprint),
            "the published route has genuine generated wire identity");
        Ensure(snapshot.Routes.Values.Count(candidate => candidate.ContractId == route.ContractId) == 1,
            "the ContractId has exactly one coordinator route");
        if (absentContractType is not null)
            Ensure(!snapshot.Routes.ContainsKey(absentContractType), "the competing or retired type is not routed");
        return (route.ContractId, route.Fingerprint); // Scalars only; no unretained map/slot/type escapes.
    }

    // These coordinator controls wrap a real child. Only caller delivery/rejection
    // is controlled here; module election, original tasks and release evidence are
    // always supplied by the actual client registry through the exact mailbox.
    private sealed class ControlledDynamicAssemblyClient : ISharpLinkClient,
        ISharpLinkPreparedAssemblyClient, IDynamicAssemblyRegistrationInspector
    {
        private static readonly FieldInfo RegistryField = typeof(SharpLinkClient)
            .GetField("_assemblyRegistry", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Real child registry field is missing.");
        private readonly Lock _gate = new();
        private SharpLinkClient? _inner;
        private ClientAssemblyRegistry? _registry;
        private Lock? _registryGate;
        private readonly SharpLinkAssemblyRegistrationResult _registrationResult;
        private readonly List<(Task Original, Exception? Expected)> _operations = [];
        private readonly TaskCompletionSource _deliverUnregister =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly InvalidOperationException _rejection = new("controlled child unregister rejected");
        private readonly InvalidOperationException _replacementCleanupFailure = new("controlled replacement cleanup failure");
        private TaskCompletionSource<SharpLinkAssemblyUnregisterResult>? _rejectedUnregister;
        private Task? _stopTask;
        private Assembly? _heldAssembly;
        private SharpLinkDynamicModuleLease _heldCall;
        private int _unregisterCalls;
        private int _rejectNextUnregister;
        private int _blockNextUnregisterRejection;
        private int _publishReplacementThenFailCleanup;

        internal ControlledDynamicAssemblyClient(SharpLinkAssemblyRegistrationResult registrationResult)
            => _registrationResult = registrationResult;
        private SharpLinkClient Inner => _inner ??
            throw new InvalidOperationException("This prepared coordinator control has not materialized its actual child.");
        private ISharpLinkPreparedAssemblyClient Prepared => Inner;
        private void EnsureChild(PreparedGeneratedManifest manifest)
        {
            lock (_gate)
            {
                if (_stopTask is not null) throw new ObjectDisposedException(nameof(ControlledDynamicAssemblyClient));
                if (_inner is not null)
                {
                    if (!ReferenceEquals(((SharpLinkRuntimeContext)_inner.RuntimeContext).Domain.Authority,
                        manifest.Preparation.Authority))
                        throw new InvalidOperationException("This child cannot cross metadata authorities.");
                    return;
                }
                // The first actual prepared source supplies the authority. Building
                // an independent public .Build() here would make Share reject it.
                // A child domain preserves the real parent/child shutdown boundary.
                var domain = manifest.Preparation.Authority.CreateDomain(manifest.Preparation.Domain);
                var builder = SharpClientBuilder.Create().DisableRequestTimeout()
                    .UseTcp(IPAddress.Loopback.ToString(), 1);
                var plan = builder.CompileForMultiCluster([], domain);
                // Materialize owns its synchronous transaction and rollback; never
                // discard the same plan again after a failed ownership transfer.
                _inner = (SharpLinkClient)builder.MaterializeCompiledPlan(plan);
                _registry = (ClientAssemblyRegistry)RegistryField.GetValue(_inner)!;
                _registryGate = (Lock)typeof(ClientAssemblyRegistry)
                    .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_registry)!;
            }
        }
        internal TaskCompletionSource<bool> FirstUnregisterStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> RejectedUnregisterStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SharpLinkConnectionState State => Inner.State;
        public SharpLinkClientLifecycleState LifecycleState => Inner.LifecycleState;
        public SharpLinkReadinessState Readiness => Inner.Readiness;
        public SharpLinkClusterState ClusterState => Inner.ClusterState;
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => Inner.StartAsync(cancellationToken);
        public ValueTask WaitForReadyAsync(CancellationToken cancellationToken = default) => Inner.WaitForReadyAsync(cancellationToken);
        public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => Inner.WaitForShutdownAsync(cancellationToken);
        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => Inner.ConnectAsync(cancellationToken);
        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Inner.CheckHealthAsync(cancellationToken);
        public IRpcRuntimeContext RuntimeContext => Inner.RuntimeContext;
        public RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null) where TContract : IService
            => Inner.CreateProxy<TContract>(options);
        public RpcLocalContract GetLocalContract<TContract>() where TContract : IService => Inner.GetLocalContract<TContract>();
        public RpcLocalContract GetLocalContract(Assembly registeredAssembly, long contractId)
            => Inner.GetLocalContract(registeredAssembly, contractId);
        public IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null)
            => Inner.CreateContractChannel(local, options);

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => throw new NotSupportedException("This coordinator control requires prepared metadata.");
        public SharpLinkAssemblyRegistrationResult RegisterPreparedAssembly(PreparedGeneratedManifest manifest,
            Func<Func<SharpLinkAssemblyRegistrationResult>, SharpLinkAssemblyRegistrationResult>? publication = null)
        {
            if (!_registrationResult.Succeeded) return _registrationResult;
            EnsureChild(manifest);
            return Prepared.RegisterPreparedAssembly(manifest, publication);
        }
        public ClientRetirementBudget CaptureRetirementBudget(TimeSpan gracefulTimeout)
            => Prepared.CaptureRetirementBudget(gracefulTimeout);

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly, ClientRetirementBudget budget, ChildAssemblySelectionMailbox? mailbox = null)
        {
            if (Interlocked.Exchange(ref _rejectNextUnregister, 0) != 0)
            {
                mailbox?.FinalizeDisposition(); // No Claim, no selected module, no invented physical task.
                return new ValueTask<SharpLinkAssemblyUnregisterResult>(
                    Own(Task.FromException<SharpLinkAssemblyUnregisterResult>(_rejection), _rejection));
            }
            if (Interlocked.Exchange(ref _blockNextUnregisterRejection, 0) != 0)
            {
                var rejected = new TaskCompletionSource<SharpLinkAssemblyUnregisterResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) _rejectedUnregister = rejected;
                mailbox?.FinalizeDisposition();
                var original = Own(rejected.Task, _rejection);
                RejectedUnregisterStarted.TrySetResult(true);
                return new ValueTask<SharpLinkAssemblyUnregisterResult>(original);
            }
            if (Interlocked.Increment(ref _unregisterCalls) == 1)
            {
                // A real acquired module lease, not a manufactured deferred result.
                // No other operation mutates this exact child registration here.
                lock (_registryGate ?? throw new InvalidOperationException("Real child registry is not initialized."))
                {
                    var module = _registry!.DynamicModules[assembly];
                    lock (_gate)
                    {
                        if (_heldCall.IsAcquired || !module.TryAcquire(stream: false, out _heldCall))
                            throw new InvalidOperationException("Could not acquire the actual deferred retirement call.");
                        _heldAssembly = assembly;
                    }
                }
                var original = Own(Prepared.UnregisterAssemblyAsync(assembly, budget, mailbox).AsTask());
                var delivery = Own(DeliverUnregisterAsync(original));
                FirstUnregisterStarted.TrySetResult(true);
                return new ValueTask<SharpLinkAssemblyUnregisterResult>(delivery);
            }
            return new ValueTask<SharpLinkAssemblyUnregisterResult>(
                Own(Prepared.UnregisterAssemblyAsync(assembly, budget, mailbox).AsTask()));
        }
        private async Task<SharpLinkAssemblyUnregisterResult> DeliverUnregisterAsync(Task<SharpLinkAssemblyUnregisterResult> original)
        {
            await _deliverUnregister.Task.ConfigureAwait(false);
            return await original.ConfigureAwait(false);
        }
        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly, TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
        {
            var original = UnregisterAssemblyAsync(assembly, CaptureRetirementBudget(gracefulTimeout)).AsTask();
            return cancellationToken.CanBeCanceled ? new(original.WaitAsync(cancellationToken)) : new(original);
        }
        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly, Assembly newAssembly, TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This coordinator control requires prepared metadata.");
        public ValueTask<SharpLinkAssemblyReplacementResult> ReplacePreparedAssemblyAsync(
            Assembly oldAssembly, PreparedGeneratedManifest manifest, ClientRetirementBudget budget,
            ChildAssemblySelectionMailbox? mailbox = null)
        {
            if (Interlocked.Exchange(ref _publishReplacementThenFailCleanup, 0) == 0)
                throw new NotSupportedException();
            var original = Own(Prepared.ReplacePreparedAssemblyAsync(oldAssembly, manifest, budget, mailbox).AsTask());
            return new ValueTask<SharpLinkAssemblyReplacementResult>(Own(
                CompleteReplacementCleanupAsync(original, mailbox), _replacementCleanupFailure));
        }
        private async Task<SharpLinkAssemblyReplacementResult> CompleteReplacementCleanupAsync(
            Task<SharpLinkAssemblyReplacementResult> original, ChildAssemblySelectionMailbox? mailbox)
        {
            var result = await original.ConfigureAwait(false);
            if (!result.Succeeded) return result;
            // Independent decorator cleanup after REAL child publication/retirement.
            // This is not a fabricated child physical-task fault or release certificate.
            mailbox?.RecordInvocationFailure(_replacementCleanupFailure);
            throw _replacementCleanupFailure;
        }

        private Task<T> Own<T>(Task<T> original, Exception? expected = null)
        {
            lock (_gate) _operations.Add((original, expected));
            return original;
        }
        public bool IsDynamicAssemblyRegistered(Assembly assembly) => Inner.IsDynamicAssemblyRegistered(assembly);
        internal void CompleteTimedOutUnregister() => _deliverUnregister.TrySetResult();
        internal void ReleaseAssembly(Assembly assembly)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_heldAssembly, assembly) || !_heldCall.IsAcquired)
                    throw new InvalidOperationException("The requested assembly does not own the held module call.");
                var original = _heldCall; _heldCall = default; _heldAssembly = null;
                original.Dispose();
            }
        }
        internal void RejectNextUnregister() => Volatile.Write(ref _rejectNextUnregister, 1);
        internal void BlockAndRejectNextUnregister() => Volatile.Write(ref _blockNextUnregisterRejection, 1);
        internal void PublishReplacementThenFailCleanup() => Volatile.Write(ref _publishReplacementThenFailCleanup, 1);
        internal void CompleteRejectedUnregister()
        {
            lock (_gate)
                (_rejectedUnregister ?? throw new InvalidOperationException("No original rejection is pending."))
                    .TrySetException(_rejection);
        }
        internal int UnregisterCalls => Volatile.Read(ref _unregisterCalls);

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            Task original;
            lock (_gate) original = _stopTask ??= StopCoreAsync();
            return cancellationToken.CanBeCanceled ? new(original.WaitAsync(cancellationToken)) : new(original);
        }
        public ValueTask DisposeAsync() => StopAsync();
        private async Task StopCoreAsync()
        {
            await Task.Yield();
            List<Exception> failures = [];
            _deliverUnregister.TrySetResult();
            lock (_gate)
            {
                _rejectedUnregister?.TrySetException(_rejection);
                if (_heldCall.IsAcquired)
                {
                    var held = _heldCall; _heldCall = default; _heldAssembly = null;
                    try { held.Dispose(); } catch (Exception failure) { failures.Add(failure); }
                }
            }
            Task? stop = null;
            try
            {
                if (_inner is not null)
                { stop = _inner.StopAsync().AsTask(); await stop.ConfigureAwait(false); }
            }
            catch (Exception failure) { failures.Add(stop is null ? failure : OriginalFailure(stop, failure)); }
            (Task Original, Exception? Expected)[] originals;
            lock (_gate) originals = _operations.ToArray();
            foreach (var item in originals)
            {
                try { await item.Original.ConfigureAwait(false); }
                catch (Exception failure)
                {
                    var graph = OriginalFailure(item.Original, failure);
                    // Only this exact source task's predefined expected marker is
                    // consumed; arbitrary OCE and unrelated stage faults are not.
                    if (!ReferenceEquals(graph, item.Expected)) failures.Add(graph);
                }
            }
            if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        private static Exception OriginalFailure(Task original, Exception awaited)
            => original.Exception is { } graph
                ? graph.InnerExceptions.Count == 1 ? graph.InnerExceptions[0] : graph : awaited;
    }

    private sealed class BlockingConnectClient : ISharpLinkClient
    {
        private readonly bool _releaseWhenStopped;
        private readonly bool _ignoreCancellation;
        private readonly TaskCompletionSource _connectRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = (int)SharpLinkConnectionState.Created;
        private int _lifecycle = (int)SharpLinkClientLifecycleState.Created;
        private readonly TaskCompletionSource _shutdown =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SharpLinkClientLifecycleState LifecycleState =>
            (SharpLinkClientLifecycleState)Volatile.Read(ref _lifecycle);
        public SharpLinkReadinessState Readiness => LifecycleState == SharpLinkClientLifecycleState.Running &&
            State == SharpLinkConnectionState.Ready ? SharpLinkReadinessState.Ready : SharpLinkReadinessState.NotReady;
        public SharpLinkClusterState ClusterState => LifecycleState == SharpLinkClientLifecycleState.Stopped
            ? SharpLinkClusterState.Stopped
            : State == SharpLinkConnectionState.Ready ? SharpLinkClusterState.Ready : SharpLinkClusterState.Inactive;
        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = Interlocked.CompareExchange(ref _lifecycle,
                (int)SharpLinkClientLifecycleState.Running, (int)SharpLinkClientLifecycleState.Created);
            if (previous == (int)SharpLinkClientLifecycleState.Stopped)
                throw new InvalidOperationException("The blocked connection fixture has stopped.");
            return ValueTask.CompletedTask;
        }
        public async ValueTask WaitForReadyAsync(CancellationToken cancellationToken = default)
        {
            await StartAsync(cancellationToken);
            await ConnectAsync(cancellationToken);
            if (Readiness != SharpLinkReadinessState.Ready)
                throw new InvalidOperationException("The blocked connection fixture stopped before readiness.");
        }
        public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
            => _shutdown.Task.WaitAsync(cancellationToken);


        internal BlockingConnectClient(bool releaseWhenStopped = true, bool ignoreCancellation = false)
        {
            _releaseWhenStopped = releaseWhenStopped;
            _ignoreCancellation = ignoreCancellation;
        }

        internal TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SharpLinkConnectionState State => (SharpLinkConnectionState)Volatile.Read(ref _state);

        public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult();
            if (_ignoreCancellation)
                await _connectRelease.Task.ConfigureAwait(false);
            else
                await _connectRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _state, (int)SharpLinkConnectionState.Ready);
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (_releaseWhenStopped)
                _connectRelease.TrySetResult();
            Volatile.Write(ref _state, (int)SharpLinkConnectionState.Stopped);
            Volatile.Write(ref _lifecycle, (int)SharpLinkClientLifecycleState.Stopped);
            _shutdown.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Ready));

        // This fixture exercises lifecycle/readiness only, never contract construction.
        public IRpcRuntimeContext RuntimeContext => throw new NotSupportedException();

        public RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null)
            where TContract : IService => throw new NotSupportedException();

        public RpcLocalContract GetLocalContract<TContract>() where TContract : IService
            => throw new NotSupportedException();

        public RpcLocalContract GetLocalContract(Assembly registeredAssembly, long contractId)
            => throw new NotSupportedException();

        public IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null)
            => throw new NotSupportedException();

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => StopAsync();

        internal void ReleaseConnect() => _connectRelease.TrySetResult();
    }

    private sealed class DynamicHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;
        private readonly ServiceProvider _serviceProvider;
        private string? _expectedServerStopFailure;

        private DynamicHarness(
            ISharpLinkServer server,
            ISharpLinkClient client,
            int port,
            CancellationTokenSource serverCancellation,
            Task serverTask,
            ServiceProvider serviceProvider)
        {
            Server = server;
            Client = client;
            Port = port;
            _serverCancellation = serverCancellation;
            _serverTask = serverTask;
            _serviceProvider = serviceProvider;
        }

        internal ISharpLinkServer Server { get; }
        internal ISharpLinkClient Client { get; }
        internal int Port { get; }

        internal void ExpectServerStopFailure(string message)
            => _expectedServerStopFailure = message;

        internal static async Task<DynamicHarness> CreateAsync(
            bool registerDynamicServiceDependencies = true,
            IManifestReadinessControl? control = null,
            ISharpLinkCompressionProvider? compressionProvider = null,
            Action<IServiceCollection>? configureServices = null)
        {
            var serverCancellation = new CancellationTokenSource();
            var services = new ServiceCollection();
            if (registerDynamicServiceDependencies)
                services.AddSingleton(TimeProvider.System);
            configureServices?.Invoke(services);
            var serviceProvider = services.BuildServiceProvider();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback)
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseServiceProvider(serviceProvider);
            if (control is not null)
                serverBuilder.ReplaceService(control);
            if (compressionProvider is not null)
                serverBuilder.UseRuntime(options => options.Compression.Providers.Add(compressionProvider));
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = server.RunUntilStoppedAsync(serverCancellation.Token).AsTask();
            var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            if (compressionProvider is not null)
                clientBuilder.UseRuntime(options => options.Compression.Providers.Add(compressionProvider));
            var client = clientBuilder.Build();
            await client.ConnectAsync();
            return new DynamicHarness(server, client, port, serverCancellation, serverTask, serviceProvider);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.StopAsync();
            try
            {
                await Server.StopAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (
                _expectedServerStopFailure is { } message && ContainsMessage(exception, message))
            {
            }
            await _serverCancellation.CancelAsync();
            try
            {
                await _serverTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or ObjectDisposedException or IOException or SocketException ||
                _expectedServerStopFailure is { } message && ContainsMessage(exception, message))
            {
            }
            _serverCancellation.Dispose();
            await _serviceProvider.DisposeAsync();
        }
    }
}

[RpcContract]
public interface IShutdownCleanupProbe : IService
{
    ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken);
}

[RpcService]
public sealed class ShutdownCleanupProbe : IShutdownCleanupProbe, IAsyncDisposable
{
    private static int _disposed;

    internal static int Disposed => Volatile.Read(ref _disposed);

    internal static void Reset() => Volatile.Write(ref _disposed, 0);

    public ValueTask<int> TouchAsync(int value, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return ValueTask.FromResult(value + 100);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposed);
        return ValueTask.CompletedTask;
    }
}
