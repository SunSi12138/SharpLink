using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task AllRouteShouldSkipFrameworkWirePrimitivesButKeepCompositeConfigurable()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public enum FixedMode : byte
{
    Zero,
    One
}

[SharpLink.Sdk.RpcContract]
public interface IFrameworkPrimitiveBoundaryContract : SharpLink.Sdk.IService
{
    ValueTask<int> EchoInt(int value, CancellationToken cancellationToken);
    ValueTask<string> EchoString(string value, CancellationToken cancellationToken);
    ValueTask<Guid> EchoGuid(Guid value, CancellationToken cancellationToken);
    ValueTask<FixedMode> EchoEnum(FixedMode value, CancellationToken cancellationToken);
    ValueTask<byte[]> EchoBytes(byte[] value, CancellationToken cancellationToken);
    ValueTask<System.Collections.Generic.List<int>> EchoList(
        System.Collections.Generic.List<int> value,
        CancellationToken cancellationToken);
}

public sealed class RouteAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "route.framework-boundary/v1";
    public override string WireFormatId => "route-framework-boundary-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(RouteAdapter), \"route.framework-boundary/v1\", \"route-framework-boundary-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecRoute(SharpLink.Sdk.RpcCodecScope.All, typeof(RouteAdapter))]");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("CreateCodec<int>()", StringComparison.Ordinal),
            "int is a fixed framework wire primitive");
        Ensure(!generated.Contains("CreateCodec<string>()", StringComparison.Ordinal),
            "string is a fixed framework wire primitive");
        Ensure(!generated.Contains("CreateCodec<global::System.Guid>()", StringComparison.Ordinal),
            "Guid is a fixed framework wire primitive");
        Ensure(!generated.Contains("CreateCodec<global::FixedMode>()", StringComparison.Ordinal),
            "enum is a fixed framework wire primitive");
        Ensure(!generated.Contains("CreateCodec<byte[]>()", StringComparison.Ordinal),
            "byte[] is the explicit bytes primitive exception");
        Ensure(generated.Contains(
                "CreateCodec<global::System.Collections.Generic.List<int>>()",
                StringComparison.Ordinal),
            "ordinary collection types remain configurable even when their element is a framework primitive");
        return Task.CompletedTask;
    }

    [Test]
    public Task FrameworkWirePrimitiveAdapterBindingsShouldBeRejectedAsOnePolicyClass()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public enum FixedMode : byte
{
    Zero,
    One
}

[SharpLink.Sdk.RpcContract]
public interface IFrameworkPrimitiveBindingContract : SharpLink.Sdk.IService
{
    ValueTask<int> EchoInt(int value, CancellationToken cancellationToken);
    ValueTask<string> EchoString(string value, CancellationToken cancellationToken);
    ValueTask<Guid> EchoGuid(Guid value, CancellationToken cancellationToken);
    ValueTask<FixedMode> EchoEnum(FixedMode value, CancellationToken cancellationToken);
    ValueTask<byte[]> EchoBytes(byte[] value, CancellationToken cancellationToken);
}

public sealed class PrimitiveAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "primitive-rebind/v1";
    public override string WireFormatId => "primitive-rebind-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(PrimitiveAdapter), \"primitive-rebind/v1\", \"primitive-rebind-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(int), typeof(PrimitiveAdapter))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(string), typeof(PrimitiveAdapter))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(Guid), typeof(PrimitiveAdapter))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(FixedMode), typeof(PrimitiveAdapter))]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(byte[]), typeof(PrimitiveAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK049") == 5,
            "every framework wire primitive target must be rejected by the same fixed-wire policy boundary");
        return Task.CompletedTask;
    }

    [Test]
    public Task ByteArrayShouldBeFixedBytesPrimitiveWhileOrdinaryArraysRemainConfigurable()
    {
        var source = AddAssemblyAttributes(BuildSource("""
[SharpLink.Sdk.RpcCodecImplementation("bytes-custom/v1", "bytes-custom-schema/v1")]
public sealed class BytesCodec : SharpLink.Abstractions.IRpcCodec<byte[]>
{
}

[SharpLink.Sdk.RpcCodecImplementation("int-array-custom/v1", "int-array-custom-schema/v1")]
public sealed class IntArrayCodec : SharpLink.Abstractions.IRpcCodec<int[]>
{
}

[SharpLink.Sdk.RpcContract]
public interface IArrayBoundaryContract : SharpLink.Sdk.IService
{
    ValueTask<byte[]> EchoBytes(byte[] value, CancellationToken cancellationToken);
    ValueTask<int[]> EchoInts(int[] value, CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(byte[]), typeof(BytesCodec))]",
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(int[]), typeof(IntArrayCodec))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK063") == 1,
            "only byte[] must be rejected as the framework bytes primitive; ordinary arrays remain configurable");

        var generated = string.Join("\n", RunGeneratorAndGetSources(source));
        Ensure(!generated.Contains("new global::BytesCodec()", StringComparison.Ordinal),
            "byte[] custom Codec must not be published");
        Ensure(generated.Contains("new global::IntArrayCodec()", StringComparison.Ordinal),
            "ordinary int[] remains a configurable closed payload type");
        return Task.CompletedTask;
    }
}
