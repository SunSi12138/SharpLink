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
    public Task Issue870ClosedGenericMembersMustKeepConstructedTypeIdentity()
    {
        const string ambiguous = """
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IBase<T>
{
    [NonCancellable] ValueTask<int> Read();
}

[RpcContract]
public interface ICombined : IService, IBase<int>, IBase<string> { }
""";
        Check(ambiguous, expectedConflict: true);

        const string sharedClosedDiamond = """
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IBase<T>
{
    [NonCancellable] ValueTask<int> Read();
}
public interface ILeft : IBase<int> { }
public interface IRight : IBase<int> { }

[RpcContract]
public interface ICombined : IService, ILeft, IRight { }
""";
        Check(sharedClosedDiamond, expectedConflict: false);

        const string oneClosedParent = """
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IBase<T>
{
    [NonCancellable] ValueTask<int> Read();
}

[RpcContract]
public interface ICombined : IService, IBase<int> { }
""";
        Check(oneClosedParent, expectedConflict: false);

        const string directRedeclaration = """
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IBase<T>
{
    [NonCancellable] ValueTask<int> Read();
}

[RpcContract]
public interface ICombined : IService, IBase<int>, IBase<string>
{
    [NonCancellable] new ValueTask<int> Read();
}
""";
        Check(directRedeclaration, expectedConflict: false);

        const string distinctParameterOverloads = """
using System.Threading.Tasks;
using SharpLink.Sdk;

public interface IBase<T>
{
    [NonCancellable] ValueTask<int> Read(T value);
}

[RpcContract]
public interface ICombined : IService, IBase<int>, IBase<string> { }
""";
        Check(distinctParameterOverloads, expectedConflict: false);
        return Task.CompletedTask;

        static void Check(string source, bool expectedConflict)
        {
            var assemblyReference = RealAbstractionsCompilationReference.Create();
            var input = GeneratorTestHarness.CreateCompilation(
                "Issue870ConstructedGenericCompilation", source, [assemblyReference]);
            var inputErrors = input.GetDiagnostics()
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToArray();
            Ensure(inputErrors.Length == 0,
                $"valid C# contract was rejected before generation: {FormatDiagnostics(inputErrors)}");

            GeneratorDriver driver = CSharpGeneratorDriver.Create(new RpcGenerator());
            driver = driver.RunGeneratorsAndUpdateCompilation(
                input, out var output, out var generatorDiagnostics);
            var run = driver.GetRunResult();
            var conflicts = generatorDiagnostics.Where(
                static diagnostic => diagnostic.Id == "SHARPLINK057").ToArray();

            Ensure(!generatorDiagnostics.Any(static diagnostic => diagnostic.Id == "CS8785"),
                "unsupported inheritance must not crash the source generator");
            Ensure(conflicts.Length == (expectedConflict ? 1 : 0),
                $"unexpected inherited member conflict: {FormatDiagnostics(generatorDiagnostics)}");

            var generated = string.Join("\n", run.GeneratedTrees.Select(
                static tree => tree.GetText().ToString()));
            if (expectedConflict)
            {
                Ensure(!generated.Contains("private sealed class __Stub_", StringComparison.Ordinal),
                    "ambiguous closed generic RPC must suppress the invalid generated stub");
            }
            else
            {
                Ensure(generated.Contains("private sealed class __Stub_", StringComparison.Ordinal),
                    "supported inherited contract must generate its stub");
            }

            // Compile and emit the entire output against the actual Abstractions
            // assembly. In the negative case only the generator diagnostic should
            // reject the contract; no broken generated C# may escape to the user.
            var outputErrors = output.GetDiagnostics()
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToArray();
            Ensure(outputErrors.Length == 0,
                $"generated C# failed compilation: {FormatDiagnostics(outputErrors)}");
            using var binary = new MemoryStream();
            var emit = output.Emit(binary);
            Ensure(emit.Success,
                $"complete generated compilation failed Emit: {FormatDiagnostics(emit.Diagnostics)}");
        }
    }
}
