using System.Net;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using SharpLink.Client;
using SharpLink.RollbackPlugin;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Builder;

public partial class BuilderOwnershipRollbackTests
{
    private static RpcHash128 SyntheticManifestHash => new(0x6275696c6465722dUL, 0x726f6c6c6261636bUL);
    private static RpcHash128 SyntheticCodecHash => new(0x6275696c6465722dUL, 0x636f6465632d7631UL);

    private static SharpClientBuilder CreateClientBuilder()
        => SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableRequestTimeout();

    private static SharpLinkServerBuilder CreateServerBuilder(
        params ISharpLinkGeneratedAssemblyManifest[] manifests)
        => SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(manifests.Length == 0
                ? FixedGeneratedManifestSource.Empty
                : new FixedGeneratedManifestSource(manifests));

    private static SharpLinkMultiClusterClientBuilder CreateMultiClusterBuilder()
        => SharpLinkMultiClusterClientBuilder.Create()
            .DisableRequestTimeout()
            .UseGeneratedDiscoverySources(
                FixedGeneratedManifestSource.Empty,
                FixedGeneratedClusterRouteSource.Empty);

    private static Exception Capture(Action action)
    {
        try { action(); throw new Exception("expected build failure"); }
        catch (Exception exception) { return exception; }
    }

    private static bool Contains(Exception exception, string text)
    {
        if (exception.Message.Contains(text, StringComparison.Ordinal)) return true;
        if (exception is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) if (Contains(inner, text)) return true;
        return exception.InnerException is { } nested && Contains(nested, text);
    }

    private static SharpLinkEndpoint CreateEndpoint(string id, int port)
        => new()
        {
            Id = id,
            Address = new SharpLinkTcpAddress("127.0.0.1", port)
        };

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void EnsureSequence(IReadOnlyList<string> actual, params string[] expected)
    {
        Ensure(actual.Count == expected.Length,
            $"expected {expected.Length} cleanup events but saw {actual.Count}: {string.Join(", ", actual)}");
        for (var index = 0; index < expected.Length; index++)
        {
            Ensure(string.Equals(actual[index], expected[index], StringComparison.Ordinal),
                $"cleanup event {index} must be '{expected[index]}' but was '{actual[index]}'");
        }
    }

    private static void MarkReplacementFrameworkOwned(SharpLinkServerBuilder builder, Type contractType)
    {
        var definitionsField = typeof(SharpLinkServerBuilder).GetField(
            "_replacementServices",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new Exception("cannot find Server Builder replacement services");
        var definitions = (System.Collections.IDictionary)(definitionsField.GetValue(builder) ??
            throw new Exception("cannot read Server Builder replacement services"));
        var replacement = definitions[contractType] ??
            throw new Exception($"cannot find replacement for '{contractType.FullName}'");
        var replacementType = replacement.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var instance = replacementType.GetProperty("Instance", flags)?.GetValue(replacement);
        var factory = replacementType.GetProperty("Factory", flags)?.GetValue(replacement);
        var lifetime = replacementType.GetProperty("Lifetime", flags)?.GetValue(replacement);
        ConstructorInfo? constructor = null;
        foreach (var candidate in replacementType.GetConstructors(flags))
        {
            if (candidate.GetParameters().Length == 4)
            {
                constructor = candidate;
                break;
            }
        }
        if (constructor is null || lifetime is null)
            throw new Exception("cannot construct framework-owned Server Builder replacement");

        definitions[contractType] = constructor.Invoke([instance, factory, lifetime, false]);
    }

    private interface IMissingService : IService;

    private interface IRegistrationServiceOne : IService;

    private interface IRegistrationServiceTwo : IService;

    private interface IRegistrationBuildFailure : IService;

    private sealed class CodecValue;

    private sealed class TrackingClientTransport(
        string? bindingFailure,
        string? cleanupFailure,
        BuilderFaultInjectionProbe? probe = null,
        string? resource = null) :
        IClientTransportFactory,
        IPerformanceProfileAwareTransport
    {
        public int DisposeCount { get; private set; }

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            if (bindingFailure is not null)
                throw new InvalidOperationException(bindingFailure);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (probe is not null)
                probe.RecordCleanup(resource ?? throw new InvalidOperationException("Tracked resource name is required."));
            return cleanupFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(new InvalidOperationException(cleanupFailure));
        }
    }

