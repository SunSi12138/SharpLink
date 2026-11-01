using System.Buffers.Binary;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

// Fresh source controls on the actual dynamic registration, client, Pending table and manager.
// The peer is a controlled in-process transport, not a full Server/TCP or ALC collection proof.
public sealed class InterceptedResponseLifetimeRecoveryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const long ContractId = 8_775_701;
    private static readonly RpcHash128 AssemblyHash = new(0x775701, 0xC01D);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task DeadlineReturnsButSameResponseLeaseProtectsHeldManagerDecode(bool duplex)
        => WithFixture(new Interceptor(), async fixture =>
        {
            var release = new ManualResetEventSlim(); var codec = new HeldCodec(release);
            fixture.ReleaseDecode = release;
            var enumerator = fixture.Enumerate(duplex, timed: true, codec);
            var move = fixture.Own(enumerator.MoveNextAsync().AsTask());
            var request = await fixture.RequestAsync(duplex);
            _ = fixture.Own(fixture.InjectChunkAsync(request.Header.RequestId));
            await codec.Entered.Task.WaitAsync(Bound);
            await Until(() => fixture.Module.RemainingStreams == 1, "only the original response lease remains");
            Ensure(fixture.Connection.Session.StreamManager.ActiveStreamCount == 1,
                "the real response route is manager-bound before codec Deserialize");
            fixture.Clock.Advance(Deadline);
            var logical = await Failure(move);
            Ensure(logical is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "logical deadline remains prompt");
            var pendingDeadline = Read<Exception>(fixture.Interceptor.Core!, "_error");
            Ensure(pendingDeadline is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
                "actual Pending deadline publishes its own original response error");
            var unregister = fixture.Unregister();
            Ensure(!unregister.IsCompleted && fixture.Module.RemainingStreams == 1 && codec.Exits == 0,
                "logical completion cannot refund the same response lease while actual Decode remains held");
            Ensure(!IsDisposed(fixture.Interceptor.Token), "existing intercepted CTS remains physically owned");
            release.Set(); await codec.Exited.Task.WaitAsync(Bound);
            Ensure((await unregister.WaitAsync(Bound)).ReferencesReleased, "module releases only after original decoder exits");
            Ensure(fixture.Module.RemainingCalls == 0 && IsDisposed(fixture.Interceptor.Token), "last physical owner releases lease and original CTS once");
            // This source outcome was never consumed logically. It is an independently owned
            // physical error, not an allowed aggregate that can conceal extra cleanup failures.
            var stopped = await Failure(fixture.Client.StopAsync().AsTask());
            Ensure(ReferenceEquals(stopped, pendingDeadline), "Stop retains exactly the original unconsumed Pending deadline");
            fixture.ExpectedStopFailure = pendingDeadline;
        });

    [Test]
    public Task LateSuccessfulInterceptorResultIsDisposedBeforeModuleRetires()
    {
        var stream = new ControlledStream { HoldDispose = true };
        var interceptor = new Interceptor { ShortCircuit = stream, HoldBeforeResult = true };
        return WithFixture(interceptor, async fixture =>
        {
            var enumerator = fixture.Enumerate(false, timed: true, Int32Codec.Instance);
            var move = fixture.Own(enumerator.MoveNextAsync().AsTask());
            await interceptor.Entered.Task.WaitAsync(Bound);
            fixture.Clock.Advance(Deadline);
            Ensure(await Failure(move) is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "late invocation still returns logical deadline");
            var unregister = fixture.Unregister(); Ensure(!unregister.IsCompleted, "pending successful result still owns the same module lease");
            interceptor.ReleaseResult.TrySetResult(); await stream.DisposeEntered.Task.WaitAsync(Bound);
            Ensure(stream.MoveCalls == 0 && stream.DisposeCalls == 1 && !unregister.IsCompleted,
                "late successful enumerable is physically acquired and disposed, not merely Task-observed and discarded");
            stream.ReleaseDispose.TrySetResult();
            Ensure((await unregister.WaitAsync(Bound)).ReferencesReleased && IsDisposed(interceptor.Token), "late-result cleanup completes before refund");
        });
    }

    [Test]
    public Task SuccessfulNonforwardingReplacementStillJoinsOriginalCoreWithoutDeadline()
    {
        var replacement = new ControlledStream();
        var interceptor = new Interceptor { Replacement = replacement, HoldAfterNext = true };
        return WithFixture(interceptor, async fixture =>
        {
            var release = new ManualResetEventSlim(); var codec = new HeldCodec(release); fixture.ReleaseDecode = release;
            var enumerator = fixture.Enumerate(false, timed: false, codec);
            var move = fixture.Own(enumerator.MoveNextAsync().AsTask());
            await interceptor.AfterNext.Task.WaitAsync(Bound);
            var request = await fixture.RequestAsync(false); _ = fixture.Own(fixture.InjectChunkAsync(request.Header.RequestId));
            await codec.Entered.Task.WaitAsync(Bound); interceptor.ReleaseNext.TrySetResult();
            await replacement.DisposeEntered.Task.WaitAsync(Bound);
            var unregister = fixture.Unregister();
            Ensure(!move.IsCompleted && !unregister.IsCompleted && fixture.Module.RemainingStreams == 1,
                "ordinary successful replacement cleanup joins the original sink inline, even without abandoned deadline stage");
            release.Set(); Ensure(!await move.WaitAsync(Bound), "replacement remains an empty successful stream");
            Ensure((await unregister.WaitAsync(Bound)).ReferencesReleased && codec.Exits == 1,
                "original codec dispatch and original route are reconciled before module release");
            Ensure(fixture.Connection.Session.StreamManager.ActiveStreamCount == 0 && fixture.Connection.PendingCalls.ActiveCount == 0,
                "nonforwarding result cannot strand original stream or Pending registration");
        });
    }

    [Test]
    public Task SynchronousPostNextThrowPreservesPrimaryAndReconcilesOriginalCore()
    {
        var primary = new IOException("synchronous post-next marker");
        return WithFixture(new Interceptor { ThrowSynchronouslyAfterNext = primary }, async fixture =>
        {
            var enumerator = fixture.Enumerate(false, timed: false, Int32Codec.Instance);
            var move = fixture.Own(enumerator.MoveNextAsync().AsTask());
            Ensure(ReferenceEquals(await Failure(move), primary), "synchronous interceptor primary is not replaced");
            Ensure(fixture.Interceptor.Core is not null, "actual terminal returned an original pooled sink before the synchronous throw");
            Ensure(fixture.Module.RemainingCalls == 0 && fixture.Connection.Session.StreamManager.ActiveStreamCount == 0 && fixture.Connection.PendingCalls.ActiveCount == 0,
                "every-exit core reconciliation includes synchronous interceptor construction failure");
            Ensure((await fixture.Unregister().WaitAsync(Bound)).ReferencesReleased, "no leaked module owner after synchronous failure");
        });
    }

    [Test]
    public Task ReplacementGetEnumeratorThrowPreservesPrimaryAndReconcilesOriginalCore()
    {
        var primary = new IOException("replacement enumerator marker");
        return WithFixture(new Interceptor { Replacement = new ControlledStream { GetEnumeratorFailure = primary } }, async fixture =>
        {
            var enumerator = fixture.Enumerate(false, timed: false, Int32Codec.Instance);
            var move = fixture.Own(enumerator.MoveNextAsync().AsTask());
            Ensure(ReferenceEquals(await Failure(move), primary), "replacement GetAsyncEnumerator failure stays caller-owned exactly once");
            Ensure(fixture.Interceptor.Core is not null && fixture.Module.RemainingCalls == 0 && fixture.Connection.Session.StreamManager.ActiveStreamCount == 0,
                "failed replacement acquisition cannot bypass original core cleanup");
            Ensure((await fixture.Unregister().WaitAsync(Bound)).ReferencesReleased, "failed enumerator leaves no response lease");
        });
    }

    [Test]
    public Task ArbitraryMoveNextExitsBeforeDisposeAndRawCancellationBelongsToPhysicalStop()
    {
        var raw = new OperationCanceledException("opaque unconsumed MoveNext marker");
        var stream = new ControlledStream { HoldMove = true };
        return WithFixture(new Interceptor { ShortCircuit = stream }, async fixture =>
        {
            var enumerator = fixture.Enumerate(false, timed: true, Int32Codec.Instance);
            var move = fixture.Own(enumerator.MoveNextAsync().AsTask());
            await stream.MoveEntered.Task.WaitAsync(Bound); fixture.Clock.Advance(Deadline);
            Ensure(await Failure(move) is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "logical deadline does not wait on arbitrary opaque MoveNext");
            var unregister = fixture.Unregister();
            Ensure(!unregister.IsCompleted && stream.DisposeCalls == 0 && !IsDisposed(fixture.Interceptor.Token),
                "no concurrent arbitrary Dispose and no early original CTS/module release");
            stream.ReleaseMove.TrySetException(raw); await stream.DisposeEntered.Task.WaitAsync(Bound);
            Ensure(!stream.DisposeOverlappedMove && stream.DisposeCalls == 1, "cleanup serialized actual MoveNext exit then Dispose");
            Ensure((await unregister.WaitAsync(Bound)).ReferencesReleased, "opaque physical tail eventually refunds same lease");
            Ensure(ReferenceEquals(await Failure(fixture.Client.StopAsync().AsTask()), raw),
                "Stop owns precisely the original unconsumed OCE, never a synthesized cancellation or containing aggregate");
            fixture.ExpectedStopFailure = raw;
        });
    }

    private static async Task WithFixture(Interceptor interceptor, Func<Fixture, Task> body)
    {
        var fixture = new Fixture(interceptor); Exception? primary = null;
        try { await fixture.InitializeAsync(); await body(fixture); }
        catch (Exception error) { primary = error; }
        await fixture.CleanupAsync(primary);
    }

    private sealed class Fixture(Interceptor interceptor)
    {
        internal readonly ManualTimeProvider Clock = new();
        internal readonly Interceptor Interceptor = interceptor;
        private readonly List<Task> _workers = [];
        private OwnerLoadContext? _loadContext;
        private IAsyncEnumerator<int>? _enumerator;
        internal TestClientTransportFactory Transport = null!;
        internal SharpLinkClient Client = null!;
        internal SharpLinkDynamicModule Module = null!;
        internal ClientConnection Connection = null!;
        internal Assembly Assembly = null!;
        internal IRpcContractChannel Channel = null!;
        internal ManualResetEventSlim? ReleaseDecode;
        internal Exception? ExpectedStopFailure;
        private Task<SharpLinkAssemblyUnregisterResult>? _unregister;
        internal async Task InitializeAsync()
        {
            _loadContext = new OwnerLoadContext(); Assembly = CreateOwnerAssembly(_loadContext);
            Transport = new TestClientTransportFactory(contractManifest: [new(ContractId, AssemblyHash)]);
            Client = (SharpLinkClient)SharpClientBuilder.Create().DisableRequestTimeout()
                .UseGeneratedManifestSource(new FixedGeneratedManifestSource([]))
                .UseTimeProvider(Clock).UseTransport(Transport).AddInterceptor(Interceptor).Build();
            var registered = Client.RegisterAssembly(Assembly);
            Ensure(registered.Succeeded, $"actual current manifest registration failed: {registered.Error}");
            var registry = Read<ClientAssemblyRegistry>(Client, "_assemblyRegistry");
            Module = registry.DynamicModules[Assembly];
            Channel = Client.CreateContractChannel(Client.GetLocalContract(Assembly, ContractId));
            await Client.ConnectAsync().AsTask().WaitAsync(Bound);
            var ready = Read<ClientConnection[]>(Client, "_readyConnections"); Ensure(ready.Length == 1, "one genuine connected Session"); Connection = ready[0];
            var prepare = Own(Channel.PrepareAsync(Bound).AsTask());
            var bind = await Transport.Connection.WaitForSentFrame(ProtocolV2FrameType.BindContract).WaitAsync(Bound);
            var requested = Protocol3BindingControlCodec.ReadBindContract(new ReadOnlySequence<byte>(bind.Payload));
            Ensure(requested.ContractId == ContractId && requested.Hash == AssemblyHash && requested.Requirement == Protocol3BindingRequirement.Compatible,
                "peer binds only this exact current fixture contract identity");
            var authority = Transport.Connection.Authority;
            using var bytes = new PooledByteBufferWriter();
            Protocol3BindingControlCodec.WriteBindResult(bytes, new Protocol3BindResult(Protocol3BindStatus.Success,
                Protocol3BindingToken.Create(0, 1, authority.Limits.Slots), ContractId, AssemblyHash, 1, 1,
                authority.ServerInstanceId, authority.RegistryEpoch), authority.Limits.Slots);
            await Transport.Connection.InjectFrameAsync(ProtocolV2FrameType.BindResult, ProtocolV2FrameFlags.None, bind.Header.RequestId, bytes.WrittenMemory);
            await prepare.WaitAsync(Bound);
            Ensure(Transport.ConnectCount == 1, "actual Connect/handshake and exact Bind reached ready state");
        }
        internal IAsyncEnumerator<int> Enumerate(bool duplex, bool timed, IRpcCodec<int> codec)
        {
            var method = new RpcMethodDescriptor(ContractId, duplex ? 2 : 1,
                duplex ? RpcMethodKind.DuplexStreaming : RpcMethodKind.ServerStreaming,
                true, duplex, timed, timed ? Deadline : null, ClientStreamCount: duplex ? 1 : 0);
            var request = default(RpcEmptyRequest); var writer = default(EmptyWriter);
            var stream = duplex
                ? Channel.InvokeDuplexStreamingAsync(method, request, RpcEmptyRequestCodec.Instance, codec, writer, null)
                : Channel.InvokeServerStreamingAsync(method, request, RpcEmptyRequestCodec.Instance, codec, null);
            return _enumerator = stream.GetAsyncEnumerator();
        }
        internal async Task<TestSentFrame> RequestAsync(bool duplex)
        {
            var frame = await Transport.Connection.WaitForSentFrame(ProtocolV2FrameType.Request).WaitAsync(Bound);
            Ensure(Connection.PendingCalls.Contains(unchecked((long)frame.Header.RequestId)), "request owns an actual full Pending ID");
            if (duplex)
                await Transport.Connection.InjectPacketAsync(ProtocolV2FrameType.RequestAccepted, ProtocolV2FrameFlags.None, unchecked((long)frame.Header.RequestId));
            return frame;
        }
        internal Task InjectChunkAsync(ulong requestId)
        {
            var bytes = new byte[6]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), 17);
            return Transport.Connection.InjectFrameAsync(ProtocolV2FrameType.StreamData, ProtocolV2FrameFlags.None, requestId, bytes);
        }
        internal Task<SharpLinkAssemblyUnregisterResult> Unregister()
            => _unregister ??= Own(Client.UnregisterAssemblyAsync(Assembly, TimeSpan.FromMinutes(1)).AsTask());
        internal T Own<T>(T task) where T : Task { _workers.Add(task); return task; }
        internal async Task CleanupAsync(Exception? primary)
        {
            List<Exception> failures = []; if (primary is not null) failures.Add(primary);
            // Release every test barrier before independently attempting each physical owner.
            try { ReleaseDecode?.Set(); } catch (Exception error) { failures.Add(error); }
            Interceptor.ReleaseAll();
            Task? stop = null;
            if (Client is not null)
            {
                try { stop = Client.StopAsync().AsTask(); }
                catch (Exception error) { failures.Add(error); }
            }
            // Stop closes pending calls before module retirement. Join active caller operations
            // before invoking the async iterator's Dispose (which is not concurrency-safe).
            foreach (var worker in _workers)
                if (!ReferenceEquals(worker, _unregister)) await JoinWorker(worker);
            if (_enumerator is not null)
                await Attempt(async () => await _enumerator.DisposeAsync().AsTask().WaitAsync(Bound));
            if (stop is not null) await Attempt(async () => await ObserveStop(stop));
            if (Transport is not null) await Attempt(async () => await Transport.DisposeAsync().AsTask().WaitAsync(Bound));
            foreach (var worker in _workers) await JoinWorker(worker);
            if (stop is { IsCompleted: false }) await Attempt(async () => await ObserveStop(stop));
            try { ReleaseDecode?.Dispose(); } catch (Exception error) { failures.Add(error); }
            try { _loadContext?.Unload(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
            return;
            async Task Attempt(Func<Task> operation) { try { await operation(); } catch (Exception error) { failures.Add(error); } }
            async Task JoinWorker(Task worker)
            {
                await worker.WaitAsync(Bound).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                if (!worker.IsCompleted) failures.Add(new TimeoutException("owned fixture worker did not physically exit"));
            }
            async Task ObserveStop(Task operation)
            {
                try { await operation.WaitAsync(Bound); }
                catch (Exception error) when (ReferenceEquals(error, ExpectedStopFailure)) { }
            }
        }
    }

    private sealed class Interceptor : ISharpLinkClientInterceptor
    {
        internal ControlledStream? ShortCircuit;
        internal ControlledStream? Replacement;
        internal bool HoldBeforeResult;
        internal bool HoldAfterNext;
        internal Exception? ThrowSynchronouslyAfterNext;
        internal CancellationToken Token;
        internal PooledAsyncStreamDispatcher<int>? Core;
        internal readonly TaskCompletionSource Entered = Signal();
        internal readonly TaskCompletionSource AfterNext = Signal();
        internal readonly TaskCompletionSource ReleaseResult = Signal();
        internal readonly TaskCompletionSource ReleaseNext = Signal();
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        {
            Token = context.CancellationToken; Entered.TrySetResult();
            if (ShortCircuit is not null) return ShortCircuitAsync();
            var result = next(context);
            if (ThrowSynchronouslyAfterNext is { } primary)
            {
                Ensure(result.IsCompletedSuccessfully, "fixture terminal must synchronously publish the response sink");
                Core = (PooledAsyncStreamDispatcher<int>)result.Result.Value!; AfterNext.TrySetResult();
                ExceptionDispatchInfo.Capture(primary).Throw();
            }
            return ForwardAsync(result);
        }
        private async ValueTask<SharpLinkClientInvocationResult> ShortCircuitAsync()
        {
            if (HoldBeforeResult) await ReleaseResult.Task.ConfigureAwait(false);
            return new SharpLinkClientInvocationResult(ShortCircuit!);
        }
        private async ValueTask<SharpLinkClientInvocationResult> ForwardAsync(ValueTask<SharpLinkClientInvocationResult> next)
        {
            var actual = await next.ConfigureAwait(false); Core = (PooledAsyncStreamDispatcher<int>)actual.Value!; AfterNext.TrySetResult();
            if (HoldAfterNext) await ReleaseNext.Task.ConfigureAwait(false);
            return Replacement is null ? actual : new SharpLinkClientInvocationResult(Replacement);
        }
        internal void ReleaseAll()
        {
            ReleaseResult.TrySetResult(); ReleaseNext.TrySetResult();
            ShortCircuit?.ReleaseAll(); Replacement?.ReleaseAll();
        }
    }

    private sealed class ControlledStream : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal bool HoldMove;
        internal bool HoldDispose;
        internal Exception? GetEnumeratorFailure;
        internal int MoveCalls, DisposeCalls;
        internal bool DisposeOverlappedMove;
        private bool _moving;
        internal readonly TaskCompletionSource MoveEntered = Signal();
        internal readonly TaskCompletionSource DisposeEntered = Signal();
        internal readonly TaskCompletionSource<bool> ReleaseMove = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseDispose = Signal();
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (GetEnumeratorFailure is { } error) ExceptionDispatchInfo.Capture(error).Throw();
            return this;
        }
        public async ValueTask<bool> MoveNextAsync()
        {
            MoveCalls++; _moving = true; MoveEntered.TrySetResult();
            try { return HoldMove && await ReleaseMove.Task.ConfigureAwait(false); }
            finally { _moving = false; }
        }
        public async ValueTask DisposeAsync()
        {
            DisposeCalls++; DisposeOverlappedMove |= _moving; DisposeEntered.TrySetResult();
            Ensure(!_moving, "arbitrary enumerator Dispose must not overlap MoveNext");
            if (HoldDispose) await ReleaseDispose.Task.ConfigureAwait(false);
        }
        internal void ReleaseAll() { ReleaseMove.TrySetResult(false); ReleaseDispose.TrySetResult(); }
    }
    private sealed class HeldCodec(ManualResetEventSlim release) : IRpcCodec<int>
    {
        internal readonly TaskCompletionSource Entered = Signal();
        internal readonly TaskCompletionSource Exited = Signal();
        internal int Exits;
        public void Serialize(in int value, IBufferWriter<byte> writer) => Int32Codec.Instance.Serialize(value, writer);
        public int Deserialize(in ReadOnlySequence<byte> bytes)
        {
            Entered.TrySetResult();
            try { if (!release.Wait(Bound)) throw new TimeoutException("codec release"); return Int32Codec.Instance.Deserialize(bytes); }
            finally { Interlocked.Increment(ref Exits); Exited.TrySetResult(); }
        }
    }
    private readonly struct EmptyWriter : IRpcClientStreamWriter
    {
        public ValueTask WriteAsync(IRpcClientStreamSink sink, long requestId, CancellationToken cancellationToken)
            => new(sink.SendClientStreamAsync(requestId, 1, Empty(), Int32Codec.Instance, cancellationToken));
        private static async IAsyncEnumerable<int> Empty() { await Task.CompletedTask; yield break; }
    }

    private static Assembly CreateOwnerAssembly(OwnerLoadContext context)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName($"SharpLink.ResponseLifetime.{Guid.NewGuid():N}"), typeof(object).Assembly);
        var module = builder.DefineDynamicModule("CurrentOwner");
        var contract = module.DefineType("OwnedResponseContract", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        contract.AddInterfaceImplementation(typeof(IService)); contract.CreateType();
        var manifest = module.DefineType("OwnedResponseManifest", TypeAttributes.Public | TypeAttributes.Sealed, typeof(ManifestBase));
        var constructor = manifest.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var il = constructor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(ManifestBase).GetConstructor(Type.EmptyTypes)!); il.Emit(OpCodes.Ret);
        var type = manifest.CreateType();
        builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(SharpLinkGeneratedAssemblyManifestAttribute).GetConstructor(
            [typeof(Type), typeof(int), typeof(int), typeof(string), typeof(string)])!,
            [type, SharpLinkGeneratedManifestVersions.Api, SharpLinkGeneratedManifestVersions.Protocol, "fresh-response-control", SharpLinkGeneratedManifestVersions.AbiIdentity]));
        using var bytes = new MemoryStream(); builder.Save(bytes); bytes.Position = 0; return context.LoadFromStream(bytes);
    }
    private sealed class OwnerLoadContext() : AssemblyLoadContext($"ResponseLifetime.{Guid.NewGuid():N}", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
            => Default.Assemblies.FirstOrDefault(candidate => AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), name));
    }
    // Public only for the explicitly emitted current owner; no process-global locator/resolver.
    public class ManifestBase : ISharpLinkGeneratedAssemblyManifest
    {
        public ManifestBase()
        {
            var type = GetType().Assembly.GetType("OwnedResponseContract", throwOnError: true)!;
            Contracts = [new(type, type.FullName!, ContractId, new string('a', 64),
                [new("Server", 1, RpcMethodKind.ServerStreaming, true, "empty", "stream:int32", new string('b', 64)),
                 new("Duplex", 2, RpcMethodKind.DuplexStreaming, true, "stream:int32", "stream:int32", new string('c', 64))],
                static (_, _) => throw new NotSupportedException("This fixture uses the actual contract channel, not a fake proxy."),
                static _ => throw new NotSupportedException("Controlled peer has no local service stub."))];
        }
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "fresh-response-control";
        public Assembly OwnerAssembly => GetType().Assembly;
        public RpcHash128 RpcAssemblyHash => AssemblyHash;
        public string CompileTimeDescriptor => "fresh-response-control-server1-duplex2";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; } = [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; } = [];
        public IReadOnlyList<string> Dependencies { get; } = [];
    }
    private static T Read<T>(object value, string field)
        => (T)(value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value)
            ?? throw new InvalidOperationException($"Missing actual diagnostic field {field}"));
    private static bool IsDisposed(CancellationToken token) { try { _ = token.WaitHandle; return false; } catch (ObjectDisposedException) { return true; } }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<Exception> Failure(Task task)
    {
        try { await task.WaitAsync(Bound); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected actual failure.");
    }
    private static async Task Until(Func<bool> predicate, string label)
    {
        var stop = Stopwatch.StartNew();
        while (!predicate()) { if (stop.Elapsed >= Bound) throw new TimeoutException(label); await Task.Delay(1); }
    }
}
