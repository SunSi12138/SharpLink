using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public void NativeUnionCodecShouldEmitDeterministicIdentityAndDispatch()
    {
        var baseline = BuildUnionSource(
            """
[SharpLink.Sdk.RpcUnionCase(1, typeof(CardPayment))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(CashPayment))]
""",
            """
public sealed class CardPayment : IPayment { public int Amount { get; set; } }
public sealed class CashPayment : IPayment { public int Amount { get; set; } }
public sealed class VoucherPayment : IPayment { public int Amount { get; set; } }
""");
        var reordered = BuildUnionSource(
            """
[SharpLink.Sdk.RpcUnionCase(2, typeof(CashPayment))]
[SharpLink.Sdk.RpcUnionCase(1, typeof(CardPayment))]
""",
            """
public sealed class CardPayment : IPayment { public int Amount { get; set; } }
public sealed class CashPayment : IPayment { public int Amount { get; set; } }
public sealed class VoucherPayment : IPayment { public int Amount { get; set; } }
""");
        var retagged = BuildUnionSource(
            """
[SharpLink.Sdk.RpcUnionCase(1, typeof(CardPayment))]
[SharpLink.Sdk.RpcUnionCase(7, typeof(CashPayment))]
""",
            """
public sealed class CardPayment : IPayment { public int Amount { get; set; } }
public sealed class CashPayment : IPayment { public int Amount { get; set; } }
public sealed class VoucherPayment : IPayment { public int Amount { get; set; } }
""");
        var sameWireDifferentCaseType = BuildUnionSource(
            """
[SharpLink.Sdk.RpcUnionCase(1, typeof(CardPayment))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(VoucherPayment))]
""",
            """
public sealed class CardPayment : IPayment { public int Amount { get; set; } }
public sealed class CashPayment : IPayment { public int Amount { get; set; } }
public sealed class VoucherPayment : IPayment { public int Amount { get; set; } }
""");
        var changedChildCodec = BuildUnionSource(
            """
[SharpLink.Sdk.RpcUnionCase(1, typeof(CardPayment))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(CashPayment))]
""",
            """
public sealed class CardPayment : IPayment { public int Amount { get; set; } }
public sealed class CashPayment : IPayment { public long Amount { get; set; } }
public sealed class VoucherPayment : IPayment { public int Amount { get; set; } }
""");

        var baselineHash = GetUnionCodecHash(baseline, "global::IPayment");
        Ensure(baselineHash == GetUnionCodecHash(reordered, "global::IPayment"),
            "union declaration order must not change CodecHash");
        Ensure(baselineHash != GetUnionCodecHash(retagged, "global::IPayment"),
            "changing a union discriminator must change CodecHash");
        Ensure(baselineHash != GetUnionCodecHash(sameWireDifferentCaseType, "global::IPayment"),
            "case logical identity must change CodecHash even when the child wire CodecHash is identical");
        Ensure(baselineHash != GetUnionCodecHash(changedChildCodec, "global::IPayment"),
            "changing a child CodecHash must change the containing union CodecHash");

        var generated = GetUnionCodecSource(baseline, "global::IPayment");
        Ensure(generated.Contains("case global::CardPayment", StringComparison.Ordinal),
            "native union encoder must dispatch CardPayment by generated type pattern");
        Ensure(generated.Contains("case global::CashPayment", StringComparison.Ordinal),
            "native union encoder must dispatch CashPayment by generated type pattern");
        Ensure(generated.Contains("__WriteDiscriminator(writer, 1)", StringComparison.Ordinal) &&
               generated.Contains("__WriteDiscriminator(writer, 2)", StringComparison.Ordinal),
            "native union encoder must write explicit declared discriminators");
        Ensure(generated.Contains("unknown discriminator", StringComparison.Ordinal),
            "native union decoder must fail closed on unknown discriminators");
        Ensure(!generated.Contains("GetType()", StringComparison.Ordinal) &&
               !generated.Contains("GetCustomAttributes", StringComparison.Ordinal),
            "native union dispatch must not use runtime reflection discovery");
    }

    [Test]
    public void NativeUnionCodecShouldWorkNestedAndRejectAmbiguousRuntimeCases()
    {
        var nested = BuildSource("""
[SharpLink.Sdk.RpcUnionCase(1, typeof(AlphaPayment))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(BetaPayment))]
public interface IPayment { }

public sealed class AlphaPayment : IPayment { public int Value { get; set; } }
public sealed class BetaPayment : IPayment { public int Value { get; set; } }

public sealed class PaymentEnvelope
{
    public IPayment? Current { get; set; }
    public List<IPayment> History { get; set; } = new();
}

[SharpLink.Sdk.RpcContract]
public interface IPaymentContract : SharpLink.Sdk.IService
{
    ValueTask<PaymentEnvelope> Echo(PaymentEnvelope value, CancellationToken cancellationToken);
}
""");
        var diagnostics = RunGenerator(nested);
        Ensure(!diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            $"nested native union graph should resolve without generator errors: {FormatDiagnostics(diagnostics)}");
        var generated = string.Join("\n", RunGeneratorAndGetSources(nested));
        Ensure(generated.Contains("IRpcCodec<global::IPayment>", StringComparison.Ordinal),
            "nested DTO/collection graph must bind the native union Codec");
        Ensure(generated.Contains("IRpcCodec<global::IPayment?>", StringComparison.Ordinal),
            "nullable union DTO members must preserve nullable reference annotations in emitted child Codec types");

        var ambiguous = BuildSource("""
[SharpLink.Sdk.RpcUnionCase(1, typeof(BaseCase))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(DerivedCase))]
public interface IAmbiguousUnion { }

public class BaseCase : IAmbiguousUnion { }
public sealed class DerivedCase : BaseCase { }

[SharpLink.Sdk.RpcContract]
public interface IAmbiguousContract : SharpLink.Sdk.IService
{
    ValueTask<IAmbiguousUnion> Echo(IAmbiguousUnion value, CancellationToken cancellationToken);
}
""");
        var ambiguousDiagnostics = RunGenerator(ambiguous);
        Ensure(ambiguousDiagnostics.Any(static diagnostic =>
                diagnostic.GetMessage().Contains("overlap at runtime", StringComparison.Ordinal)),
            $"overlapping runtime case mappings must be rejected: {FormatDiagnostics(ambiguousDiagnostics)}");
    }

    [Test]
    public void NativeUnionCodecShouldRoundTripCasesAndFailClosedAtRuntime()
    {
        const string source = """
#nullable enable
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SharpLink.Sdk
{
    public interface IService { }

    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class RpcContractAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true)]
    public sealed class RpcUnionCaseAttribute(int tag, Type caseType) : Attribute
    {
        public int Tag { get; } = tag;
        public Type CaseType { get; } = caseType;
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class RpcCodecAttribute(Type codecType) : Attribute
    {
        public Type CodecType { get; } = codecType;
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RpcCodecSemanticIdentityAttribute(ulong high, ulong low) : Attribute
    {
        public ulong High { get; } = high;
        public ulong Low { get; } = low;
    }
}

namespace SharpLink.Abstractions
{
    public interface IRpcCodec { }

    public interface IRpcCodec<T> : IRpcCodec
    {
        void Serialize(in T value, IBufferWriter<byte> buffer);
        T? Deserialize(in ReadOnlySequence<byte> buffer);
    }

    public interface IRpcCodecProvider
    {
        IRpcCodec<T> GetCodec<T>();
    }

    public interface IRpcCodecAdapter { }
    public interface IRpcCodecAdapterScope { }

    public readonly record struct RpcHash128(ulong High, ulong Low);

    public interface IRpcGeneratedCodecFactory
    {
        Type TargetType { get; }
        RpcHash128 CodecHash { get; }
        string? AdapterId { get; }
        IRpcCodecAdapter? Adapter { get; }
        IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope);
        bool IsCompatibleCodec(IRpcCodec codec);
    }

    public enum SharpLinkErrorCode
    {
        InvalidArgument,
        DataLoss
    }

    public sealed class SharpLinkException(SharpLinkErrorCode code, string message) : Exception(message)
    {
        public SharpLinkErrorCode Code { get; } = code;
    }

    public static class RpcGeneratedCodecWire
    {
        public static void EnsureFullyConsumed(in SequenceReader<byte> reader)
        {
            if (reader.Remaining != 0)
                throw DataLoss("trailing bytes");
        }

        public static SharpLinkException DataLoss(string message)
            => new(SharpLinkErrorCode.DataLoss, message);
    }
}

[SharpLink.Sdk.RpcUnionCase(1, typeof(Alpha))]
[SharpLink.Sdk.RpcUnionCase(2, typeof(Beta))]
public interface ITestUnion { }

[SharpLink.Sdk.RpcCodec(typeof(AlphaCodec))]
public readonly struct Alpha : ITestUnion { }

[SharpLink.Sdk.RpcCodec(typeof(BetaCodec))]
public readonly struct Beta : ITestUnion { }

public sealed class Gamma : ITestUnion { }

[SharpLink.Sdk.RpcContract]
public interface ITestContract : SharpLink.Sdk.IService
{
    ValueTask<ITestUnion> Echo(ITestUnion value, CancellationToken cancellationToken);
}

public sealed class TestCodecProvider : SharpLink.Abstractions.IRpcCodecProvider
{
    private readonly AlphaCodec _alpha = new();
    private readonly BetaCodec _beta = new();

    public SharpLink.Abstractions.IRpcCodec<T> GetCodec<T>()
    {
        if (typeof(T) == typeof(Alpha))
            return (SharpLink.Abstractions.IRpcCodec<T>)(object)_alpha;
        if (typeof(T) == typeof(Beta))
            return (SharpLink.Abstractions.IRpcCodec<T>)(object)_beta;
        throw new InvalidOperationException(typeof(T).FullName);
    }
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x10UL, 0x11UL)]
public sealed class AlphaCodec : SharpLink.Abstractions.IRpcCodec<Alpha>
{
    public void Serialize(in Alpha value, IBufferWriter<byte> buffer)
    {
        var span = buffer.GetSpan(1);
        span[0] = 0xA1;
        buffer.Advance(1);
    }

    public Alpha Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length != 1 || buffer.FirstSpan[0] != 0xA1)
            throw new Exception("bad Alpha payload");
        return new Alpha();
    }
}

[SharpLink.Sdk.RpcCodecSemanticIdentity(0x20UL, 0x21UL)]
public sealed class BetaCodec : SharpLink.Abstractions.IRpcCodec<Beta>
{
    public void Serialize(in Beta value, IBufferWriter<byte> buffer)
    {
        var span = buffer.GetSpan(1);
        span[0] = 0xB2;
        buffer.Advance(1);
    }

    public Beta Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length != 1 || buffer.FirstSpan[0] != 0xB2)
            throw new Exception("bad Beta payload");
        return new Beta();
    }
}

public static class UnionRuntimeProbe
{
    public static void Run()
    {
        var codecType = typeof(UnionRuntimeProbe).Assembly.GetTypes()
            .Single(type => type.Namespace == "SharpLink.Generated" &&
                            !type.IsAbstract &&
                            typeof(SharpLink.Abstractions.IRpcCodec<ITestUnion>).IsAssignableFrom(type));
        var codec = (SharpLink.Abstractions.IRpcCodec<ITestUnion>)Activator.CreateInstance(
            codecType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { new TestCodecProvider() },
            culture: null)!;

        VerifyCase(codec, new Alpha(), 1, 0xA1, typeof(Alpha));
        VerifyCase(codec, new Beta(), 2, 0xB2, typeof(Beta));

        var nullWriter = new ArrayBufferWriter<byte>();
        ITestUnion nullValue = null!;
        codec.Serialize(in nullValue, nullWriter);
        if (nullWriter.WrittenCount != sizeof(int) ||
            BinaryPrimitives.ReadInt32LittleEndian(nullWriter.WrittenSpan) != 0 ||
            codec.Deserialize(new ReadOnlySequence<byte>(nullWriter.WrittenMemory)) is not null)
        {
            throw new Exception("union null representation is not canonical");
        }

        var invalidWriter = new ArrayBufferWriter<byte>();
        ITestUnion invalidValue = new Gamma();
        try
        {
            codec.Serialize(in invalidValue, invalidWriter);
            throw new Exception("undeclared runtime union value was accepted");
        }
        catch (SharpLink.Abstractions.SharpLinkException exception)
            when (exception.Code == SharpLink.Abstractions.SharpLinkErrorCode.InvalidArgument)
        {
        }

        var unknown = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(unknown, 99);
        try
        {
            _ = codec.Deserialize(new ReadOnlySequence<byte>(unknown));
            throw new Exception("unknown union discriminator was accepted");
        }
        catch (SharpLink.Abstractions.SharpLinkException exception)
            when (exception.Code == SharpLink.Abstractions.SharpLinkErrorCode.DataLoss)
        {
        }
    }

    private static void VerifyCase(
        SharpLink.Abstractions.IRpcCodec<ITestUnion> codec,
        ITestUnion value,
        int expectedDiscriminator,
        byte expectedPayload,
        Type expectedType)
    {
        var writer = new ArrayBufferWriter<byte>();
        codec.Serialize(in value, writer);
        if (writer.WrittenCount != sizeof(int) + 1 ||
            BinaryPrimitives.ReadInt32LittleEndian(writer.WrittenSpan) != expectedDiscriminator ||
            writer.WrittenSpan[sizeof(int)] != expectedPayload)
        {
            throw new Exception($"bad union encoding for {expectedType.Name}");
        }
        var decoded = codec.Deserialize(new ReadOnlySequence<byte>(writer.WrittenMemory));
        if (decoded is null || decoded.GetType() != expectedType)
            throw new Exception($"bad union decoding for {expectedType.Name}");
    }
}
""";

        var compilation = GeneratorTestHarness.CreateCompilation(
            "NativeUnionCodecRuntimeProbe_" + Guid.NewGuid().ToString("N"),
            source);
        IIncrementalGenerator generator = new RpcGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out _,
            out var generatorDiagnostics);
        var codecSource = driver.GetRunResult().Results
            .SelectMany(static result => result.GeneratedSources)
            .Single(static generated => generated.HintName == "SharpLink.GeneratedCodecs.g.cs")
            .SyntaxTree;
        var runtimeCompilation = compilation.AddSyntaxTrees(codecSource);
        var errors = generatorDiagnostics
            .Concat(runtimeCompilation.GetDiagnostics())
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Ensure(errors.Length == 0,
            $"runtime union probe did not compile: {FormatDiagnostics(errors)}");

        using var image = new MemoryStream();
        var emit = runtimeCompilation.Emit(image);
        Ensure(emit.Success,
            $"runtime union probe emit failed: {FormatDiagnostics(emit.Diagnostics)}");
        image.Position = 0;
        var loadContext = new AssemblyLoadContext(
            "NativeUnionCodecRuntimeProbe_" + Guid.NewGuid().ToString("N"),
            isCollectible: true);
        try
        {
            var assembly = loadContext.LoadFromStream(image);
            assembly.GetType("UnionRuntimeProbe", throwOnError: true)!
                .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, null);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static string BuildUnionSource(string attributes, string caseDeclarations)
        => BuildSource($$"""
{{attributes}}
public interface IPayment { }

{{caseDeclarations}}

[SharpLink.Sdk.RpcContract]
public interface IPaymentService : SharpLink.Sdk.IService
{
    ValueTask<IPayment> Echo(IPayment value, CancellationToken cancellationToken);
}
""");

    private static string GetUnionCodecHash(string source, string unionType)
    {
        var generated = GetUnionCodecSource(source, unionType);
        var target = "public Type TargetType => typeof(" + unionType + ");";
        var targetIndex = generated.IndexOf(target, StringComparison.Ordinal);
        Ensure(targetIndex >= 0, $"missing generated union factory target '{unionType}'");
        var hashStart = generated.IndexOf("public RpcHash128 CodecHash =>", targetIndex, StringComparison.Ordinal);
        Ensure(hashStart >= 0, $"missing generated union CodecHash for '{unionType}'");
        var hashEnd = generated.IndexOf('\n', hashStart);
        return generated[hashStart..(hashEnd < 0 ? generated.Length : hashEnd)].Trim();
    }

    private static string GetUnionCodecSource(string source, string unionType)
        => RunGeneratorAndGetSources(source)
            .Single(text => text.Contains("public Type TargetType => typeof(" + unionType + ");", StringComparison.Ordinal));
}