    private sealed class NoopClientTransport : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingServerTransport(
        string? cleanupFailure = null,
        string? bindingFailure = null,
        List<string>? cleanupEvents = null,
        string? cleanupResource = null) : IServerTransportListener, IPerformanceProfileAwareTransport
    {
        public int DisposeCount { get; private set; }
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            if (bindingFailure is not null)
                throw new InvalidOperationException(bindingFailure);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            cleanupEvents?.Add(cleanupResource ?? "listener");
            return cleanupFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(new InvalidOperationException(cleanupFailure));
        }
    }

    private sealed class TrackingResolver(string cleanupFailure) : ISharpLinkEndpointResolver
    {
        public int DisposeCount { get; private set; }

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<SharpLinkEndpointSnapshot>(new NotSupportedException());

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.FromException(new InvalidOperationException(cleanupFailure));
        }
    }

    private sealed class ThrowingLoggerFactory(string failure) : ILoggerFactory
    {
        public int DisposeCount { get; private set; }
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => throw new InvalidOperationException(failure);
        public void Dispose() => DisposeCount++;
    }

    private sealed class MultiClusterThrowingLoggerFactory(string failure) : ILoggerFactory
    {
        public int DisposeCount { get; private set; }

        public void AddProvider(ILoggerProvider provider) { }

        public ILogger CreateLogger(string categoryName)
            => categoryName.Contains(nameof(SharpLinkMultiClusterClient), StringComparison.Ordinal)
                ? throw new InvalidOperationException(failure)
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() => DisposeCount++;
    }

    private sealed class TrackingServiceProvider : IServiceProvider, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public List<Type> RequestedServices { get; } = [];

        public object? GetService(Type serviceType)
        {
            RequestedServices.Add(serviceType);
            return null;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingCodec : IRpcCodec<CodecValue>, IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Serialize(in CodecValue value, IBufferWriter<byte> buffer) { }

        public CodecValue? Deserialize(in ReadOnlySequence<byte> buffer) => null;

        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingRuntimeContextManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(ThrowingRuntimeContextManifest).Assembly;
        public RpcHash128 RpcAssemblyHash => SyntheticManifestHash;
        public string CompileTimeDescriptor => "builder-runtime-context-throw";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [new ThrowingRuntimeContextCodecFactory()];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class ThrowingRuntimeContextCodecFactory : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(CodecValue);
        public RpcHash128 CodecHash => SyntheticCodecHash;
        public string? AdapterId => "builder-runtime-context-adapter/v1";
        public IRpcCodecAdapter Adapter { get; } = new ThrowingRuntimeContextAdapter();

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
            => new TrackingCodec();

        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<CodecValue>;
    }

    private sealed class ThrowingRuntimeContextAdapter : IRpcCodecAdapter
    {
        public string AdapterId => "builder-runtime-context-adapter/v1";

        public IRpcCodecAdapterScope CreateScope()
            => throw new InvalidOperationException("controlled Runtime Context construction failure");
    }

    private sealed class TrackingRegistrationServiceOne(List<string> cleanupEvents) : IRegistrationServiceOne, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            cleanupEvents.Add("registration:first");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingRegistrationServiceTwo(List<string> cleanupEvents) : IRegistrationServiceTwo, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            cleanupEvents.Add("registration:second");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RegistrationBuildFailureService : IRegistrationBuildFailure
    {
    }

    private sealed class RegistrationRollbackManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly => typeof(RegistrationRollbackManifest).Assembly;
        public RpcHash128 RpcAssemblyHash => SyntheticManifestHash;
        public string CompileTimeDescriptor => "builder-registration-rollback";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } =
        [
            CreateContract(typeof(IRegistrationServiceOne), 91_001),
            CreateContract(typeof(IRegistrationServiceTwo), 91_002),
            CreateContract(typeof(IRegistrationBuildFailure), 91_003)
        ];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];

        private static SharpLinkGeneratedContractDescriptor CreateContract(Type contractType, long contractId)
            => new(
                contractType,
                contractType.FullName!,
                contractId,
                new string('a', 64),
                [],
                static (_, _) => throw new NotSupportedException(),
                static _ => RegistrationStub.Instance);
    }

    private sealed class RegistrationStub : IRpcStub
    {
        public RpcMethodFacts ResolveMethodFacts(long methodHash) => RpcMethodFacts.UnknownMethod;

        internal static readonly RegistrationStub Instance = new();

        public long InterfaceHash => 91_000;

        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts methodHash,
            long requestId, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => ValueTask.CompletedTask;

        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}

