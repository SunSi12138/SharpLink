using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Threading;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests.Builder;

public sealed partial class BuildPlanBuilderTests
{
    private const string ConsumedBuilderMessage = "This SharpLink builder has already been consumed.";
    private static RpcHash128 SyntheticManifestHash => new(0x6275696c642d706cUL, 0x616e2d6d616e6966UL);
    private static RpcHash128 SyntheticCodecHash => new(0x6275696c642d706cUL, 0x616e2d636f646563UL);

    private static void ConfigureTopology(SharpClientBuilder builder, ClientTopology topology)
    {
        switch (topology)
        {
            case ClientTopology.Fixed:
                builder.UseTransport(new TrackingClientTransport());
                return;
            case ClientTopology.Static:
                builder.UseEndpoints([Endpoint("static", 5101)], static _ => new TrackingClientTransport());
                return;
            case ClientTopology.Dynamic:
                builder.UseEndpointResolver(new TrackingResolver(), static _ => new TrackingClientTransport());
                return;
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    private static SharpClientBuilder CreateClientBuilder()
        => SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableRequestTimeout();

    private static SharpLinkServerBuilder CreateServerBuilder()
        => SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);

    private static SharpLinkEndpoint Endpoint(string id, int port)
        => new()
        {
            Id = id,
            Address = new SharpLinkTcpAddress("127.0.0.1", port)
        };

    private static T ReadPrivate<T>(object instance, string fieldName) where T : class
        => instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T
           ?? throw new Exception($"cannot find {fieldName}");

    private static void AssertSemanticManifestCompileFailure(
        ISharpLinkGeneratedAssemblyManifest manifest,
        string scenario)
    {
        var adapter = new DeferredAdapter();
        var factory = new DeferredAdapterCodecFactory(adapter);
        var transport = new ProfileTrackingClientTransport();
        var builder = CreateClientBuilder().UseTransport(transport);

        var failure = Capture(() => _ = builder.CompileForMultiCluster([
            new DeferredAdapterManifest(factory),
            manifest
        ]));

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains(nameof(SharpLinkAssemblyRegistrationErrorCode.InvalidManifest), StringComparison.Ordinal),
            $"{scenario} must fail during Client Compile with an invalid-manifest error");
        Ensure(adapter.ScopeCreateCount == 0 && factory.CodecCreateCount == 0,
            $"{scenario} must fail before a preceding valid manifest materializes adapter or Codec resources");
        Ensure(transport.ProfileBindingCount == 0,
            $"{scenario} must fail before Client materialization binds the transport profile");
        Ensure(transport.DisposeCount == 1,
            $"{scenario} must release the unmaterialized direct transport exactly once");
        EnsureConsumed(() => _ = builder.Build());
    }

