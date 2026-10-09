using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using SharpLink.Abstractions;
using SharpLink.Runtime;
namespace SharpLink.NullabilityEvidence;
internal static class Pump
{
    internal static Task Run(string shape,int count,int repetitions)
    {
        if(shape=="value")return RunCore(42,false,0,1,shape,count,repetitions);
        if(shape=="nullable-value")return RunCore<int?>(42,true,2,1,shape,count,repetitions);
        return RunCore("value",shape is not ("required" or "realistic"),shape=="all-null"?1:shape=="half-null"?2:0,shape=="realistic"?512:1,shape,count,repetitions);
    }
    private static async Task RunCore<T>(T value,bool nullable,int nullEvery,int payloadBytes,string shape,int count,int repetitions)
    {
        using var context=new SharpLinkRuntimeContextBuilder().Configure(options=>options.FlowControl.MaxSendQueueBytes=64*1024*1024).Build(includeGeneratedAssemblyCatalog:false);
        var input=new Pipe();var output=new DrainingWriter();
        await using var session=new RpcSession(new Transport(input.Reader,output),new RpcSessionCreationOptions(RpcSessionRole.Server,context));
        if(!session.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,ProtocolV2Capabilities.None,context.Protocol.MaxFramePayloadBytes,context.FlowControl.StreamReceiveWindowBytes,context.FlowControl.ConnectionReceiveWindowBytes)))throw new InvalidOperationException("Handshake failed");
        var codec=new PumpCodec<T>(payloadBytes);long streams=0;
        async Task Batch(int n,int warmCount=0)
        {
            for(var i=0;i<n;i++)
            {
                await session.PumpGeneratedOutboundStreamAsync(++streams,0,new Items<T>(value,warmCount==0?count:warmCount,nullEvery),codec,nullable,default).ConfigureAwait(false);
                await session.FlushSendQueueAsync().ConfigureAwait(false);
            }
        }
        await Batch(64,1000);await Task.Delay(500);await Batch(64,1000);await Task.Delay(300);
        var beforeItems=codec.Items;var beforeNulls=codec.Nulls;var beforeBytes=output.Bytes;
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();var allocated=GC.GetTotalAllocatedBytes(true);var sw=Stopwatch.StartNew();
        await Batch(repetitions);sw.Stop();var measuredBytes=GC.GetTotalAllocatedBytes(true)-allocated;
        var items=count*(long)repetitions;var nulls=nullEvery==0?0:items/nullEvery;
        if(codec.Items-beforeItems!=items||codec.Nulls-beforeNulls!=nulls)throw new InvalidOperationException("Pump item/null count changed");
        var expectedBytes=items*(ProtocolV2Constants.HeaderBytes+sizeof(ushort)+payloadBytes)+repetitions*(ProtocolV2Constants.HeaderBytes+sizeof(ushort));
        if(output.Bytes-beforeBytes!=expectedBytes)throw new InvalidOperationException($"Pump wire bytes changed {output.Bytes-beforeBytes}/{expectedBytes}");
        Program.Write("pump-local",shape,count,repetitions,sw.Elapsed.TotalNanoseconds,measuredBytes);
        await input.Writer.CompleteAsync();
    }
    private sealed class PumpCodec<T>(int bytes):IRpcCodec<T>
    {
        internal long Items,Nulls;
        public T Deserialize(in ReadOnlySequence<byte> buffer)=>throw new NotSupportedException();
        public void Serialize(in T value,IBufferWriter<byte> buffer)
        {
            Items++;if(value is null)Nulls++;
            buffer.GetSpan(bytes)[..bytes].Fill(value is null?(byte)0:(byte)42);buffer.Advance(bytes);
        }
    }
    private sealed class Transport(PipeReader input,PipeWriter output):ITransportConnection
    {
        public string Id=>"nullability-pump";public PipeReader Input=>input;public PipeWriter Output=>output;
        public EndPoint? LocalEndPoint=>null;public EndPoint? RemoteEndPoint=>null;
        public async ValueTask DisposeAsync(){await output.CompleteAsync();await input.CompleteAsync();}
    }
    private sealed class DrainingWriter:PipeWriter
    {
        private byte[] _buffer=new byte[65536];private int _written;internal long Bytes;
        public override void Advance(int bytes){if(bytes<0||_written>_buffer.Length-bytes)throw new InvalidOperationException("Invalid advance");_written+=bytes;Bytes+=bytes;}
        public override void CancelPendingFlush(){}
        public override void Complete(Exception? exception=null){_written=0;}
        public override ValueTask<FlushResult> FlushAsync(CancellationToken token=default){token.ThrowIfCancellationRequested();_written=0;return new(new FlushResult(false,false));}
        public override Memory<byte> GetMemory(int sizeHint=0){Ensure(sizeHint);return _buffer.AsMemory(_written);}
        public override Span<byte> GetSpan(int sizeHint=0){Ensure(sizeHint);return _buffer.AsSpan(_written);}
        private void Ensure(int sizeHint){var required=checked(_written+Math.Max(sizeHint,1));if(required>_buffer.Length)Array.Resize(ref _buffer,Math.Max(required,_buffer.Length*2));}
    }
}
