using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpLink.Abstractions;
using SharpLink.Runtime;
namespace SharpLink.NullabilityEvidence;
internal static class Program
{
    public static async Task Main(string[] args)
    {
        var mode=args[0];var shape=args[1];var count=int.Parse(args[2]);var repetitions=int.Parse(args[3]);
        if(mode=="pump") { await Pump.Run(shape,count,repetitions);return; }
        if(mode=="rpc") { await FullRpc.Run(shape,count,repetitions);return; }
        Func<int,Task> run;
        if(shape=="value")run=n=>Dispatch<int>(new ConstantCodec<int>(42,0),false,count,n);
        else if(shape=="nullable-value")run=n=>Dispatch<int?>(new ConstantCodec<int?>(42,2),true,count,n);
        else
        {
            var nullable=shape!="required"&&shape!="realistic";
            var nullEvery=shape=="all-null"?1:shape=="half-null"?2:0;
            IRpcCodec<string> codec=shape=="realistic"?new Utf8Codec():new ConstantCodec<string>("value",nullEvery);
            run=n=>Dispatch<string>(codec,nullable,count,n);
        }
        await run(Math.Max(1,2000000/count));await Task.Delay(500);await run(Math.Max(1,2000000/count));await Task.Delay(300);
        var checksumBefore=s_checksum;
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();var bytes=GC.GetTotalAllocatedBytes(true);var sw=Stopwatch.StartNew();
        await run(repetitions);sw.Stop();
        var expectedNulls=shape=="all-null"?count*(long)repetitions:shape is "half-null" or "nullable-value"?count*(long)repetitions/2:0;
        if(s_checksum-checksumBefore!=expectedNulls)throw new InvalidOperationException("Null item count changed");
        Write(mode,shape,count,repetitions,sw.Elapsed.TotalNanoseconds,GC.GetTotalAllocatedBytes(true)-bytes);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Dispatch<T>(IRpcCodec<T> codec,bool nullable,int count,int repetitions)
    {
        var payload=new ReadOnlySequence<byte>(new byte[codec is Utf8Codec?512:1]);
        for(var batch=0;batch<repetitions;batch++)
        {
            var d=PooledAsyncStreamDispatcher<T>.Rent(default,codec,nullable);var e=d.GetAsyncEnumerator();
            for(var i=0;i<count;i++)
            {
                await d.DispatchAsync(payload).ConfigureAwait(false);
                if(!await e.MoveNextAsync().ConfigureAwait(false))throw new InvalidOperationException("Missing item");
                Consume(e.Current);
            }
            d.Complete(null);if(await e.MoveNextAsync())throw new InvalidOperationException("Extra item");await e.DisposeAsync();
        }
    }
    private static long s_checksum;
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume<T>(T value){if(value is null)s_checksum++;}
    internal static void Write(string mode,string shape,int count,int repetitions,double nanos,long bytes)
        => Console.WriteLine(JsonSerializer.Serialize(new Row(mode,shape,count,repetitions,nanos/(count*(double)repetitions),bytes/(count*(double)repetitions),s_checksum),JsonContext.Default.Row));
}
internal sealed class ConstantCodec<T>(T value,int nullEvery):IRpcCodec<T>
{
    private int _calls;
    public T Deserialize(in ReadOnlySequence<byte> payload)=>nullEvery!=0&&++_calls%nullEvery==0?default!:value;
    public void Serialize(in T value,IBufferWriter<byte> writer){writer.GetSpan(1)[0]=0;writer.Advance(1);}
}
internal sealed class Utf8Codec:IRpcCodec<string>
{
    public string Deserialize(in ReadOnlySequence<byte> payload)=>Encoding.UTF8.GetString(payload.FirstSpan);
    public void Serialize(in string value,IBufferWriter<byte> writer){var span=writer.GetSpan(Encoding.UTF8.GetMaxByteCount(value.Length));writer.Advance(Encoding.UTF8.GetBytes(value,span));}
}
internal sealed record Row(string Mode,string Shape,int Count,int Repetitions,double NsPerItem,double BytesPerItem,long Checksum);
[JsonSerializable(typeof(Row))]
internal partial class JsonContext:JsonSerializerContext;
