using System;
using System.Linq;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void NativeUnionCodecShouldRejectNonSealedClassCase()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcUnionCase(1, typeof(OpenCase))]
public interface IOpenUnion { }

public class OpenCase : IOpenUnion
{
    public int Value { get; set; }
}

[SharpLink.Sdk.RpcContract]
public interface IOpenUnionContract : SharpLink.Sdk.IService
{
    ValueTask<IOpenUnion> Echo(IOpenUnion value, CancellationToken cancellationToken);
}
""");

        var diagnostics = RunGenerator(source);
        Ensure(diagnostics.Any(static diagnostic =>
                diagnostic.GetMessage().Contains("must be sealed to guarantee fail-closed runtime dispatch", StringComparison.Ordinal)),
            $"non-sealed native union case classes must be rejected: {FormatDiagnostics(diagnostics)}");
    }
}