// Closed fixture owner for only the two rollback Build controls and the Dynamic profile control.
// There is one possible asynchronous resource: an unexpectedly returned real Server.
internal sealed class RollbackBuildFixture
{
    private static readonly List<RollbackBuildFixture> Quarantined = [];
    private readonly List<Exception> _failures = [];
    private string? _oldSchema;
    private int _oldScopeCount;
    private bool _schemaChanged;
    private bool _countChanged;
    private bool _buildEntered;
    private bool _buildFailureAccepted;
    private Exception? _buildFailure;
    private ISharpLinkServer? _server;
    private Task? _serverDisposal;
    internal ISharpLinkServer? UnexpectedServer => _server;
    internal Task? UnexpectedServerDisposal => _serverDisposal;
    internal int ServerDisposalCalls { get; private set; }
    internal bool ServerDisposalOutcomeObserved { get; private set; }
    internal bool IsolationReleased { get; private set; }
    internal bool IsQuarantined { get; private set; }
    internal string? OriginalSchema => _oldSchema;
    internal int OriginalScopeCount => _oldScopeCount;
    internal Exception? LateDisposalFailure { get; private set; }
    private RollbackBuildTransport? _transport;
    private RollbackBuildLogger? _logger;
    internal RollbackManifest Manifest { get; private set; } = null!;

    internal void Initialize(string schema)
    {
        // Called inside the permit-owned try, including reads that can fail.
        _oldSchema = Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA");
        _oldScopeCount = RollbackState.ScopeDisposeCount;
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", schema);
        _schemaChanged = true;
        RollbackState.ScopeDisposeCount = 0;
        _countChanged = true;
        Manifest = new RollbackManifest();
    }

    internal RollbackBuildTransport CreateTransport(string? cleanupFailure = null, string? bindingFailure = null,
        Exception? independentCleanupFailure = null)
        => _transport = new RollbackBuildTransport(cleanupFailure, bindingFailure, independentCleanupFailure);
    internal RollbackBuildLogger CreateLogger(string failure)
        => _logger = new RollbackBuildLogger(failure);

    internal Exception CaptureBuild(SharpLinkServerBuilder builder)
    {
        // The selected builder is fresh, private and configured once. BeginBuild cannot be a
        // concurrent/repeated-build rejection. From this entry, its synchronous rollback owns
        // the listener; neither a throw nor a disposal counter authorizes a second disposal.
        _buildEntered = true;
        try
        {
            _server = builder.Build(); // Retain an unexpected successful result before asserting.
            return _buildFailure = new Exception("expected build failure");
        }
        catch (Exception failure) { return _buildFailure = failure; }
    }

    internal void AcceptBuildFailure() => _buildFailureAccepted = true;
    internal void RecordBodyFailure(Exception failure) => _failures.Add(failure);

