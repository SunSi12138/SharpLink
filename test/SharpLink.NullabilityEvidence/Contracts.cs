using SharpLink.Sdk;
namespace SharpLink.NullabilityEvidence;
[RpcContract]
public interface INullabilityEvidence:IService
{
    [NonCancellable] IAsyncEnumerable<string> Required(int count,int size);
    [NonCancellable] IAsyncEnumerable<string?> Nullable(int count,int size,int nullEvery);
    [NonCancellable] IAsyncEnumerable<int> Values(int count);
    [NonCancellable] ValueTask<int> UploadRequired(IAsyncEnumerable<string> items);
    [NonCancellable] ValueTask<long> UploadNullable(IAsyncEnumerable<string?> items);
    [NonCancellable] ValueTask<int> UploadValues(IAsyncEnumerable<int> items);
}
[RpcService]
public sealed class StreamService:INullabilityEvidence
{
    public IAsyncEnumerable<string> Required(int count,int size)=>new Items<string>(new string('x',size),count,0);
    public IAsyncEnumerable<string?> Nullable(int count,int size,int nullEvery)=>new Items<string?>(new string('x',size),count,nullEvery);
    public IAsyncEnumerable<int> Values(int count)=>new Items<int>(42,count,0);
    public async ValueTask<int> UploadRequired(IAsyncEnumerable<string> items){var n=0;await foreach(var v in items){if(v is null)throw new InvalidOperationException("Null required");n++;}return n;}
    public async ValueTask<long> UploadNullable(IAsyncEnumerable<string?> items){var n=0;var nulls=0;await foreach(var v in items){n++;if(v is null)nulls++;}return ((long)nulls<<32)|(uint)n;}
    public async ValueTask<int> UploadValues(IAsyncEnumerable<int> items){var n=0;await foreach(var v in items){if(v!=42)throw new InvalidOperationException("Wrong int");n++;}return n;}
}
internal sealed class Items<T>(T value,int count,int nullEvery):IAsyncEnumerable<T>,IAsyncEnumerator<T>
{
    private int _index;
    public T Current=>nullEvery!=0&&_index%nullEvery==0?default!:value;
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken token=default){_index=0;return this;}
    public ValueTask<bool> MoveNextAsync()=>new(++_index<=count);
    public ValueTask DisposeAsync()=>default;
}
