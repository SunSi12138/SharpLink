using System;
using System.Linq;
using System.Threading.Tasks;

namespace SharpLink.Generator.Tests;

public partial class RpcAnalyzerTests
{
    [Test]
    public Task Issue864UserNamesMustNotCaptureProxyFieldsOrStreamBindings()
    {
        var source = BuildSource("""
[SharpLink.Sdk.RpcContract]
public interface INameCollision : SharpLink.Sdk.IService
{
    [SharpLink.Sdk.NonCancellable] ValueTask<int> _channel();
    [SharpLink.Sdk.NonCancellable] ValueTask<int> Echo(int _channel);
    [SharpLink.Sdk.NonCancellable] ValueTask<int> Upload(
        IAsyncEnumerable<int> x, IAsyncEnumerable<int> __codec_x);
    [SharpLink.Sdk.NonCancellable] ValueTask<int> UploadOther(
        IAsyncEnumerable<int> x, IAsyncEnumerable<int> _x);
}
""");
        var result = GeneratorTestHarness.Run("NameCollisionRegression", source);
        Ensure(!result.Diagnostics.Any(d => d.Id == "CS8785"), "generator should remain healthy");
        var generated = result.GeneratedTrees.Select(t => t.GetText().ToString()).ToArray();
        var proxy = generated.Single(t => t.Contains("private sealed class __Proxy_", StringComparison.Ordinal));
        var streams = string.Join("\n", generated.Where(t => t.Contains("IRpcClientStreamWriter", StringComparison.Ordinal)));
        Ensure(proxy.Contains("private readonly IRpcChannel _channel_;", StringComparison.Ordinal) &&
               proxy.Contains("this._channel_.InvokeUnaryAsync", StringComparison.Ordinal),
            "method and parameter names must not capture the channel field");
        Ensure(streams.Contains("this._x = x;", StringComparison.Ordinal) &&
               streams.Contains("__x = _x;", StringComparison.Ordinal),
            "stream assignments must target their own fields");
        Ensure(streams.Contains("__codec_x_", StringComparison.Ordinal),
            "infrastructure codec argument must avoid the user parameter __codec_x");
        return Task.CompletedTask;
    }
}