    internal async Task FinishAsync(CancellationToken testCancellationToken)
    {
        if (!_buildFailureAccepted && _buildFailure is not null) _failures.Add(_buildFailure);
        var joined = true;
        if (_server is not null)
        {
            try
            {
                ServerDisposalCalls++;
                _serverDisposal = _server.DisposeAsync().AsTask();
                try { await _serverDisposal.WaitAsync(testCancellationToken); }
                catch (Exception waiterFailure) { _failures.Add(waiterFailure); }
                // Latch completion before consuming its full terminal graph. A waiter timeout
                // remains a failure even if the physical source completes immediately afterward.
                joined = _serverDisposal.IsCompleted;
                if (joined) RecordDisposalOutcome(_serverDisposal);
            }
            catch (Exception failure)
            {
                _failures.Add(failure);
                joined = false; // A throwing entry has not supplied a physical-join receipt.
            }
        }
        else if (!_buildEntered && _transport is not null)
        {
            // This selected fake completes synchronously; no worker is invented for its API.
            try { _transport.DisposeAsync().GetAwaiter().GetResult(); }
            catch (Exception failure) { _failures.Add(failure); }
        }

        if (!joined)
        {
            IsQuarantined = true;
            lock (Quarantined) Quarantined.Add(this);
            if (_serverDisposal is { } source)
                _ = source.ContinueWith(task =>
                {
                    if (task.Exception is { } failures) LateDisposalFailure = failures;
                    else if (task.IsCanceled)
                    {
                        try { task.GetAwaiter().GetResult(); }
                        catch (Exception failure) { LateDisposalFailure = failure; }
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _failures.Add(new Exception("unexpected Server cleanup is unjoined; fixture owners and isolation are retained"));
            return; // No late release, logger disposal or global restoration from the observer.
        }

        // The caller logger's original zero-disposal assertion precedes this fixture cleanup.
        try { _logger?.Dispose(); } catch (Exception failure) { _failures.Add(failure); }
        var restored = true;
        try
        {
            if (_schemaChanged) Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", _oldSchema);
        }
        catch (Exception failure) { _failures.Add(failure); restored = false; }
        try { if (_countChanged) RollbackState.ScopeDisposeCount = _oldScopeCount; }
        catch (Exception failure) { _failures.Add(failure); restored = false; }
        if (!restored)
        {
            IsQuarantined = true;
            lock (Quarantined) Quarantined.Add(this);
            _failures.Add(new Exception("global restoration failed; isolation remains retained"));
            return;
        }
        try { RollbackState.TestIsolation.Release(); IsolationReleased = true; }
        catch (Exception failure) { _failures.Add(failure); }
        GC.KeepAlive(Manifest);
    }

    private void RecordDisposalOutcome(Task source)
    {
        if (source.Exception is { } failures) _failures.Add(failures);
        else if (source.IsCanceled)
        {
            try { source.GetAwaiter().GetResult(); }
            catch (Exception failure) { _failures.Add(failure); }
        }
        ServerDisposalOutcomeObserved = true;
    }

    internal void ThrowIfFailed()
    {
        if (_failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failures[0]).Throw();
        if (_failures.Count != 0) throw new AggregateException(_failures);
    }
}

internal sealed class RollbackBuildTransport(string? cleanupFailure, string? bindingFailure,
    Exception? independentCleanupFailure = null)
    : IServerTransportListener, IPerformanceProfileAwareTransport
{
    internal Exception? CleanupFailure { get; } = cleanupFailure is null ? null : new InvalidOperationException(cleanupFailure);
    internal Exception? BindingFailure { get; } = bindingFailure is null ? null : new InvalidOperationException(bindingFailure);
    internal int DisposeCount { get; private set; }
    public EndPoint? LocalEndPoint => null;
    public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
    public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
    {
        if (BindingFailure is not null) throw BindingFailure;
    }
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        if (independentCleanupFailure is not null)
            return ValueTask.FromException(new AggregateException(
                CleanupFailure ?? throw new InvalidOperationException("An independent sibling requires the original cleanup marker."),
                independentCleanupFailure));
        return CleanupFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(CleanupFailure);
    }
}

internal sealed class RollbackBuildLogger(string failure) : ILoggerFactory
{
    internal Exception Failure { get; } = new InvalidOperationException(failure);
    internal int DisposeCount { get; private set; }
    public void AddProvider(ILoggerProvider provider) { }
    public ILogger CreateLogger(string categoryName) => throw Failure;
    public void Dispose() => DisposeCount++;
}
