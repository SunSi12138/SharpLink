namespace SharpLink.MethodFactsEvidence;
[RpcContract]
public interface IFactsRpc : IService
{
    [NonCancellable] ValueTask Method00();
    [NonCancellable] ValueTask Method01();
    [NonCancellable] ValueTask Method02();
    [NonCancellable] ValueTask Method03();
    [NonCancellable] ValueTask Method04();
    [NonCancellable] ValueTask Method05();
    [NonCancellable] ValueTask Method06();
    [NonCancellable] ValueTask Method07();
    [NonCancellable] ValueTask Method08();
    [NonCancellable] ValueTask Method09();
    [NonCancellable] ValueTask Method10();
    [NonCancellable] ValueTask Method11();
    [NonCancellable] ValueTask Method12();
    [NonCancellable] ValueTask Method13();
    [NonCancellable] ValueTask Method14();
    [NonCancellable] ValueTask Method15();
    [Oneway, NonCancellable] ValueTask OneWay();
    ValueTask Cancel(CancellationToken cancellationToken);
    [NonCancellable] ValueTask<int> Upload(IAsyncEnumerable<int> values);
    [NonCancellable] IAsyncEnumerable<int> Download();
    [NonCancellable] IAsyncEnumerable<int> Duplex(IAsyncEnumerable<int> values);
}
public sealed class FactsService : IFactsRpc
{
    public int Calls;
    public ValueTask Method00() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method01() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method02() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method03() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method04() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method05() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method06() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method07() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method08() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method09() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method10() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method11() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method12() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method13() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method14() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Method15() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask OneWay() { Calls++; return ValueTask.CompletedTask; }
    public ValueTask Cancel(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Calls++; return ValueTask.CompletedTask; }
    public async ValueTask<int> Upload(IAsyncEnumerable<int> values) { int sum = 0; await foreach(var value in values) sum += value; return sum; }
    public async IAsyncEnumerable<int> Download() { yield return 7; await Task.CompletedTask; }
    public async IAsyncEnumerable<int> Duplex(IAsyncEnumerable<int> values) { await foreach(var value in values) yield return value; }
}
