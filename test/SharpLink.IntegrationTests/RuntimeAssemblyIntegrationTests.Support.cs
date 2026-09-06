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

    private static object? GetMultiClusterProxy(ISharpLinkMultiClusterClient client, Type contractType)
        => typeof(ISharpLinkMultiClusterClient).GetMethod(nameof(ISharpLinkMultiClusterClient.Get))!
            .MakeGenericMethod(contractType)
            .Invoke(client, null);

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

        internal static PluginBundle Load(string contextName, bool loadService = true)
        {
            var directory = GetPluginOutputDirectory();
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

        private static string GetPluginOutputDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
                directory = directory.Parent;
            if (directory is null)
                throw new DirectoryNotFoundException("SharpLink workspace root was not found.");
            return Path.Combine(
                directory.FullName,
                "test",
                "SharpLink.DynamicServices",
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

    private sealed class ControlledDynamicAssemblyClient : ISharpLinkClient, IDynamicAssemblyRegistrationInspector
    {
        private readonly Lock _gate = new();
        private readonly HashSet<Assembly> _registeredAssemblies = new(ReferenceEqualityComparer.Instance);
        private readonly SharpLinkAssemblyRegistrationResult _registrationResult;
        private int _unregisterCalls;
        private int _rejectNextUnregister;
        private int _blockNextUnregisterRejection;
        private int _publishReplacementThenFailCleanup;

        internal ControlledDynamicAssemblyClient(SharpLinkAssemblyRegistrationResult registrationResult)
        {
            _registrationResult = registrationResult;
        }

        internal TaskCompletionSource<bool> FirstUnregisterStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> RejectedUnregisterStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<SharpLinkAssemblyUnregisterResult> FirstUnregisterCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<SharpLinkAssemblyUnregisterResult> RejectedUnregisterCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SharpLinkConnectionState State => SharpLinkConnectionState.Ready;

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
        {
            lock (_gate)
                _registeredAssemblies.Add(assembly);
            return _registrationResult;
        }

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
        {
            _ = assembly;
            _ = gracefulTimeout;
            _ = cancellationToken;
            if (Interlocked.Exchange(ref _rejectNextUnregister, 0) != 0)
            {
                return ValueTask.FromException<SharpLinkAssemblyUnregisterResult>(
                    new InvalidOperationException("controlled child unregister rejected"));
            }
            if (Interlocked.Exchange(ref _blockNextUnregisterRejection, 0) != 0)
            {
                RejectedUnregisterStarted.TrySetResult(true);
                return new ValueTask<SharpLinkAssemblyUnregisterResult>(RejectedUnregisterCompletion.Task);
            }
            if (Interlocked.Increment(ref _unregisterCalls) == 1)
            {
                FirstUnregisterStarted.TrySetResult(true);
                return new ValueTask<SharpLinkAssemblyUnregisterResult>(FirstUnregisterCompletion.Task);
            }
            return ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = false });
        }

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
        {
            _ = gracefulTimeout;
            _ = cancellationToken;
            if (Interlocked.Exchange(ref _publishReplacementThenFailCleanup, 0) == 0)
                throw new NotSupportedException();
            lock (_gate)
            {
                _registeredAssemblies.Remove(oldAssembly);
                _registeredAssemblies.Add(newAssembly);
            }
            return ValueTask.FromException<SharpLinkAssemblyReplacementResult>(
                new InvalidOperationException("controlled replacement cleanup failure"));
        }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Ready));

        public TContract Get<TContract>() where TContract : IService
            => default!;



        public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService


            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public bool IsDynamicAssemblyRegistered(Assembly assembly)
        {
            lock (_gate)
                return _registeredAssemblies.Contains(assembly);
        }

        internal void CompleteTimedOutUnregister()
            => FirstUnregisterCompletion.TrySetResult(new SharpLinkAssemblyUnregisterResult
            {
                ReferencesReleased = false,
                RemainingCalls = 1
            });

        internal void ReleaseAssembly(Assembly assembly)
        {
            lock (_gate)
                _registeredAssemblies.Remove(assembly);
        }

        internal void RejectNextUnregister() => Volatile.Write(ref _rejectNextUnregister, 1);

        internal void BlockAndRejectNextUnregister() => Volatile.Write(ref _blockNextUnregisterRejection, 1);

        internal void PublishReplacementThenFailCleanup()
            => Volatile.Write(ref _publishReplacementThenFailCleanup, 1);

        internal void CompleteRejectedUnregister()
            => RejectedUnregisterCompletion.TrySetException(
                new InvalidOperationException("controlled child unregister rejected"));

        internal int UnregisterCalls => Volatile.Read(ref _unregisterCalls);
    }

    private sealed class BlockingConnectClient : ISharpLinkClient
    {
        private readonly bool _releaseWhenStopped;
        private readonly bool _ignoreCancellation;
        private readonly TaskCompletionSource _connectRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = (int)SharpLinkConnectionState.Created;

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
            return ValueTask.CompletedTask;
        }

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Ready));

        public TContract Get<TContract>() where TContract : IService => throw new NotSupportedException();



        public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService


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
            bool registerDynamicServiceDependencies = true)
        {
            var serverCancellation = new CancellationTokenSource();
            var services = new ServiceCollection();
            if (registerDynamicServiceDependencies)
                services.AddSingleton(TimeProvider.System);
            var serviceProvider = services.BuildServiceProvider();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .UseServiceProvider(serviceProvider);
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = server.RunAsync(serverCancellation.Token).AsTask();
            var client = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .Build();
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
