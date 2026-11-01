using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

// Fresh controls on recovered 3eed. No historical source or runtime result is inherited.
public sealed class StaticFactoryColdOwnershipRecoveryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Test]
    public async Task StopWaitsForStaticFactoryUsingWarmedCodecAndItsRollback()
    {
        var manifest = new Manifest();
        var transport = new TestClientTransportFactory();
        var client = Build(manifest, transport);
        var entered = NewSignal();
        using var release = new ManualResetEventSlim();
        SharpLinkClient.ContractCallSite? site = null;
        manifest.Factory = (channel, codecs) =>
        {
            site = (SharpLinkClient.ContractCallSite)channel;
            var warmed = codecs.GetCodec<Payload>();
            warmed.Deserialize(ReadOnlySequence<byte>.Empty);
            entered.TrySetResult();
            if (!release.Wait(Bound)) throw new TimeoutException("factory barrier");
            warmed.Deserialize(ReadOnlySequence<byte>.Empty);
            return new Proxy();
        };
        var factory = Task.Run(() => Capture(() => client.CreateProxy<IContract>()));
        Exception? primary = null;
        try
        {
            await entered.Task.WaitAsync(Bound);
            var stop = client.StopAsync().AsTask();
            Ensure(client.State == SharpLinkConnectionState.Draining && !stop.IsCompleted,
                "actual Stop must wait for the admitted static factory");
            Ensure(manifest.Adapter.Scope.Disposals == 0 && manifest.Adapter.Scope.Uses == 1,
                "the warmed codec scope must remain live while factory is held");
            release.Set();
            var error = await factory.WaitAsync(Bound);
            Ensure(error is InvalidOperationException && error.Message.Contains("does not accept local contract construction", StringComparison.Ordinal),
                "the original factory must fail publication after Stop, not escape a retired handle");
            await stop.WaitAsync(Bound);
            Ensure(site is { IsDemandRetiredForDiagnostics: true }, "original unpublished demand must retire");
            Ensure(manifest.Adapter.Scope.Uses == 2 && manifest.Adapter.Scope.Disposals == 1,
                "both real codec uses precede one scope disposal");
            Ensure(transport.ConnectCount == 0, "cold factory must not connect");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.Set();
            await CleanupAsync(client, factory, primary);
        }
    }

    [Test]
    public async Task CallbackRejectsSameDomainManagementButAllowsCodecResolution()
    {
        var manifest = new Manifest();
        var client = Build(manifest, new TestClientTransportFactory());
        Exception? primary = null;
        try
        {
            manifest.Factory = (_, codecs) =>
            {
                RequireReentry(Capture(() => client.CreateProxy<IContract>()));
                RequireReentry(Capture(() => client.CreateContractChannel(client.GetLocalContract<IContract>())));
                RequireReentry(Capture(() => client.StopAsync()));
                RequireReentry(Capture(() => client.RegisterAssembly(typeof(IContract).Assembly)));
                codecs.GetCodec<Payload>().Deserialize(ReadOnlySequence<byte>.Empty);
                return new Proxy();
            };
            client.CreateProxy<IContract>();
            Ensure(client.State == SharpLinkConnectionState.Created && manifest.Adapter.Scope.Uses == 1,
                "rejected management must not start Stop or forbid normal codec resolution");
        }
        catch (Exception error) { primary = error; throw; }
        finally { await CleanupAsync(client, Task.CompletedTask, primary); }
    }

    [Test]
    public async Task FactoryAndDemandRollbackErrorsBothSurviveAndAdmissionIsReleased()
    {
        var manifest = new Manifest();
        var client = Build(manifest, new TestClientTransportFactory());
        var original = new IOException("factory-original");
        var rollback = new IOException("rollback-original");
        CancellationTokenRegistration cancellation = default;
        SharpLinkClient.ContractCallSite? site = null;
        Exception? primary = null;
        try
        {
            manifest.Factory = (channel, _) =>
            {
                site = (SharpLinkClient.ContractCallSite)channel;
                cancellation = site.DemandRetirement.Register(() => throw rollback);
                throw original;
            };
            var error = Capture(() => client.CreateProxy<IContract>());
            Ensure(error is AggregateException { InnerExceptions.Count: 2 } combined &&
                ReferenceEquals(combined.InnerExceptions[0], original) &&
                combined.InnerExceptions[1] is AggregateException { InnerExceptions.Count: 1 } cancelled &&
                ReferenceEquals(cancelled.InnerExceptions[0], rollback),
                "first factory failure and exact cancellation cleanup failure must remain independently owned");
            Ensure(site is { IsDemandRetiredForDiagnostics: true }, "rollback must retire the failed demand");
            cancellation.Dispose();
            await client.StopAsync().AsTask().WaitAsync(Bound);
            Ensure(manifest.Adapter.Scope.Disposals == 1, "throwing rollback must not strand preparation admission");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            cancellation.Dispose();
            await CleanupAsync(client, Task.CompletedTask, primary);
        }
    }

    [Test]
    public async Task ChannelPublicationCallbackIsJoinedAndKeepsItsExactFailure()
    {
        var manifest = new Manifest();
        var client = Build(manifest, new TestClientTransportFactory());
        var entered = NewSignal();
        using var release = new ManualResetEventSlim();
        var original = new IOException("channel-publication");
        var transaction = (ISharpLinkContractFactoryTransaction)client;
        var local = client.GetLocalContract<IContract>();
        var construction = Task.Run(() => Capture(() => transaction.CreateContractChannelTransactional(local, null, () =>
        {
            entered.TrySetResult();
            if (!release.Wait(Bound)) throw new TimeoutException("publication barrier");
            throw original;
        })));
        Exception? primary = null;
        try
        {
            await entered.Task.WaitAsync(Bound);
            var stop = client.StopAsync().AsTask();
            Ensure(!stop.IsCompleted && manifest.Adapter.Scope.Disposals == 0,
                "channel publication callback must hold the same cold shutdown admission");
            release.Set();
            Ensure(ReferenceEquals(await construction.WaitAsync(Bound), original), "publication error must not be replaced");
            await stop.WaitAsync(Bound);
            Ensure(manifest.Adapter.Scope.Disposals == 1, "scope is disposed after publication rollback");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            release.Set();
            await CleanupAsync(client, construction, primary);
        }
    }

    private static SharpLinkClient Build(Manifest manifest, TestClientTransportFactory transport)
        => (SharpLinkClient)SharpClientBuilder.Create().DisableRequestTimeout()
            .UseGeneratedManifestSource(new FixedGeneratedManifestSource([manifest])).UseTransport(transport).Build();

    private static async Task CleanupAsync(SharpLinkClient client, Task worker, Exception? primary)
    {
        List<Exception>? errors = null;
        try { await worker.WaitAsync(Bound); } catch (Exception error) { (errors ??= []).Add(error); }
        try { await client.StopAsync().AsTask().WaitAsync(Bound); } catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is null) return;
        if (primary is not null) errors.Insert(0, primary);
        throw new AggregateException(errors);
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static void Ensure(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void RequireReentry(Exception? error)
        => Ensure(error is ColdMetadataException { Code: SharpLinkAssemblyRegistrationErrorCode.InvalidObjectState } &&
            error.Message == "A metadata callback cannot reenter its owning admission or shutdown domain.",
            "same-domain callback reentry must fail synchronously before management starts");

    private interface IContract : IService;
    private sealed class Proxy : IContract;
    private sealed class Payload;
    private sealed class Manifest : ISharpLinkGeneratedAssemblyManifest
    {
        internal readonly Adapter Adapter = new();
        internal Func<IRpcContractChannel, IRpcCodecProvider, object> Factory = static (_, _) => new Proxy();
        internal Manifest()
        {
            Contracts = [new(typeof(IContract), typeof(IContract).FullName!, 8_775_512,
                new string('1', 64), [], (channel, provider) => Factory(channel, provider),
                static _ => throw new NotSupportedException())];
            ContractCodecs = [new CodecFactory(Adapter)];
        }
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "fresh-static-cold-ownership";
        public Assembly OwnerAssembly => typeof(Manifest).Assembly;
        public RpcHash128 RpcAssemblyHash => new(0x775512, 0xC01D);
        public string CompileTimeDescriptor => "fresh-static-cold-ownership";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs { get; }
        public IReadOnlyList<string> Dependencies => [];
    }
    private sealed class CodecFactory(Adapter adapter) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(Payload);
        public RpcHash128 CodecHash => new(0x775512, 1);
        public string AdapterId => adapter.AdapterId;
        public IRpcCodecAdapter Adapter => adapter;
        public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? scope)
            => (scope ?? throw new InvalidOperationException("missing scope")).CreateCodec<Payload>();
        public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<Payload>;
    }
    private sealed class Adapter : IRpcCodecAdapter
    {
        internal readonly Scope Scope = new();
        public string AdapterId => "test.static-cold-ownership/v1";
        public IRpcCodecAdapterScope CreateScope() => Scope;
    }
    private sealed class Scope : IRpcCodecAdapterScope
    {
        internal int Disposals;
        internal int Uses;
        public IRpcCodec<T> CreateCodec<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() => new Codec<T>(this);
        public void Dispose() => Interlocked.Increment(ref Disposals);
        internal void Use()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref Disposals) != 0, this);
            Interlocked.Increment(ref Uses);
        }
    }
    private sealed class Codec<T>(Scope scope) : IRpcCodec<T>
    {
        public void Serialize(in T value, IBufferWriter<byte> buffer) => scope.Use();
        public T? Deserialize(in ReadOnlySequence<byte> buffer) { scope.Use(); return default; }
    }
}
