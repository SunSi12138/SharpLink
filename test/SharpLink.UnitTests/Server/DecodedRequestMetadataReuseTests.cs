using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Linq;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Server;

public sealed class DecodedRequestMetadataReuseTests
{
    private static readonly TimeProvider Clock = new FixedClock();

    [Test]
    public async Task DecodedEnvelopeShouldMatchOriginalParserAcrossBoundaries()
    {
        await using var session = CreateSession(ProtocolV2Capabilities.Metadata);
        // Retains the independent corpus from the performance study. Every second
        // parse below compares the original parser with the production ReadDecoded.
        Ensure(Validate(session) == 2924, "the full differential corpus must run");
    }

    [Test]
    public async Task ReuseShouldStillRequireNegotiatedMetadata()
    {
        await using var sourceSession = CreateSession(ProtocolV2Capabilities.Metadata);
        await using var destinationSession = CreateSession(ProtocolV2Capabilities.None);
        var flags = ProtocolV2FrameFlags.Compressed | ProtocolV2FrameFlags.HasMetadata;
        var encoded = new ReadOnlySequence<byte>(Build(1, false, 20));
        var decoded = new ReadOnlySequence<byte>(Build(1, false, 64));
        var first = ServerRequestEnvelopeReader.Read(sourceSession, encoded, flags, 65536, Clock);
        var expected = Outcome(() => ServerRequestEnvelopeReader.Read(
            destinationSession, decoded, flags, 65536, Clock, first.RpcDeadline));
        var actual = Outcome(() => ServerRequestEnvelopeReader.ReadDecoded(
            destinationSession, decoded, encoded, in first, flags, 65536, Clock));
        Ensure(expected.StartsWith("error:", StringComparison.Ordinal), "missing capability must fail");
        Ensure(expected == actual, "fallback must preserve the capability error");
    }

    private static RpcSession CreateSession(ProtocolV2Capabilities capabilities)
    {
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "metadata-reuse", new Pipe().Reader, new Pipe().Writer,
            RpcSessionTestFixture.ServerOptions(), completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(session, capabilities);
        return session;
    }

