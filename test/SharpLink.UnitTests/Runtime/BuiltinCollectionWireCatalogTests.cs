using System.Collections.Immutable;
using System.Linq;
using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public class BuiltinCollectionWireCatalogTests
{
    [Test]
    public void CatalogShouldMatchRuntimeCollectionRegistrationsAndStrategies()
    {
        var catalogNames = RpcBuiltinCollectionWireCatalog.All
            .Select(static descriptor => descriptor.ElementTypeName)
            .ToHashSet(StringComparer.Ordinal);
        var runtimeNames = GetRuntimeCollectionElementTypes()
            .Select(static type => type.FullName ?? throw new InvalidOperationException("Builtin collection element has no CLR name."))
            .ToHashSet(StringComparer.Ordinal);

        Ensure(
            catalogNames.SetEquals(runtimeNames),
            $"builtin collection catalog mismatch; catalog-only=[{string.Join(",", catalogNames.Except(runtimeNames).OrderBy(static name => name, StringComparer.Ordinal))}], runtime-only=[{string.Join(",", runtimeNames.Except(catalogNames).OrderBy(static name => name, StringComparer.Ordinal))}]");

        foreach (var descriptor in RpcBuiltinCollectionWireCatalog.All)
        {
            var elementType = typeof(int).Assembly.GetType(descriptor.ElementTypeName) ??
                Type.GetType(descriptor.ElementTypeName, throwOnError: false);
            Ensure(elementType is not null, $"cannot resolve builtin element '{descriptor.ElementTypeName}'");

            foreach (var shape in GetCollectionShapes(elementType!))
            {
                Ensure(
                    BuiltinRpcCodecs.TryGet(shape.CollectionType, out var codec),
                    $"runtime has no builtin codec for '{shape.CollectionType}'");
                if (descriptor.Strategy == RpcBuiltinCollectionWireStrategy.RawBlit)
                {
                    var codecType = codec.GetType();
                    Ensure(
                        codecType.IsGenericType && codecType.GetGenericTypeDefinition() == shape.RawCodecDefinition,
                        $"'{shape.CollectionType}' must use '{shape.RawCodecDefinition}' but uses '{codecType}'");
                }
                else
                {
                    Ensure(
                        descriptor.Strategy == RpcBuiltinCollectionWireStrategy.DateTimeOffsetCanonical,
                        $"unknown catalog strategy '{descriptor.Strategy}'");
                    Ensure(
                        codec.GetType() == shape.DateTimeOffsetCodecType,
                        $"'{shape.CollectionType}' must use canonical DateTimeOffset codec '{shape.DateTimeOffsetCodecType}' but uses '{codec.GetType()}'");
                }
            }
        }
    }

    private static IEnumerable<Type> GetRuntimeCollectionElementTypes()
    {
        var field = typeof(BuiltinRpcCodecs).GetField("Codecs", BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("BuiltinRpcCodecs.Codecs field is missing.");
        var codecs = field.GetValue(null) as IReadOnlyDictionary<Type, IRpcCodec> ??
            throw new InvalidOperationException("BuiltinRpcCodecs.Codecs has an unexpected runtime type.");

        return codecs.Keys
            .Select(GetCollectionElementType)
            .Where(static type => type is not null)
            .Select(static type => type!)
            .Distinct();
    }

    private static Type? GetCollectionElementType(Type type)
    {
        if (type.IsArray && type.GetArrayRank() == 1)
            return type.GetElementType();
        if (!type.IsGenericType)
            return null;

        var definition = type.GetGenericTypeDefinition();
        if (definition == typeof(List<>) ||
            definition == typeof(Memory<>) ||
            definition == typeof(ReadOnlyMemory<>) ||
            definition == typeof(ImmutableArray<>))
        {
            return type.GetGenericArguments()[0];
        }
        return null;
    }

    private static CollectionShape[] GetCollectionShapes(Type elementType)
        =>
        [
            new(
                elementType.MakeArrayType(),
                typeof(BlitArrayCodec<>),
                typeof(DateTimeOffsetArrayCodec)),
            new(
                typeof(List<>).MakeGenericType(elementType),
                typeof(BlitListCodec<>),
                typeof(DateTimeOffsetListCodec)),
            new(
                typeof(Memory<>).MakeGenericType(elementType),
                typeof(BlitMemoryCodec<>),
                typeof(DateTimeOffsetMemoryCodec)),
            new(
                typeof(ReadOnlyMemory<>).MakeGenericType(elementType),
                typeof(BlitReadOnlyMemoryCodec<>),
                typeof(DateTimeOffsetReadOnlyMemoryCodec)),
            new(
                typeof(ImmutableArray<>).MakeGenericType(elementType),
                typeof(BlitImmutableArrayCodec<>),
                typeof(DateTimeOffsetImmutableArrayCodec))
        ];

    private readonly record struct CollectionShape(
        Type CollectionType,
        Type RawCodecDefinition,
        Type DateTimeOffsetCodecType);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
