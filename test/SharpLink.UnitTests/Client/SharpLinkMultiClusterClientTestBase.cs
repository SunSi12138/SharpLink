using System.Collections.Frozen;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public abstract class SharpLinkMultiClusterClientTestBase
{
    protected static readonly TimeSpan RaceCoordinationTimeout = TimeSpan.FromSeconds(10);
    protected static readonly Assembly TestManifestAssembly =
        typeof(SharpLinkMultiClusterClientTestBase).Assembly;

    protected static async Task EnsureThrows<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    protected static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    protected static async Task WaitForConditionAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2d);
        while (!condition() && Stopwatch.GetTimestamp() < deadline)
            await Task.Delay(10);
        Ensure(condition(), failureMessage);
    }

    protected static SharpLinkMultiClusterClientBuilder CreateBuilder(
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests,
        IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> routes)
        => SharpLinkMultiClusterClientBuilder.Create()
            .DisableRequestTimeout()
            .UseGeneratedDiscoverySources(
                new FixedGeneratedManifestSource(manifests),
                new FixedGeneratedClusterRouteSource(routes));

    protected static SharpLinkMultiClusterClientBuilder CreateStaticBuilder()
        => CreateBuilder([Manifest.Instance], [RouteManifest.Instance]);

    protected static SharpLinkMultiClusterClientBuilder CreateDynamicBuilder()
        => CreateBuilder([], []);

    protected static IRpcChannel GetChildChannel(
        ISharpLinkMultiClusterClient client,
        SharpLinkClusterKey cluster)
    {
        var coordinator = (SharpLinkMultiClusterClient)client;
        var snapshot = (MultiClusterSnapshot)typeof(SharpLinkMultiClusterClient)
            .GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        return (IRpcChannel)snapshot.Clusters[cluster].Client;
    }

    protected static ValueTask AddClusterWithFixedDiscoveryAsync(
        ISharpLinkMultiClusterClient client,
        SharpLinkClusterKey cluster,
        Action<SharpClientBuilder> configure,
        Action<SharpLinkMultiClusterSlotOptions>? configureSlot = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? manifests = null,
        IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest>? routes = null)
        => client.AddClusterAsync(
            cluster,
            child =>
            {
                child.DisableRequestTimeout();
                configure(child);
            },
            configureSlot,
            cancellationToken,
            new FixedGeneratedManifestSource(manifests ?? [Manifest.Instance]),
            new FixedGeneratedClusterRouteSource(routes ?? [RouteManifest.Instance]));

    protected static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    protected static void EnsureCodecIsMissing<T>(IRpcChannel channel)
    {
        Exception? failure = null;
        try
        {
            _ = channel.RuntimeContext.Codecs.GetCodec<T>();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Ensure(failure is NotSupportedException,
            $"child Runtime must not resolve unrelated Codec '{typeof(T).Name}'");
    }

    protected static void CollectWeakCatalogEntries()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _ = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot();
        _ = SharpLinkGeneratedClusterRouteCatalog.CreateSnapshot();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    protected static WeakReference RegisterUnconfiguredRouteManifest()
    {
        ISharpLinkGeneratedClusterRouteManifest manifest = new UnconfiguredRouteManifest();
        SharpLinkGeneratedClusterRouteCatalog.Register(manifest);
        return new WeakReference(manifest);
    }

    protected static SharpLinkEndpoint Endpoint(string id, int port)
        => new()
        {
            Id = id,
            Address = new SharpLinkTcpAddress("127.0.0.1", port)
        };

    protected interface IOrdersContract : IService;
    protected interface IUnroutedContract : IService;

    protected sealed class OrdersProxy(IRpcChannel channel) : IOrdersContract
    {
        internal IRpcChannel Channel { get; } = channel;
    }

    protected sealed class Manifest : ISharpLinkGeneratedAssemblyManifest
    {
        public static readonly Manifest Instance = new();
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => TestManifestAssembly;
        public RpcHash128 RpcAssemblyHash => new(0x6d756c7469636c75UL, 0x737465722d763031UL);
        public string CompileTimeDescriptor => "multi-cluster-test";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } =
        [
            new SharpLinkGeneratedContractDescriptor(
                typeof(IOrdersContract),
                typeof(IOrdersContract).FullName!,
                8_101,
                "0101010101010101010101010101010101010101010101010101010101010101",
                [],
                static (channel, _) => new OrdersProxy(channel),
                static _ => throw new NotSupportedException())
        ];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; } = [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } =
            [new TestCodecFactory<OrdersValue>()];
        public IReadOnlyList<string> Dependencies { get; } = [];
    }

    protected sealed class RouteManifest : ISharpLinkGeneratedClusterRouteManifest
    {
        public static readonly RouteManifest Instance = new();
        public Assembly OwnerAssembly => TestManifestAssembly;
        public IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes { get; } =
        [
            new SharpLinkGeneratedClusterAssemblyRoute(
                "orders",
                TestManifestAssembly,
                TestManifestAssembly.FullName!)
        ];
    }

    protected sealed class TestCodecFactory<T> : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(T);
        public RpcHash128 CodecHash => new(0x6d756c7469636c75UL, 0x737465722d636f64UL);
        public string? AdapterId => null;
        public IRpcCodecAdapter? Adapter => null;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => adapterScope is null
                ? new TestCodec<T>()
                : throw new ArgumentException("Native Codec does not accept an adapter scope.", nameof(adapterScope));
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
    }

    protected sealed class TestCodec<T> : IRpcCodec<T>
    {
        public void Serialize(in T value, IBufferWriter<byte> buffer)
        {
        }

        public T? Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }

    protected sealed class OrdersValue;

    protected sealed class CountingManifestSource(
        Func<IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>> createSnapshot)
        : IGeneratedManifestSource
    {
        private int _createSnapshotCount;
        internal int CreateSnapshotCount => Volatile.Read(ref _createSnapshotCount);

        public IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> CreateSnapshot()
        {
            Interlocked.Increment(ref _createSnapshotCount);
            return createSnapshot();
        }
    }

    protected sealed class CountingRouteSource(
        Func<IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest>> createSnapshot)
        : IGeneratedClusterRouteSource
    {
        private int _createSnapshotCount;
        internal int CreateSnapshotCount => Volatile.Read(ref _createSnapshotCount);

        public IReadOnlyList<ISharpLinkGeneratedClusterRouteManifest> CreateSnapshot()
        {
            Interlocked.Increment(ref _createSnapshotCount);
            return createSnapshot();
        }
    }

    protected sealed class ThrowingCodecManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(string).Assembly;
        public string CompileTimeDescriptor => "unrelated-manifest";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs
            => throw new InvalidOperationException("Unrelated manifests must not be read by a filtered child.");
        public IReadOnlyList<string> Dependencies => [];
    }

    protected sealed class UnconfiguredRouteManifest : ISharpLinkGeneratedClusterRouteManifest
    {
        public Assembly OwnerAssembly => typeof(SharpLinkMultiClusterClientTestBase).Assembly;
        public IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes { get; } =
        [
            new SharpLinkGeneratedClusterAssemblyRoute(
                "unconfigured",
                typeof(string).Assembly,
                typeof(string).Assembly.FullName!)
        ];
    }

    protected sealed class InvalidRuntimeRouteManifest : ISharpLinkGeneratedClusterRouteManifest
    {
        public Assembly OwnerAssembly => typeof(SharpLinkMultiClusterClientTestBase).Assembly;
        public IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes { get; } =
        [
            new SharpLinkGeneratedClusterAssemblyRoute(
                "invalid-runtime",
                typeof(string).Assembly,
                typeof(string).Assembly.FullName!)
        ];
    }

    protected sealed class ConflictingRuntimeRouteManifest : ISharpLinkGeneratedClusterRouteManifest
    {
        public Assembly OwnerAssembly => typeof(SharpLinkMultiClusterClientTestBase).Assembly;
        public IReadOnlyList<SharpLinkGeneratedClusterAssemblyRoute> Routes { get; } =
        [
            new SharpLinkGeneratedClusterAssemblyRoute(
                "conflict",
                TestManifestAssembly,
                TestManifestAssembly.FullName!)
        ];
    }

    protected sealed class BlockingTransportFactory : IClientTransportFactory
    {
        internal TaskCompletionSource<bool> ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled connect should not continue.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    protected sealed class CancellingEndpointEnumerable(
        CancellationTokenSource cancellation,
        SharpLinkEndpoint endpoint) : IEnumerable<SharpLinkEndpoint>
    {
        public IEnumerator<SharpLinkEndpoint> GetEnumerator()
        {
            cancellation.Cancel();
            yield return endpoint;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    protected sealed class ThrowingWriteLoggerFactory : ILoggerFactory
    {
        private static readonly ILogger Logger = new ThrowingWriteLogger();

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => Logger;

        public void Dispose()
        {
        }

        private sealed class ThrowingWriteLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => throw new InvalidOperationException("controlled logger write failure");
        }
    }

    protected sealed class ControlledMutationTransportFactory : IClientTransportFactory
    {
        private readonly TestClientTransportFactory _inner = new();
        private readonly TaskCompletionSource<bool> _connectRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Exception? _connectFailure;
        private int _connectCount;
        private int _disposeCount;

        internal ControlledMutationTransportFactory(
            bool blockConnect = false,
            Exception? connectFailure = null)
        {
            _connectFailure = connectFailure;
            if (!blockConnect)
                _connectRelease.TrySetResult(true);
        }

        internal TaskCompletionSource<bool> ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ConnectCount => Volatile.Read(ref _connectCount);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            ConnectStarted.TrySetResult(true);
            await _connectRelease.Task.WaitAsync(cancellationToken);
            if (_connectFailure is not null)
                throw _connectFailure;
            return await _inner.ConnectAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            await _inner.DisposeAsync();
        }

        internal void ReleaseConnect() => _connectRelease.TrySetResult(true);
    }

    protected sealed class BlockingRetiredClient :
        ISharpLinkClient,
        ISharpLinkClientDrainInspector,
        ISharpLinkClientTimeProvider
    {
        private readonly TaskCompletionSource _stop =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeCalls = 1;
        private int _registerAssemblyCallCount;
        private int _stopCount;

        internal BlockingRetiredClient(TimeProvider? timeProvider = null)
        {
            TimeProvider = timeProvider ?? global::System.TimeProvider.System;
        }

        internal TaskCompletionSource StopStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RegisterAssemblyCallCount => Volatile.Read(ref _registerAssemblyCallCount);
        internal int StopCount => Volatile.Read(ref _stopCount);

        public SharpLinkConnectionState State { get; private set; } = SharpLinkConnectionState.Ready;
        public TimeProvider TimeProvider { get; }
        int ISharpLinkClientDrainInspector.ActiveCallCount => Volatile.Read(ref _activeCalls);
        int ISharpLinkClientDrainInspector.ActiveStreamCount => 0;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCount);
            State = SharpLinkConnectionState.Draining;
            StopStarted.TrySetResult();
            return cancellationToken.CanBeCanceled
                ? new ValueTask(_stop.Task.WaitAsync(cancellationToken))
                : new ValueTask(_stop.Task);
        }

        public ValueTask DisposeAsync() => StopAsync();

        public TContract Get<TContract>() where TContract : IService
            => throw new NotSupportedException();

        public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService
            => throw new NotSupportedException();

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Draining));

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
        {
            Interlocked.Increment(ref _registerAssemblyCallCount);
            return SharpLinkAssemblyRegistrationResult.Success();
        }

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = true });

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(
                new SharpLinkAssemblyRegistrationError(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "not supported")));

        internal void ReleaseStop()
        {
            State = SharpLinkConnectionState.Stopped;
            _stop.TrySetResult();
        }

        internal void ReleaseCalls() => Volatile.Write(ref _activeCalls, 0);
    }

    protected sealed class FaultingRetiredClient : ISharpLinkClient, ISharpLinkClientDrainInspector
    {
        private readonly TaskCompletionSource _stopRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _stopOperation;

        internal TaskCompletionSource StopStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task StopOperation => _stopOperation ?? throw new InvalidOperationException("Stop has not started.");

        public SharpLinkConnectionState State { get; private set; } = SharpLinkConnectionState.Ready;
        int ISharpLinkClientDrainInspector.ActiveCallCount => 0;
        int ISharpLinkClientDrainInspector.ActiveStreamCount => 0;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            _stopOperation ??= StopCoreAsync();
            return cancellationToken.CanBeCanceled
                ? new ValueTask(_stopOperation.WaitAsync(cancellationToken))
                : new ValueTask(_stopOperation);
        }

        public ValueTask DisposeAsync() => StopAsync();

        public TContract Get<TContract>() where TContract : IService
            => throw new NotSupportedException();

        public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService
            => throw new NotSupportedException();

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Draining));

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => SharpLinkAssemblyRegistrationResult.Success();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkAssemblyUnregisterResult { ReferencesReleased = true });

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(
                new SharpLinkAssemblyRegistrationError(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "not supported")));

        internal void FailStop() => _stopRelease.TrySetResult();

        private async Task StopCoreAsync()
        {
            State = SharpLinkConnectionState.Draining;
            StopStarted.TrySetResult();
            await _stopRelease.Task;
            State = SharpLinkConnectionState.Faulted;
            throw new InvalidOperationException("retired cleanup failed");
        }
    }

    protected sealed class CoordinatedUnregisterClient : ISharpLinkClient, IDynamicAssemblyRegistrationInspector
    {
        private readonly TaskCompletionSource<SharpLinkAssemblyUnregisterResult> _unregister =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _unregisterCallCount;

        internal CoordinatedUnregisterClient(
            SharpLinkConnectionState state = SharpLinkConnectionState.Created)
            => State = state;

        internal int UnregisterCallCount => Volatile.Read(ref _unregisterCallCount);
        public SharpLinkConnectionState State { get; private set; }

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => SharpLinkAssemblyRegistrationResult.Success();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _unregisterCallCount);
            return new ValueTask<SharpLinkAssemblyUnregisterResult>(_unregister.Task);
        }

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SharpLinkAssemblyReplacementResult.Failure(
                new SharpLinkAssemblyRegistrationError(
                    SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState,
                    "not supported")));

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            State = SharpLinkConnectionState.Stopped;
            return ValueTask.CompletedTask;
        }

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SharpLinkHealthCheckResult(SharpLinkHealthStatus.Unhealthy));

        public TContract Get<TContract>() where TContract : IService
            => throw new NotSupportedException();

        public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => StopAsync();

        bool IDynamicAssemblyRegistrationInspector.IsDynamicAssemblyRegistered(Assembly assembly)
            => true;

        internal void RejectUnregister(Exception exception)
            => _unregister.TrySetException(exception);
    }

    protected sealed class OneShotEndpointEnumerable : IEnumerable<SharpLinkEndpoint>
    {
        private readonly SharpLinkEndpoint _endpoint;
        private int _enumerationCount;

        public OneShotEndpointEnumerable(SharpLinkEndpoint endpoint) => _endpoint = endpoint;

        public IEnumerator<SharpLinkEndpoint> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerationCount) != 1)
                throw new InvalidOperationException("Endpoint source must be enumerated only once.");

            yield return _endpoint;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
