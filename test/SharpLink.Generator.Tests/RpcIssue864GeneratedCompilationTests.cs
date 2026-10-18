using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue864StreamAndCodecFieldCollisionsMustCompileCompleteGeneratedContract()
    {
        const string source = """
using System.Collections.Generic;
using System.Threading.Tasks;
using SharpLink.Sdk;

[RpcContract]
public interface IStreamFieldCollision : IService
{
    [NonCancellable] ValueTask<int> _channel();
    [NonCancellable] ValueTask<int> Echo(int _channel);

    // _codec_x produces the same field spelling as x's codec without
    // a shared stream-field/codec-field allocator.
    [NonCancellable] ValueTask<int> Upload(
        IAsyncEnumerable<int> x, IAsyncEnumerable<int> _codec_x);

    [NonCancellable] ValueTask<int> UploadWithCodecParameter(
        IAsyncEnumerable<int> x, IAsyncEnumerable<int> __codec_x);

    [NonCancellable] ValueTask<int> UploadWithStreamFieldParameter(
        IAsyncEnumerable<int> x, IAsyncEnumerable<int> _x);

    [NonCancellable] ValueTask<int> UploadTriple(
        IAsyncEnumerable<int> x,
        IAsyncEnumerable<int> _codec_x,
        IAsyncEnumerable<int> __codec_x_);
}
""";
        var realAbstractions = MetadataReference.CreateFromFile(
            typeof(SharpLink.Sdk.IService).Assembly.Location);
        var input = GeneratorTestHarness.CreateCompilation(
            "Issue864StreamFieldCollisionCompilation", source, [realAbstractions]);
        var inputErrors = input.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Ensure(inputErrors.Length == 0,
            $"source contract must be valid before generation: {FormatDiagnostics(inputErrors)}");

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RpcGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            input, out var output, out var generatorDiagnostics);
        var run = driver.GetRunResult();
        Ensure(!generatorDiagnostics.Any(static diagnostic =>
                diagnostic.Id == "CS8785" || diagnostic.Severity == DiagnosticSeverity.Error),
            $"generator failed: {FormatDiagnostics(generatorDiagnostics)}");

        var generated = run.GeneratedTrees.Select(static tree => tree.GetText().ToString()).ToArray();
        var streamHelpers = string.Join("\n", generated.Where(static text =>
            text.Contains("IRpcClientStreamWriter", StringComparison.Ordinal)));
        Ensure(streamHelpers.Contains("__codec_x_", StringComparison.Ordinal),
            "codec field that collides with another stream field must be reallocated");
        Ensure(streamHelpers.Contains("this.__codec_x_ =", StringComparison.Ordinal),
            "codec constructor assignment must target the allocated field");
        Ensure(streamHelpers.Contains("this.__codec_x =", StringComparison.Ordinal),
            "second stream field must receive its original stream instance");
        Ensure(streamHelpers.Contains(
                "sink.SendClientStreamAsync(requestId, (ushort)1, _x, __codec_x_", StringComparison.Ordinal),
            "first stream must retain its matching codec and wire stream ID");

        var errors = output.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Ensure(errors.Length == 0,
            $"complete generated contract contains invalid C#: {FormatDiagnostics(errors)}");
        using var binary = new MemoryStream();
        var emit = output.Emit(binary);
        Ensure(emit.Success,
            $"complete generated C# failed Emit: {FormatDiagnostics(emit.Diagnostics)}");
        return Task.CompletedTask;
    }
}
