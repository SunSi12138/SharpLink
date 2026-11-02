using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;

[assembly: SharpLinkGeneratedAssemblyManifest(
    typeof(SharpLink.RollbackPlugin.RollbackManifest),
    SharpLinkGeneratedManifestVersions.Api,
    SharpLinkGeneratedManifestVersions.Protocol,
    "rollback-test",
    SharpLinkGeneratedManifestVersions.AbiIdentity)]

namespace SharpLink.RollbackPlugin;

public sealed class RollbackMarker;

public interface IRollbackRetirementContract { }

public static class RollbackState
{
    public static int ScopeDisposeCount;
    public static TaskCompletionSource? ManifestConstructionStarted;
    public static TaskCompletionSource? ManifestConstructionRelease;
    public static SemaphoreSlim TestIsolation { get; } = new(1, 1);
}

public sealed class RollbackManifest : ISharpLinkGeneratedAssemblyManifest
{
    private readonly RollbackScopeReceipts _scopeReceipts = new();

    // Instance-owned diagnostic receipts; no static roots retain collectible manifests.
    public (Exception Failure, int Count, int Order)[] GetScopeCleanupReceipts()
        => _scopeReceipts.Snapshot();

    public RollbackManifest()
    {
        var identity = Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_CODEC_IDENTITY") ??
            Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA") ??
            "default";
        var codecHash = ComputeIdentityHash(identity);
        RpcAssemblyHash = new RpcHash128(0x726f6c6c6261636bUL, codecHash.Low);
        Codecs = string.Equals(
                Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC"),
                "1",
                StringComparison.Ordinal)
            ? []
            : [new RollbackCodecFactory(codecHash, _scopeReceipts)];

        if (string.Equals(Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_RETIREMENT_CONTRACT"),
                "1", StringComparison.Ordinal))
        {
            var contract = typeof(IRollbackRetirementContract);
            Contracts = [new SharpLinkGeneratedContractDescriptor(contract, contract.FullName!, 0x7751,
                new string('c', 64), [], static (_, _) => throw new NotSupportedException(),
                static _ => throw new NotSupportedException())];
        }

        var started = RollbackState.ManifestConstructionStarted;
        if (started is null)
            return;

        started.TrySetResult();
        RollbackState.ManifestConstructionRelease?.Task.GetAwaiter().GetResult();
    }

    public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
    public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
    public string GeneratorVersion => "rollback-test";
    public Assembly OwnerAssembly => typeof(RollbackManifest).Assembly;
    public RpcHash128 RpcAssemblyHash { get; }
    public string CompileTimeDescriptor => "rollback-test";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } = [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs { get; }
    public IReadOnlyList<IRpcGeneratedCodecFactory> ContractCodecs => [];
    public IReadOnlyList<string> Dependencies => [];

    private static RpcHash128 ComputeIdentityHash(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var low = offset;
        foreach (var character in value)
        {
            low ^= character;
            low *= prime;
        }
        return new RpcHash128(0x726f6c6c6261636bUL, low == 0 ? 1UL : low);
    }
}

internal sealed class RollbackCodecFactory(RpcHash128 codecHash, RollbackScopeReceipts receipts) : IRpcGeneratedCodecFactory
{
    public Type TargetType => typeof(string);
    public RpcHash128 CodecHash { get; } = codecHash;
    public string AdapterId => "rollback-adapter/v1";
    public IRpcCodecAdapter Adapter { get; } = new RollbackAdapter(receipts);

    public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        => (adapterScope ?? throw new ArgumentNullException(nameof(adapterScope))).CreateCodec<string>();

    public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<string>;
}

internal sealed class RollbackScopeReceipts
{
    private readonly object _gate = new();
    private readonly List<RollbackScope> _scopes = [];
    private int _nextDisposal;

    internal RollbackScope CreateScope()
    {
        lock (_gate)
        {
            var scope = new RollbackScope(this);
            _scopes.Add(scope);
            return scope;
        }
    }

    internal void Record(RollbackScope scope)
    {
        lock (_gate)
        {
            scope.DisposeCount++;
            scope.LastDisposalOrder = ++_nextDisposal;
        }
    }

    internal (Exception Failure, int Count, int Order)[] Snapshot()
    {
        lock (_gate)
        {
            var result = new (Exception, int, int)[_scopes.Count];
            for (var index = 0; index < result.Length; index++)
            {
                var scope = _scopes[index];
                result[index] = (scope.Failure, scope.DisposeCount, scope.LastDisposalOrder);
            }
            return result;
        }
    }
}

internal sealed class RollbackAdapter(RollbackScopeReceipts receipts) : IRpcCodecAdapter
{
    public string AdapterId => "rollback-adapter/v1";
    public IRpcCodecAdapterScope CreateScope() => receipts.CreateScope();
}

internal sealed class RollbackScope(RollbackScopeReceipts receipts) : IRpcCodecAdapterScope
{
    internal Exception Failure { get; } = new InvalidOperationException("rollback Adapter scope cleanup failed");
    internal int DisposeCount;
    internal int LastDisposalOrder;
    public IRpcCodec<T> CreateCodec<T>() => (IRpcCodec<T>)(object)new RollbackStringCodec();

    public void Dispose()
    {
        Interlocked.Increment(ref RollbackState.ScopeDisposeCount);
        receipts.Record(this);
        throw Failure;
    }
}

internal sealed class RollbackStringCodec : IRpcCodec<string>
{
    public void Serialize(in string value, IBufferWriter<byte> buffer) { }
    public string Deserialize(in ReadOnlySequence<byte> buffer) => string.Empty;
}
