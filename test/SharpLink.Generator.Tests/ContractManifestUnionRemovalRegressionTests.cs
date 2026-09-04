using System.Linq;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void RemovingPublishedUnionTagShouldFailContractBaseline()
    {
        var baselineSource = BuildSource("""
public sealed class FirstCase : IResultUnion { }
public sealed class SecondCase : IResultUnion { }

[SharpLink.Sdk.RpcUnionCase(1, typeof(FirstCase))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(SecondCase))]
public interface IResultUnion { }
""");
        var currentSource = BuildSource("""
public sealed class FirstCase : IResultUnion { }
public sealed class SecondCase : IResultUnion { }

[SharpLink.Sdk.RpcUnionCase(1, typeof(FirstCase))]
public interface IResultUnion { }
""");

        var baseline = RunContractGenerator(baselineSource);
        Ensure(!baseline.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK033"),
            "valid baseline union tags must not report compatibility diagnostics");

        var current = RunContractGenerator(currentSource, baseline.Json);
        Ensure(current.Diagnostics.Any(static diagnostic => diagnostic.Id == "SHARPLINK033"),
            "removing a published union tag must report SHARPLINK033 so the tag reservation cannot disappear from future baselines");
    }

    [Test]
    public void RemovingEntirePublishedUnionShouldFailContractBaseline()
    {
        var baselineSource = BuildSource("""
public sealed class FirstCase : IResultUnion { }
public sealed class SecondCase : IResultUnion { }

[SharpLink.Sdk.RpcUnionCase(1, typeof(FirstCase))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(SecondCase))]
public interface IResultUnion { }
""");
        var currentSource = BuildSource("""
public sealed class FirstCase : IResultUnion { }
public sealed class SecondCase : IResultUnion { }
public interface IResultUnion { }
""");

        var baseline = RunContractGenerator(baselineSource);
        var current = RunContractGenerator(currentSource, baseline.Json);

        Ensure(current.Diagnostics.Count(static diagnostic => diagnostic.Id == "SHARPLINK033") == 2,
            "removing the union declaration metadata must retain every published tag reservation in the compatibility baseline");
    }
}
