using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void NativeUnionCodecIdentityShouldTrackCaseSetAndAllowTagReuse()
    {
        var oneCase = BuildUnionSource(
            """
[SharpLink.Sdk.RpcUnionCase(1, typeof(CardPayment))]
""",
            """
public sealed class CardPayment : IPayment { public int Amount { get; set; } }
public sealed class CashPayment : IPayment { public int Amount { get; set; } }
public sealed class VoucherPayment : IPayment { public int Amount { get; set; } }
""");
        var twoCases = BuildUnionSource(
            """
[SharpLink.Sdk.RpcUnionCase(1, typeof(CardPayment))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(CashPayment))]
""",
            """
public sealed class CardPayment : IPayment { public int Amount { get; set; } }
public sealed class CashPayment : IPayment { public int Amount { get; set; } }
public sealed class VoucherPayment : IPayment { public int Amount { get; set; } }
""");
        var reusedTag = BuildUnionSource(
            """
[SharpLink.Sdk.RpcUnionCase(1, typeof(CardPayment))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(VoucherPayment))]
""",
            """
public sealed class CardPayment : IPayment { public int Amount { get; set; } }
public sealed class CashPayment : IPayment { public int Amount { get; set; } }
public sealed class VoucherPayment : IPayment { public int Amount { get; set; } }
""");

        var oneCaseHash = GetUnionCodecHash(oneCase, "global::IPayment");
        var twoCaseHash = GetUnionCodecHash(twoCases, "global::IPayment");
        Ensure(oneCaseHash != twoCaseHash,
            "adding or removing a declared union case must change the union CodecHash");
        Ensure(twoCaseHash != GetUnionCodecHash(reusedTag, "global::IPayment"),
            "reusing a discriminator for a different case in a later schema must change the union CodecHash");

        var reusedDiagnostics = RunGenerator(reusedTag);
        Ensure(!reusedDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            $"tag reuse in a distinct union schema must remain valid: {FormatDiagnostics(reusedDiagnostics)}");
        Ensure(GetRpcAssemblyHash(twoCases) != GetRpcAssemblyHash(reusedTag),
            "a reused discriminator with a different case mapping must propagate to a different RpcAssemblyHash");
    }

    private static string GetRpcAssemblyHash(string source)
    {
        var manifest = RunGeneratorAndGetSources(source)
            .Single(static text => text.Contains("public RpcHash128 RpcAssemblyHash =>", StringComparison.Ordinal));
        return manifest.Split('\n')
            .Select(static line => line.Trim())
            .Single(static line => line.StartsWith("public RpcHash128 RpcAssemblyHash =>", StringComparison.Ordinal));
    }
}
