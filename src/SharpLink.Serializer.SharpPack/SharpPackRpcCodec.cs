using System;
using System.Buffers;
using System.ComponentModel;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Sdk;
using SharpPack;

[assembly: RpcCodecAdapterRegistration(
    typeof(SharpLink.Runtime.SharpPackRpcCodecAdapter),
    SharpLink.Runtime.SharpPackRpcCodecAdapter.AdapterIdentity,
    SharpLink.Runtime.SharpPackRpcCodecAdapter.WireFormatIdentity,
    SelectorAttributeType = typeof(SharpPackableAttribute))]

namespace SharpLink.Runtime;

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
public sealed class SharpPackRpcCodecAdapter : IRpcCodecAdapter
{
    /// <summary>The stable Adapter implementation identity.</summary>
    public const string AdapterIdentity = "sharplink.serializer.sharppack/v1";

    /// <summary>The MemoryPack-compatible wire-format identity.</summary>
    public const string WireFormatIdentity = "memorypack-binary/v1";

    /// <inheritdoc />
    public string AdapterId => AdapterIdentity;

    /// <inheritdoc />
    public string WireFormatId => WireFormatIdentity;

    /// <inheritdoc />
    public IRpcCodecAdapterScope CreateScope() => new SharpPackRpcCodecAdapterScope();
}

internal sealed class SharpPackRpcCodecAdapterScope : IRpcCodecAdapterScope
{
    private SharpPackSerializerContext? _context = new();

    public IRpcCodec<T> CreateCodec<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>()
    {
        var context = Volatile.Read(ref _context);
        ObjectDisposedException.ThrowIf(context is null, this);
        return new SharpPackRpcCodec<T>(context);
    }

    public void Dispose() => Interlocked.Exchange(ref _context, null);
}

internal sealed class SharpPackRpcCodec<
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)] T>(
    SharpPackSerializerContext context) : IRpcCodec<T>
{
    public void Serialize(in T value, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        try
        {
            SharpPackSerializer.Serialize(writer, value, context);
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
            var consumed = SharpPackSerializer.Deserialize(in sequence, ref value, context);
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

    internal static bool ShouldWrap(Exception exception)
        => exception is not SharpLinkException and not OutOfMemoryException and not StackOverflowException;
}
