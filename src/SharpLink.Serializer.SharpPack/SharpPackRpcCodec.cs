using System;
using System.Buffers;
using System.ComponentModel;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Sdk;
using SharpPack;

[assembly: RpcCodecAdapterRegistration(
    typeof(SharpLink.Serializer.SharpPack.SharpPackRpcCodecAdapter),
    SharpLink.Serializer.SharpPack.SharpPackRpcCodecAdapter.AdapterIdentity,
    SelectorAttributeType = typeof(SharpPackableAttribute))]

namespace SharpLink.Serializer.SharpPack;

/// <summary>Creates explicit SharpPack Codecs backed by a caller-owned serializer Context.</summary>
public static class SharpPackRpcCodec
{
    /// <summary>Creates a Codec using the supplied caller-owned SharpPack Context.</summary>
    public static IRpcCodec<T> Create<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>(
        SharpPackSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new SharpPackRpcCodec<T>(context);
    }
}

/// <summary>SharpPack integration selected by generated Manifest metadata.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[RpcCodecSemanticIdentity(0x3fd7540d55dfa977UL, 0xbb67b4932c1a5249UL)]
public sealed class SharpPackRpcCodecAdapter : IRpcCodecAdapter
{
    /// <summary>The stable Adapter implementation identity.</summary>
    public const string AdapterIdentity = "sharplink.serializer.sharppack/v1";

    /// <inheritdoc />
    public string AdapterId => AdapterIdentity;

    /// <inheritdoc />
    public IRpcCodecAdapterScope CreateScope() => new SharpPackRpcCodecAdapterScope();
}

internal sealed class SharpPackRpcCodecAdapterScope : IRpcCodecAdapterScope
{
    private SharpPackSerializerContext? _context = CreateIsolatedContext();

    public IRpcCodec<T> CreateCodec<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>()
    {
        var context = Volatile.Read(ref _context);
        ObjectDisposedException.ThrowIf(context is null, this);
        return new SharpPackRpcCodec<T>(context);
    }

    public void Dispose() => Interlocked.Exchange(ref _context, null);

    private static SharpPackSerializerContext CreateIsolatedContext()
        => new SharpPackSerializerContextBuilder()
            .Register<SharpPackScopeMarker>(new SharpPackScopeMarkerFormatter())
            .Build();
}

internal sealed class SharpPackScopeMarker;

internal sealed class SharpPackScopeMarkerFormatter : SharpPackFormatter<SharpPackScopeMarker>
{
    public override void Serialize<TBufferWriter>(
        ref SharpPackWriter<TBufferWriter> writer,
        scoped ref SharpPackScopeMarker? value)
        => throw new NotSupportedException("The internal SharpPack Scope marker cannot be serialized.");

    public override void Deserialize(
        ref SharpPackReader reader,
        scoped ref SharpPackScopeMarker? value)
        => throw new NotSupportedException("The internal SharpPack Scope marker cannot be deserialized.");
}

internal sealed class SharpPackRpcCodec<
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T> : IRpcCodec<T>
{
    private readonly SharpPackSerializerContext _context;

    internal SharpPackRpcCodec(SharpPackSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    internal SharpPackSerializerContext Context => _context;

    public void Serialize(in T value, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        try
        {
            var bufferWriter = new SharpPackBufferWriter(writer);
            SharpPackSerializer.Serialize(ref bufferWriter, value, _context);
        }
        catch (Exception exception) when (ShouldWrap(exception))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DataLoss,
                $"SharpPack serialization failed for '{typeof(T).FullName}'.",
                exception);
        }
    }

    public T? Deserialize(in ReadOnlySequence<byte> sequence)
    {
        try
        {
            T? value = default;
            var consumed = SharpPackSerializer.Deserialize(in sequence, ref value, _context);
            if (consumed != sequence.Length)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.DataLoss,
                    $"SharpPack payload for '{typeof(T).FullName}' contains trailing bytes.");
            }
            return value;
        }
        catch (Exception exception) when (ShouldWrap(exception))
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DataLoss,
                $"SharpPack deserialization failed for '{typeof(T).FullName}'.",
                exception);
        }
    }

    internal static bool ShouldWrap(Exception exception) => !ContainsNonWrappableException(exception);

    private static bool ContainsNonWrappableException(Exception exception)
    {
        if (exception is SharpLinkException or
            OperationCanceledException or
            OutOfMemoryException or
            StackOverflowException or
            AccessViolationException)
        {
            return true;
        }

        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.InnerExceptions)
            {
                if (ContainsNonWrappableException(innerException))
                {
                    return true;
                }
            }

            return false;
        }

        return exception.InnerException is not null &&
            ContainsNonWrappableException(exception.InnerException);
    }
}

// Keep SharpPack's generic writer closure concrete for NativeAOT while forwarding
// to the caller-owned writer without allocating a wrapper object.
internal readonly struct SharpPackBufferWriter(IBufferWriter<byte> writer) : IBufferWriter<byte>
{
    public void Advance(int count) => writer.Advance(count);
    public Memory<byte> GetMemory(int sizeHint = 0) => writer.GetMemory(sizeHint);
    public Span<byte> GetSpan(int sizeHint = 0) => writer.GetSpan(sizeHint);
}