    private static int Validate(RpcSession session)
    {
        int count=0;
        foreach(var m in new[]{0,1,8}) foreach(var timed in new[]{false,true})
        {
            var flags=(m>0?ProtocolV2FrameFlags.HasMetadata:0)|(timed?ProtocolV2FrameFlags.HasTimeBudget:0);
            var bytes=Build(m,timed,8);
            var whole=ServerRequestEnvelopeReader.Read(session,new(bytes),flags,65536,Clock);
            for(int n=0;n<=bytes.Length;n++)
            {
                var cropped=bytes[..n];
                foreach(int split in new[]{-1,0,n/2,n})
                {
                    var seq=split<0?new ReadOnlySequence<byte>(cropped):Split(cropped,split);
                    var a=Outcome(()=>ServerRequestEnvelopeReader.Read(session,seq,flags,65536,Clock,whole.RpcDeadline));
                    var b=Outcome(()=>ServerRequestEnvelopeReader.ReadDecoded(session,seq,new(bytes),in whole,flags,65536,Clock));
                    Ensure(a==b,"decoded/plain/optional/truncated differential outcomes"); count++;
                }
            }
            var encoded=Build(m,timed,20); var decoded=Build(m,timed,64);
            var original=ServerRequestEnvelopeReader.Read(session,new(encoded),flags,65536,Clock);
            for(int split=-1;split<Math.Min(encoded.Length,40);split++)
            {
                var encodedSeq=split<0?new ReadOnlySequence<byte>(encoded):Split(encoded,split);
                var a=Outcome(()=>ServerRequestEnvelopeReader.Read(session,new(decoded),flags,65536,Clock,original.RpcDeadline));
                var b=Outcome(()=>ServerRequestEnvelopeReader.ReadDecoded(session,new(decoded),encodedSeq,in original,flags,65536,Clock));
                Ensure(a==b,"decoded rebind across source layouts");count++;
            }
            // Mutated routing, budget or metadata must fall back to the exact old parser.
            int prefix=encoded.Length-(int)original.Arguments.Length;
            for(int at=0;at<prefix;at++)
            {
                byte old=decoded[at];decoded[at]^=0xff;
                var a=Outcome(()=>ServerRequestEnvelopeReader.Read(session,new(decoded),flags,65536,Clock,original.RpcDeadline));
                var b=Outcome(()=>ServerRequestEnvelopeReader.ReadDecoded(session,new(decoded),new(encoded),in original,flags,65536,Clock));
                Ensure(a==b,"changed-prefix fallback");count++; decoded[at]=old;
            }
            if(m>0)
            {
                var limitedExpected=Outcome(()=>ServerRequestEnvelopeReader.Read(session,new(decoded),flags,0,Clock,original.RpcDeadline));
                var limitedActual=Outcome(()=>ServerRequestEnvelopeReader.ReadDecoded(session,new(decoded),new(encoded),in original,flags,0,Clock));
                Ensure(limitedExpected==limitedActual,"new metadata limit must still apply");count++;
                var reused=ServerRequestEnvelopeReader.ReadDecoded(session,new(decoded),new(encoded),in original,flags,65536,Clock);
                Ensure(ReferenceEquals(reused.Metadata,original.Metadata),"must reuse already immutable metadata");
                Ensure(reused.RpcDeadline.Equals(original.RpcDeadline),"must retain exact deadline");
                decoded[^1]=55; Ensure(reused.Arguments.ToArray()[^1]==55,"arguments must alias decoded owner, not encoded owner");count+=3;
            }
        }
        return count;
    }
    private static string Outcome(Func<ServerRequestEnvelope> f)
    {
        try { var e=f(); return $"{e.InterfaceHash}:{e.MethodHash}:{e.RpcDeadline.Timestamp}:"+
            string.Join(";",e.Metadata?.Select(x=>$"{x.Key}={x.Value}")??[])+":"+Convert.ToHexString(e.Arguments.ToArray()); }
        catch(Exception e) { return $"error:{e.GetType()}:{(e as SharpLinkException)?.Code}:{e.Message}"; }
    }
    private static byte[] Build(int entries,bool timed,int argumentBytes)
    {
        var w=new ArrayBufferWriter<byte>();var p=w.GetSpan(16);BinaryPrimitives.WriteInt64LittleEndian(p,123);BinaryPrimitives.WriteInt64LittleEndian(p[8..],456);w.Advance(16);
        if(timed){p=w.GetSpan(8);BinaryPrimitives.WriteInt64LittleEndian(p,TimeSpan.TicksPerSecond);w.Advance(8);}
        if(entries>0)
        {
            var pairs=Enumerable.Range(0,entries).Select(i=>new KeyValuePair<string,string>("key"+i,"tenant-value-"+i)).ToArray();
            var mw=new ArrayBufferWriter<byte>();ProtocolV2PayloadCodec.WriteMetadata(mw,new SharpLinkMetadata(pairs));
            ProtocolV2PayloadCodec.WriteVarUInt32(w,(uint)mw.WrittenCount);w.Write(mw.WrittenSpan);
        }
        w.Write(new byte[argumentBytes]);return w.WrittenSpan.ToArray();
    }
    private static void Ensure(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static ReadOnlySequence<byte> Split(byte[] bytes,int at){var a=new Seg(bytes.AsMemory(0,at));var b=a.Append(bytes.AsMemory(at));return new(a,0,b,b.Memory.Length);}
    private sealed class Seg:ReadOnlySequenceSegment<byte>{public Seg(ReadOnlyMemory<byte> m)=>Memory=m;public Seg Append(ReadOnlyMemory<byte> m){var n=new Seg(m){RunningIndex=RunningIndex+Memory.Length};Next=n;return n;}}
    private sealed class FixedClock:TimeProvider{public override long TimestampFrequency=>1_000_000_000;public override long GetTimestamp()=>123456;public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UnixEpoch;}
}
