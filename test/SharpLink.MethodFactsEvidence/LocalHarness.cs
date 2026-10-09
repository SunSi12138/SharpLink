using System.Buffers.Binary;
using System.Collections.Frozen;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
namespace SharpLink.MethodFactsEvidence;

internal sealed class LocalHarness : IAsyncDisposable
{
    internal SharpLinkServer Server { get; }
    internal RpcSession Session { get; }
    internal ServerConnectionState Connection { get; }
    internal ServiceRegistration Registration { get; }
    internal IRpcStub Stub { get; }
    internal FactsService Service { get; } = new();
    internal long[] Methods { get; }
    internal long OneWayMethod { get; }
    private readonly Pipe _input = new();
    private readonly RpcGeneratedManifestRegistration? _moduleCodecs;
    internal SharpLinkDynamicModule? Module { get; }
    private readonly ReadOnlySequence<byte>[] _payloads;
    private readonly SharpLinkCallContextSnapshot[] _contexts;
    private long _requestId;

    internal LocalHarness(bool intercepted, bool dynamic = false, bool admission = false, CountingStub? counting = null, bool queued = false, string interceptorMode = "pass")
    {
        var builder = SharpLinkServerBuilder.Create().DisableAutomaticServiceRegistration().UseTransport(new IdleListener());
        if (intercepted) builder.AddInterceptor(new PassThrough(interceptorMode));
        if (admission) builder.UseAdmissionControl(o => { o.Global.UseConcurrency(queued ? 1 : 8); if (queued) { o.MaxQueuedCalls=8; o.MaxQueuedBytes=4096; o.MaxQueueDelay=TimeSpan.FromSeconds(10); o.QueueOneWayCalls=true; } });
        Server = (SharpLinkServer)builder.Build();
        var runtime = Access.Runtime(Server);
        Session = new RpcSession(new PipeTransport(_input.Reader, new SinkWriter()), new RpcSessionCreationOptions(RpcSessionRole.Server, runtime));
        Session.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion, ProtocolV2Capabilities.None,
            runtime.Protocol.MaxFramePayloadBytes, runtime.FlowControl.StreamReceiveWindowBytes, runtime.FlowControl.ConnectionReceiveWindowBytes));
        Connection = new ServerConnectionState(Session, new RpcSessionGeneratedServerBridge(Session),
            new StripedLongMap<ServerCallCancellationState>(runtime.Concurrency), CancellationToken.None, runtime.TimeProvider);
        if (!Connection.MarkReady(null)) throw new InvalidOperationException("Connection not ready");
        var manifest = SharpLink.Generated.__SharpLinkGeneratedAssemblyManifest_f01f8ae46b7b958f.Instance;
        var contract = manifest.Contracts.Single(c => c.ContractType == typeof(IFactsRpc));
        Stub = counting ?? contract.StubFactory(runtime.GetManifestCodecProvider(typeof(IFactsRpc).Assembly));
        if (dynamic)
        {
            _moduleCodecs = runtime.PrepareGeneratedManifest(manifest);
            Module = new SharpLinkDynamicModule(typeof(IFactsRpc).Assembly, manifest, _moduleCodecs);
        }
        Registration = ServiceRegistration.CreateSingleton(typeof(IFactsRpc), Stub, Service, ownsService:false, Module);
        Access.Registry(Server).PublishServices(new Dictionary<long, ServiceRegistration> { [Stub.InterfaceHash] = Registration }.ToFrozenDictionary());
        Access.State(Server) = 2;
        Methods = counting is not null ? [CountingStub.MethodId] : contract.Methods.Where(m => m.Name.StartsWith("Method",StringComparison.Ordinal)).OrderBy(m=>m.Name).Select(m=>m.MethodId).ToArray();
        OneWayMethod = counting is not null ? CountingStub.MethodId : contract.Methods.Single(m=>m.Name == "OneWay").MethodId;
        _payloads = Methods.Select(id => Payload(id)).ToArray();
        _contexts = Methods.Select(id => Access.Context(Server, Connection, Stub, id, 1, default, null, CancellationToken.None)).ToArray();
        if (counting is not null) counting.Reset();
    }
    internal ReadOnlySequence<byte> Payload(long method, bool expired = false)
    {
        var bytes = new byte[expired ? 24 : 16]; BinaryPrimitives.WriteInt64LittleEndian(bytes, Stub.InterfaceHash);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), method); return new(bytes);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Invoke(int index, bool includeContext)
    {
        index &= Methods.Length-1;
        var context = includeContext ? Access.Context(Server,Connection,Stub,Methods[index],1,default,null,CancellationToken.None) : _contexts[index];
        if (context is SharpLinkServerInvocationContext invocation) invocation.Status=SharpLinkInvocationStatus.Pending;
        using var scope = SharpLinkCallContext.Push(context);
        var call = Access.Invoke(Server,Registration,Connection,Session,Methods[index],1,default,null,CancellationToken.None,context);
        if (!call.IsCompletedSuccessfully) throw new InvalidOperationException("Local invocation unexpectedly suspended");
        call.GetAwaiter().GetResult();
    }
    internal ValueTask Dispatch(bool oneWay=false, bool cancellable=false, long? method=null, bool expired=false)
    {
        var id = ++_requestId;
        var flags = (oneWay ? ProtocolV2FrameFlags.OneWay : ProtocolV2FrameFlags.None) | (cancellable ? ProtocolV2FrameFlags.Cancellable : ProtocolV2FrameFlags.None);
        if (expired) flags |= ProtocolV2FrameFlags.HasTimeBudget;
        var payload = expired ? Payload(oneWay ? OneWayMethod : Methods[0], true) : method.HasValue ? Payload(method.Value) : oneWay ? Payload(OneWayMethod) : _payloads[0];
        var admission = Server.CaptureAdmissionProgramForTests(id);
        return oneWay ? Access.OneWay(Server,Connection,id,flags,payload,Connection.CallCancellations,CancellationToken.None,admission,null,false,0,null)
            : Access.Dispatch(Server,Connection,id,flags,payload,Connection.CallCancellations,CancellationToken.None,admission,null,false,null);
    }
    internal void RemoveService() => Access.Registry(Server).PublishServices(new Dictionary<long, ServiceRegistration>().ToFrozenDictionary());
    public async ValueTask DisposeAsync()
    {
        await Connection.CloseAsync(); await Session.DisposeAsync(); await Server.DisposeAsync();
        _moduleCodecs?.Dispose(); await _input.Writer.CompleteAsync();
    }
    private sealed class PassThrough(string mode) : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next) => mode == "short" ? ValueTask.CompletedTask : mode == "throw" ? ValueTask.FromException(new InvalidOperationException("interceptor failure")) : next(context);
    }
    private sealed class IdleListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default) => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class PipeTransport(PipeReader input,PipeWriter output) : ITransportConnection
    {
        public string Id=>"method-facts"; public PipeReader Input=>input; public PipeWriter Output=>output;
        public EndPoint? LocalEndPoint=>null; public EndPoint? RemoteEndPoint=>null;
        public async ValueTask DisposeAsync() { await Input.CompleteAsync(); await Output.CompleteAsync(); }
    }
    private sealed class SinkWriter : PipeWriter
    {
        private byte[] _bytes=new byte[4096];
        public override void Advance(int count) { }
        public override void CancelPendingFlush() { }
        public override void Complete(Exception? exception=null) { }
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken=default)=>new(new FlushResult(false,false));
        public override Memory<byte> GetMemory(int sizeHint=0) { if(sizeHint>_bytes.Length)_bytes=new byte[sizeHint];return _bytes; }
        public override Span<byte> GetSpan(int sizeHint=0)=>GetMemory(sizeHint).Span;
    }
}
internal static class Access
{
    [UnsafeAccessor(UnsafeAccessorKind.Field,Name="_runtimeContext")] internal static extern ref SharpLinkRuntimeContext Runtime(SharpLinkServer server);
    [UnsafeAccessor(UnsafeAccessorKind.Field,Name="_serviceModuleRegistry")] internal static extern ref ServerServiceModuleRegistry Registry(SharpLinkServer server);
    [UnsafeAccessor(UnsafeAccessorKind.Field,Name="_state")] internal static extern ref int State(SharpLinkServer server);
    [UnsafeAccessor(UnsafeAccessorKind.Method,Name="CreateCallContext")]
    internal static extern SharpLinkCallContextSnapshot Context(SharpLinkServer server,ServerConnectionState connection,IRpcStub stub,long methodId,long requestId,RpcDeadline deadline,SharpLinkMetadata? metadata,CancellationToken token);
    [UnsafeAccessor(UnsafeAccessorKind.Method,Name="InvokeServiceAsync")]
    internal static extern ValueTask Invoke(SharpLinkServer server,ServiceRegistration registration,ServerConnectionState connection,RpcSession session,long methodId,long requestId,ReadOnlySequence<byte> arguments,IRpcByteBufferWriter? output,CancellationToken token,SharpLinkCallContextSnapshot context);
    [UnsafeAccessor(UnsafeAccessorKind.Method,Name="DispatchRpcAsync")]
    internal static extern ValueTask Dispatch(SharpLinkServer server,ServerConnectionState connection,long requestId,ProtocolV2FrameFlags flags,ReadOnlySequence<byte> payload,StripedLongMap<ServerCallCancellationState> cancellations,CancellationToken token,AdmissionProgram? admission,ServerCallCancellationState? callState,bool granted,ServerRetainedAdmissionPayload? retained);
    [UnsafeAccessor(UnsafeAccessorKind.Method,Name="DispatchOneWayRpc")]
    internal static extern ValueTask OneWay(SharpLinkServer server,ServerConnectionState connection,long requestId,ProtocolV2FrameFlags flags,ReadOnlySequence<byte> payload,StripedLongMap<ServerCallCancellationState> cancellations,CancellationToken token,AdmissionProgram? admission,ServerCallCancellationState? callState,bool granted,int streams,ServerRetainedAdmissionPayload? retained);
}
internal sealed class CountingStub(RpcMethodKind kind, bool found=true, bool supportsCancellation=true, bool fail=false) : IRpcStub
{
    internal const long MethodId=71;
    public long InterfaceHash=>43;
    internal int Descriptors, Cancellations, Invocations;
    internal Task? Block;
    internal void Reset() { Descriptors=0; Cancellations=0; Invocations=0; }
    public bool TryGetMethodDescriptor(long methodHash,out RpcMethodDescriptor descriptor)
    {
        Descriptors++; descriptor = new(InterfaceHash,methodHash,kind,false,false,false,null); return found && methodHash==MethodId;
    }
    public bool SupportsCancellation(long methodHash) { Cancellations++; return supportsCancellation; }
    private ValueTask Invoke() { Invocations++; if(fail)throw new InvalidOperationException("controlled failure");return Block is null ? ValueTask.CompletedTask : new ValueTask(Block); }
    public ValueTask InvokeNoReturnAsync(object service,IRpcGeneratedServerBridge bridge,long methodHash,long requestId,ReadOnlySequence<byte> args)=>Invoke();
    public ValueTask InvokeNoReturnCancellableAsync(object service,IRpcGeneratedServerBridge bridge,long methodHash,long requestId,ReadOnlySequence<byte> args,CancellationToken cancellationToken)=>Invoke();
    public ValueTask InvokeAsync(object service,IRpcGeneratedServerBridge bridge,long methodHash,long requestId,ReadOnlySequence<byte> args,IBufferWriter<byte> output)=>Invoke();
    public ValueTask InvokeCancellableAsync(object service,IRpcGeneratedServerBridge bridge,long methodHash,long requestId,ReadOnlySequence<byte> args,IBufferWriter<byte> output,CancellationToken cancellationToken)=>Invoke();
}