    private static Exception Capture(Action action)
    {
        try
        {
            action();
            throw new Exception("expected operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void EnsureConsumed(Action action)
    {
        var failure = Capture(action);
        Ensure(failure is InvalidOperationException && failure.Message == ConsumedBuilderMessage,
            "the builder must have one stable terminal consumed error");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private enum ClientTopology : byte
    {
        Fixed,
        Static,
        Dynamic
    }

    private class TrackingClientTransport : IClientTransportFactory
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingClientTransport : TrackingClientTransport, IPerformanceProfileAwareTransport
    {
        private readonly ManualResetEventSlim _release = new();

        internal ManualResetEventSlim ProfileBindingEntered { get; } = new();

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            _ = profile;
            ProfileBindingEntered.Set();
            _release.Wait();
        }

        internal void ReleaseProfileBinding() => _release.Set();
    }

    private sealed class ProfileFailureClientTransport : TrackingClientTransport, IPerformanceProfileAwareTransport
    {
        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            _ = profile;
            throw new InvalidOperationException("phase11 Client profile failure");
        }
    }

    private sealed class ProfileTrackingClientTransport : TrackingClientTransport, IPerformanceProfileAwareTransport
    {
        private int _profileBindingCount;

        internal int ProfileBindingCount => Volatile.Read(ref _profileBindingCount);

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            _ = profile;
            Interlocked.Increment(ref _profileBindingCount);
        }
    }

    private sealed class TrackingResolver : ISharpLinkEndpointResolver
    {
        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<SharpLinkEndpointSnapshot>(new NotSupportedException());

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class TrackingServerListener : IServerTransportListener
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingServerListener : TrackingServerListener, IPerformanceProfileAwareTransport
    {
        private readonly ManualResetEventSlim _release = new();

        internal ManualResetEventSlim ProfileBindingEntered { get; } = new();

        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            _ = profile;
            ProfileBindingEntered.Set();
            _release.Wait();
        }

        internal void ReleaseProfileBinding() => _release.Set();
    }

    private sealed class ProfileFailureServerListener : TrackingServerListener, IPerformanceProfileAwareTransport
    {
        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
        {
            _ = profile;
            throw new InvalidOperationException("phase11 Server profile failure");
        }
    }

    private sealed class CountingEndpointEnumerable(IReadOnlyList<SharpLinkEndpoint> endpoints)
        : IEnumerable<SharpLinkEndpoint>
    {
        private int _enumerationCount;
        private int _moveNextCount;

        internal int EnumerationCount => Volatile.Read(ref _enumerationCount);
        internal int MoveNextCount => Volatile.Read(ref _moveNextCount);

        public IEnumerator<SharpLinkEndpoint> GetEnumerator()
        {
            if (Interlocked.Increment(ref _enumerationCount) != 1)
                throw new InvalidOperationException("endpoint source must not be enumerated twice");

            for (var index = 0; index < endpoints.Count; index++)
            {
                Interlocked.Increment(ref _moveNextCount);
                yield return endpoints[index];
            }
            Interlocked.Increment(ref _moveNextCount);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingEndpointEnumerable : IEnumerable<SharpLinkEndpoint>
    {
        private int _enumerationCount;
        private int _moveNextCount;

        internal int EnumerationCount => Volatile.Read(ref _enumerationCount);
        internal int MoveNextCount => Volatile.Read(ref _moveNextCount);

        public IEnumerator<SharpLinkEndpoint> GetEnumerator()
        {
            Interlocked.Increment(ref _enumerationCount);
            Interlocked.Increment(ref _moveNextCount);
            yield return Endpoint("first", 5301);
            Interlocked.Increment(ref _moveNextCount);
            throw new InvalidOperationException("endpoint enumeration failed");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingManifestList(IReadOnlyList<ISharpLinkGeneratedAssemblyManifest> manifests)
        : IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>
    {
        private int _accessCount;

        internal int AccessCount => Volatile.Read(ref _accessCount);
        internal bool RejectFurtherAccess { get; set; }

        public int Count
        {
            get
            {
                RecordAccess();
                return manifests.Count;
            }
        }

        public ISharpLinkGeneratedAssemblyManifest this[int index]
        {
            get
            {
                RecordAccess();
                return manifests[index];
            }
        }

        public IEnumerator<ISharpLinkGeneratedAssemblyManifest> GetEnumerator()
            => throw new InvalidOperationException("the build plan must snapshot manifests by indexed access");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private void RecordAccess()
        {
            if (RejectFurtherAccess)
                throw new InvalidOperationException("caller manifest list was accessed after Compile");
            Interlocked.Increment(ref _accessCount);
        }
    }

    private sealed class EmptyManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase11-test";
        public Assembly OwnerAssembly => typeof(BuildPlanBuilderTests).Assembly;
        public RpcHash128 RpcAssemblyHash => SyntheticManifestHash;
        public string CompileTimeDescriptor => "phase11-empty";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class IncompatibleManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api + 1;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase11-test";
        public Assembly OwnerAssembly => typeof(BuildPlanBuilderTests).Assembly;
        public RpcHash128 RpcAssemblyHash => SyntheticManifestHash;
        public string CompileTimeDescriptor => "phase11-incompatible";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class MalformedApi4Manifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase11-test";
        public Assembly OwnerAssembly => typeof(BuildPlanBuilderTests).Assembly;
        public RpcHash128 RpcAssemblyHash => SyntheticManifestHash;
        public string CompileTimeDescriptor => "phase11-malformed";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => null!;
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class ForeignContractOwnershipManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase11-test";
        public Assembly OwnerAssembly => typeof(BuildPlanBuilderTests).Assembly;
        public RpcHash128 RpcAssemblyHash => SyntheticManifestHash;
        public string CompileTimeDescriptor => "phase11-foreign-contract";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } =
        [
            new(
                typeof(string),
                typeof(string).FullName!,
                11_001,
                new string('a', 64),
                [],
                static (_, _) => null!,
                static _ => null!)
        ];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class DeferredAdapterManifest(DeferredAdapterCodecFactory factory) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "phase11-test";
        public Assembly OwnerAssembly => typeof(BuildPlanBuilderTests).Assembly;
        public RpcHash128 RpcAssemblyHash => SyntheticManifestHash;
        public string CompileTimeDescriptor => "phase11-deferred-adapter";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [factory];
        public IReadOnlyList<string> Dependencies => [];
    }

    private sealed class DeferredAdapterCodecFactory(DeferredAdapter adapter) : IRpcGeneratedCodecFactory
    {
        private int _codecCreateCount;

        internal int CodecCreateCount => Volatile.Read(ref _codecCreateCount);
        public Type TargetType => typeof(DeferredCodecValue);
        public RpcHash128 CodecHash => SyntheticCodecHash;
        public string? AdapterId => "phase11-deferred-adapter/v1";
        public IRpcCodecAdapter Adapter { get; } = adapter;

        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        {
            Interlocked.Increment(ref _codecCreateCount);
            return (adapterScope ?? throw new ArgumentNullException(nameof(adapterScope))).CreateCodec<DeferredCodecValue>();
        }

        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<DeferredCodecValue>;
    }

    private sealed class DeferredAdapter : IRpcCodecAdapter
    {
        private int _scopeCreateCount;

        internal int ScopeCreateCount => Volatile.Read(ref _scopeCreateCount);
        public string AdapterId => "phase11-deferred-adapter/v1";

        public IRpcCodecAdapterScope CreateScope()
        {
            Interlocked.Increment(ref _scopeCreateCount);
            return new DeferredAdapterScope();
        }
    }

    private sealed class DeferredAdapterScope : IRpcCodecAdapterScope
    {
        public IRpcCodec<T> CreateCodec<T>() => new DeferredCodec<T>();

        public void Dispose()
        {
        }
    }

    private sealed class DeferredCodecValue;

    private sealed class DeferredCodec<T> : IRpcCodec<T>
    {
        public void Serialize(in T value, IBufferWriter<byte> buffer)
        {
        }

        public T? Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }
}
