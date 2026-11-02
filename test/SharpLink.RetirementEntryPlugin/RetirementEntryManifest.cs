using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Sdk;

[assembly: SharpLinkGeneratedAssemblyManifest(typeof(SharpLink.RetirementEntryPlugin.EntryManifest),
    SharpLinkGeneratedManifestVersions.Api, SharpLinkGeneratedManifestVersions.Protocol,
    "retirement-entry-control", SharpLinkGeneratedManifestVersions.AbiIdentity)]

namespace SharpLink.RetirementEntryPlugin;

public interface IEntryContract : IService { }
public sealed class EntryService : IEntryContract { }

// Controls set only the new collectible assembly's fields before loading its
// manifest. Each manifest/scope captures its own exact stage markers.
public static class EntryState
{
    public static TaskCompletionSource? ManifestEntered;
    public static TaskCompletionSource? ManifestRelease;
    public static Exception? StubFailure;
    public static Exception? ScopeFailure;
    public static Exception? ManifestFailureObserved;
    public static int ManifestCount;
    public static int StubCount;
    public static int ScopeCreated;
    public static int ScopeDisposeEntered;
    public static int ScopeDisposeExited;
}

public sealed class EntryManifest : ISharpLinkGeneratedAssemblyManifest
{
    public const long ContractId = 0x7E171;
    public EntryManifest()
    {
        Interlocked.Increment(ref EntryState.ManifestCount);
        var stubFailure = EntryState.StubFailure;
        var scopeFailure = EntryState.ScopeFailure;
        var contract = typeof(IEntryContract);
        Contracts = [new(contract, contract.FullName!, ContractId, new string('e', 64), [],
            static (_, _) => throw new NotSupportedException("No proxy is constructed by these controls."),
            provider =>
            {
                Interlocked.Increment(ref EntryState.StubCount);
                _ = provider.GetCodec<string>(); // Exact eagerly created registration scope.
                if (stubFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stubFailure).Throw();
                return new EntryStub();
            })];
        Services = [new(contract, typeof(EntryService), contract.FullName!, typeof(EntryService).FullName!,
            ContractId, new string('e', 64), SharpLinkServiceLifetime.Call, [], static _ => new EntryService())];
        Codecs = [new EntryCodecFactory(scopeFailure)];
        EntryState.ManifestEntered?.TrySetResult();
        try { EntryState.ManifestRelease?.Task.GetAwaiter().GetResult(); }
        catch (Exception exception) { EntryState.ManifestFailureObserved = exception; throw; }
    }
    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "retirement-entry-control";
    public Assembly OwnerAssembly => typeof(EntryManifest).Assembly;
    public RpcHash128 RpcAssemblyHash => new(0x726574697265656eUL, 0x7472792d63642d31UL);
    public string CompileTimeDescriptor => "retirement-entry-control/v1";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; }
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services { get; }
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; }
    public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => [];
    public IReadOnlyList<string> Dependencies => [];
}

internal sealed class EntryCodecFactory(Exception? cleanupFailure) : IRpcGeneratedCodecFactory
{
    public Type TargetType => typeof(string);
    public RpcHash128 CodecHash => new(0x656e747279636f64UL, 1);
    public string AdapterId => "retirement-entry/v1";
    public IRpcCodecAdapter Adapter { get; } = new EntryAdapter(cleanupFailure);
    public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        => (adapterScope ?? throw new ArgumentNullException(nameof(adapterScope))).CreateCodec<string>();
    public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<string>;
}
internal sealed class EntryAdapter(Exception? cleanupFailure) : IRpcCodecAdapter
{
    public string AdapterId => "retirement-entry/v1";
    public IRpcCodecAdapterScope CreateScope()
    {
        Interlocked.Increment(ref EntryState.ScopeCreated);
        return new EntryScope(cleanupFailure);
    }
}
internal sealed class EntryScope(Exception? cleanupFailure) : IRpcCodecAdapterScope
{
    public IRpcCodec<T> CreateCodec<T>() => (IRpcCodec<T>)(object)new EntryStringCodec();
    public void Dispose()
    {
        Interlocked.Increment(ref EntryState.ScopeDisposeEntered);
        try
        {
            if (cleanupFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
        finally { Interlocked.Increment(ref EntryState.ScopeDisposeExited); }
    }
}
internal sealed class EntryStringCodec : IRpcCodec<string>
{
    public void Serialize(in string value, IBufferWriter<byte> buffer) { }
    public string Deserialize(in ReadOnlySequence<byte> buffer) => string.Empty;
}
internal sealed class EntryStub : IRpcStub
{
    public long InterfaceHash => EntryManifest.ContractId;
    public RpcMethodFacts ResolveMethodFacts(long methodHash) => RpcMethodFacts.UnknownMethod;
    public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
        long requestId, ReadOnlySequence<byte> args) => throw new NotSupportedException();
    public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
        long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
        long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => throw new NotSupportedException();
    public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
        long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
