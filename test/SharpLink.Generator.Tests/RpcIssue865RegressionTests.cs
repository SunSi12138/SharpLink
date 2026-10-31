using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task Issue865CapturedSnapshotsMustReleaseEveryChildAndRetainMainFailure(bool useWindowsLineEndings)
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcSerializable]
public sealed class ChildSnapshot
{
    [SharpLink.Sdk.RpcMember(1)] public int Number { get; set; }
}
[SharpLink.Sdk.RpcSerializable]
public sealed class ParentSnapshot
{
    [SharpLink.Sdk.RpcMember(1)] public ChildSnapshot A { get; set; } = new();
    [SharpLink.Sdk.RpcMember(2)] public ChildSnapshot B { get; set; } = new();
    [SharpLink.Sdk.RpcMember(3)] public string Name { get; set; } = "";
}
[SharpLink.Sdk.RpcContract]
public interface ISnapshotContract : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<ParentSnapshot> Read();
}
""");
        var result = GeneratorTestHarness.Run("SnapshotOwnershipRegression", source);
        Ensure(!result.Diagnostics.Any(d => d.Id == "CS8785"), "generator must not fault");
        var code = string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()))
            .ReplaceLineEndings(useWindowsLineEndings ? "\r\n" : "\n")
            .ReplaceLineEndings("\n");
        Ensure(code.Contains("var __snapshotCleanupStarted = false;", StringComparison.Ordinal),
            "capture must install an exception cleanup path immediately after rent");
        Ensure(code.Contains("catch { /* The capture exception takes precedence. */ }", StringComparison.Ordinal),
            "capture cleanup may not mask the original getter/codec exception");
        Ensure(code.Contains("snapshot.__nestedSnapshot_0 = null;", StringComparison.Ordinal) &&
               code.Contains("snapshot.__nestedSnapshot_1 = null;", StringComparison.Ordinal),
            "each child ownership slot must be cleared before invoking cleanup");
        Ensure(code.Contains("__firstCleanupError?.Throw();", StringComparison.Ordinal),
            "all children must be attempted and first cleanup failure rethrown when primary absent");
        Ensure(code.Contains("finally\n        {\n            ReturnSnapshot(__snapshot);", StringComparison.Ordinal),
            "parent pool return must run even if a child release fails");
        Ensure(code.Contains("catch { /* Preserve the original writer/serializer exception. */ }", StringComparison.Ordinal),
            "writer/serializer failures must remain primary when cleanup faults");
        return Task.CompletedTask;
    }
}
