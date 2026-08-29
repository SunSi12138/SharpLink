using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task FrameworkEnumAdapterBindingShouldBeRejected()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public enum FixedMode : short
{
    Zero,
    One
}

[SharpLink.Sdk.RpcContract]
public interface IFixedEnumContract : SharpLink.Sdk.IService
{
    ValueTask<FixedMode> Echo(FixedMode value, CancellationToken cancellationToken);
}

public sealed class EnumAdapter : TestRouteAdapterBase
{
    public override string AdapterId => "fixed-enum/v1";
    public override string WireFormatId => "fixed-enum-wire/v1";
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodecAdapterRegistration(typeof(EnumAdapter), \"fixed-enum/v1\", \"fixed-enum-wire/v1\")]",
            "[assembly: SharpLink.Sdk.RpcCodecAdapter(typeof(FixedMode), typeof(EnumAdapter))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK049"),
            "framework enum wire semantics must not be rebound through RpcCodecAdapter");
        return Task.CompletedTask;
    }

    [Test]
    public Task FrameworkEnumCustomCodecBindingShouldBeRejected()
    {
        var source = AddAssemblyAttributes(BuildRouteSource("""
public enum FixedMode : int
{
    Zero,
    One
}

[SharpLink.Sdk.RpcCodecImplementation("fixed-enum-wire/v2", "fixed-enum-schema/v2")]
public sealed class EnumCodec : SharpLink.Abstractions.IRpcCodec<FixedMode>
{
}

[SharpLink.Sdk.RpcContract]
public interface IFixedEnumContract : SharpLink.Sdk.IService
{
    ValueTask<FixedMode> Echo(FixedMode value, CancellationToken cancellationToken);
}
"""),
            "[assembly: SharpLink.Sdk.RpcCodec(typeof(FixedMode), typeof(EnumCodec))]");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK063"),
            "framework enum wire semantics must not be rebound through RpcCodec");
        return Task.CompletedTask;
    }
}
