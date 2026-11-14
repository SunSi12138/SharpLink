using SharpLink.Abstractions;
using SharpLink.Sdk;
namespace SharpLink.GroupPlugin;

public static class Signals { public static int Tag; public static Action<string>? Observe; }
[RpcContract]
public interface IUnrelatedRpc : IService { [NonCancellable] ValueTask<int> GetAsync(); }
[RpcService]
public sealed class UnrelatedService : IUnrelatedRpc { public ValueTask<int> GetAsync() => ValueTask.FromResult(Signals.Tag); }
