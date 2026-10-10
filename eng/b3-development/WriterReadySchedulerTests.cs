using System.Linq;
using System.Reflection;
using System.IO.Pipelines;
using System.Collections.Concurrent;
using SharpLink.Abstractions;
using SharpLink.Runtime;

namespace SharpLink.UnitTests.Runtime;

public sealed class WriterReadySchedulerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static WriterReadyStreamScheduler New(int window=4, int connection=8, int streams=8,
        int slots=16, int bytes=8192, int quantum=16, Action? signal=null)
        => new(signal ?? (() => { }), p => p.Dispose(), window, connection, streams, slots, bytes, 1024, 1024, quantum);
    private sealed class Admission(bool allowed=true) : IWriterReadyAdmission
    {
        internal int Reserves;
        public bool TryReserve(int bytes) { if (allowed) Reserves++; return allowed; }
    }
    private static WriterReadyFrame Take(WriterReadyStreamScheduler source)
    {
        Check(source.TryTake(new Admission(), out var frame), "expected ready DATA");
        return frame;
    }
    private static void Poll(WriterReadyStreamScheduler source)
        => Check(!source.TryTake(new Admission(false), out _), "denied pump budget cannot transfer a frame");
    private static void Release(WriterReadyFrame frame, Exception? error=null)
    { frame.Packet.Dispose(); frame.Completion.Complete(frame.CreditBytes, error); }
    private static async Task ExpectError(ValueTask pending, Exception? expected=null)
    {
        Exception? caught=null;
        try { await pending.AsTask().WaitAsync(Limit); } catch (Exception e) { caught=e; }
        Check(caught is not null, "expected failed operation");
        if (expected is not null) Check(ReferenceEquals(expected,caught), "original error identity changed");
    }

    [Test]
    public async Task PreparationDoesNotDebitAndWriterTakeDoes()
    {
        var s=New(connection:4); var h=s.Open(1,0); var p=new Packet(1);
        await s.EnqueueAsync(h,p,2);
        Check(s.ConnectionCredit==4 && h.Credit==4, "producer debited credit");
        var frame=Take(s);
        Check(s.ConnectionCredit==2 && h.Credit==2, "writer did not debit both accounts");
        Release(frame); s.ApplyWindowUpdate(1,0,2); Poll(s);
        Check(s.ConnectionCredit==4 && p.Returns==1, "settlement mismatch");
        s.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task DeniedQueueBudgetDoesNotDebitOrDetach()
    {
        var s=New(); var h=s.Open(1,0); var p=new Packet(1);
        await s.EnqueueAsync(h,p,4); Poll(s);
        Check(s.ConnectionCredit==8 && h.Credit==4 && p.Returns==0 && h.Frames.Count==1,"budget miss lost ownership");
        Release(Take(s)); s.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task FinishWaitsForDataAndActualWriterReleaseNotPeerCredit()
    {
        var s=New(connection:4); var h=s.Open(1,0);
        await s.EnqueueAsync(h,new Packet(1),2); await s.EnqueueAsync(h,new Packet(2),2);
        var finish=s.FinishAsync(h); Check(!finish.IsCompleted,"queued DATA did not hold drain");
        var a=Take(s); var b=Take(s);
        Check(!finish.IsCompleted,"transport pins did not hold drain");
        Release(a); Check(!finish.IsCompleted,"one remaining pin was ignored");
        Release(b); await finish.WaitAsync(Limit);
        Check(s.ConnectionCredit==0,"drain fabricated peer credit");
        s.Complete(h); Poll(s); Check(s.RetainedStreams==1,"debt tombstone disappeared");
        s.ApplyWindowUpdate(1,0,4); Poll(s); Check(s.RetainedStreams==0,"fully settled stream did not retire");
        s.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task PrematureCleanTerminalCannotDiscardData()
    {
        var s=New(); var h=s.Open(1,0); var p=new Packet(1); await s.EnqueueAsync(h,p,1);
        bool rejected=false; try { s.Complete(h); } catch(InvalidOperationException) { rejected=true; }
        Check(rejected && p.Returns==0,"premature terminal discarded DATA");
        Release(Take(s)); s.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task ConnectionBlockedHeadCannotBeBypassedBySmallerData()
    {
        var s=New(connection:5); var a=s.Open(1,0); var b=s.Open(2,0); var c=s.Open(3,0);
        await s.EnqueueAsync(a,new Packet(1),4); Release(Take(s));
        await s.EnqueueAsync(b,new Packet(2),3); await s.EnqueueAsync(c,new Packet(3),1);
        Check(!s.TryTake(new Admission(),out _),"smaller stream bypassed connection-credit head");
        s.ApplyWindowUpdate(1,0,2); var frame=Take(s);
        Check(frame.Packet.WrittenSpan[0]==2 && s.ConnectionCredit==0,"wrong waiter admitted");
        Release(frame); s.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task StreamLocalBlockedHeadDoesNotStrandOtherStreams()
    {
        var s=New(); var a=s.Open(1,0); var b=s.Open(2,0);
        await s.EnqueueAsync(a,new Packet(1),4); Release(Take(s));
        await s.EnqueueAsync(a,new Packet(2),1); await s.EnqueueAsync(b,new Packet(3),1);
        var frame=Take(s); Check(frame.Packet.WrittenSpan[0]==3,"stream-local block stranded eligible DATA");
        Release(frame); s.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task LateAbortedUpdateCannotFundUnrelatedStream()
    {
        var s=New(connection:4); var a=s.Open(1,0); var b=s.Open(2,0); var c=s.Open(3,0);
        await s.EnqueueAsync(a,new Packet(1),4); var first=Take(s);
        s.Abort(a,new OperationCanceledException()); Poll(s);
        await s.EnqueueAsync(b,new Packet(2),4); var second=Take(s);
        s.ApplyWindowUpdate(1,0,4); await s.EnqueueAsync(c,new Packet(3),1);
        Check(!s.TryTake(new Admission(),out _) && s.ConnectionCredit==0,"late credit was counted twice");
        Release(first); Release(second); s.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task OversizedItemBorrowsOnceAndRepaysExactly()
    {
        var s=New(window:2,connection:2); var a=s.Open(1,0);
        await s.EnqueueAsync(a,new Packet(1),6); Release(Take(s));
        Check(s.ConnectionCredit==-4 && a.Credit==-4,"oversized debt lost");
        await s.EnqueueAsync(a,new Packet(2),1);
        Check(!s.TryTake(new Admission(),out _),"second borrow was admitted");
        s.ApplyWindowUpdate(1,0,7); Release(Take(s));
        Check(s.ConnectionCredit==1 && a.Credit==1,"oversized/excess refund wrong");
        s.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task EarlyCreditCannotReleaseBufferEpoch()
    {
        var s=New(streams:1); var a=s.Open(1,0); var p=new Packet(1);
        await s.EnqueueAsync(a,p,4); var frame=Take(s);
        s.ApplyWindowUpdate(1,0,4); Poll(s);
        var reason=new IOException("abort"); s.Abort(a,reason); s.Complete(a); Poll(s);
        Check(s.RetainedStreams==1 && p.Returns==0,"early credit recycled a pinned buffer");
        bool rejected=false; try { s.Open(1,0); } catch(InvalidOperationException) { rejected=true; }
        Check(rejected,"same key reused before release"); Release(frame);
        Check(s.RetainedStreams==0,"released closed stream retained forever");
        var b=s.Open(1,0); Check(!ReferenceEquals(a,b),"completion epoch reused");
        var stale=new Packet(9); await ExpectError(s.EnqueueAsync(a,stale,1),reason);
        Check(stale.Returns==1 && b.Credit==4,"stale handle reached replacement");
        s.Stopped(reason);
    }

    [Test]
    public async Task CanceledCapacityWaitReturnsOnlyUnpublishedPacket()
    {
        var s=New(slots:1); var h=s.Open(1,0); var a=new Packet(1); var b=new Packet(2);
        await s.EnqueueAsync(h,a,1); using var cts=new CancellationTokenSource();
        var waiting=s.EnqueueAsync(h,b,1,cts.Token); Check(!waiting.IsCompleted,"capacity wait missing");
        cts.Cancel(); await ExpectError(waiting);
        Check(a.Returns==0 && b.Returns==1 && h.Frames.Count==1,"cancel stole or leaked packet");
        s.Stopped(new OperationCanceledException()); Check(a.Returns==1,"stop failed to return prepared packet");
    }

    [Test]
    public async Task StreamAbortUnblocksCapacityWithOriginalError()
    {
        var s=New(slots:1); var h=s.Open(1,0); var a=new Packet(1); var b=new Packet(2);
        await s.EnqueueAsync(h,a,1); var pending=s.EnqueueAsync(h,b,1);
        var error=new IOException("exact abort"); s.Abort(h,error); await ExpectError(pending,error);
        Check(a.Returns==1 && b.Returns==1,"abort leaked prepared or unpublished packet");
        s.Complete(h); Poll(s); Check(s.RetainedStreams==0,"abort retained empty lifecycle"); s.Stopped(error);
    }

    [Test]
    public async Task ReadyNotificationIsCoalescedForBusyStream()
    {
        int signals=0; var s=New(window:100,connection:100,signal:()=>Interlocked.Increment(ref signals)); var h=s.Open(1,0);
        for(int i=0;i<16;i++) await s.EnqueueAsync(h,new Packet(1),1);
        Check(signals==1,"preparation queued per-frame notifications");
        var f=Take(s); await s.EnqueueAsync(h,new Packet(2),1);
        Check(signals==1,"already-ready stream signaled per frame");
        Release(f); s.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task PreparedByteBoundAllowsOnlyOneOversizedPacket()
    {
        var s=New(bytes:24); var h=s.Open(1,0); var a=new Packet(1,32); var b=new Packet(2);
        await s.EnqueueAsync(h,a,1); var pending=s.EnqueueAsync(h,b,1);
        Check(!pending.IsCompleted && h.PreparedBytes==32,"prepared byte bound exceeded");
        Release(Take(s)); await pending.AsTask().WaitAsync(Limit);
        Check(h.PreparedBytes==24,"preparation accounting wrong"); s.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task RejectedForeignAndInvalidFramesAreReturned()
    {
        var a=New(); var b=New(); var h=a.Open(1,0); var p=new Packet(1);
        await ExpectError(b.EnqueueAsync(h,p,1)); Check(p.Returns==1,"foreign packet leaked");
        var invalid=new Packet(2); await ExpectError(a.EnqueueAsync(h,invalid,0));
        Check(invalid.Returns==1 && a.ConnectionCredit==8,"invalid frame changed ledger");
        a.Stopped(new OperationCanceledException()); b.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task ReleaseFailureStillAllowsStopToDiscardPreparedTail()
    {
        var s=New(); var h=s.Open(1,0); var a=new Packet(1); var b=new Packet(2);
        await s.EnqueueAsync(h,a,1); await s.EnqueueAsync(h,b,1);
        var f=Take(s); var error=new IOException("failed flush"); Release(f,error); s.Stopped(error);
        Check(a.Returns==1 && b.Returns==1 && s.RetainedStreams==0,"poisoned stream skipped preparation cleanup");
    }

    [Test]
    public async Task OneHundredThousandSameKeyLifecyclesDoNotCrossTalk()
    {
        var s=New(streams:1);
        for(int i=0;i<100000;i++)
        {
            var h=s.Open(1,0); var p=new Packet(1); await s.EnqueueAsync(h,p,1); Release(Take(s));
            await s.FinishAsync(h); s.Complete(h); s.ApplyWindowUpdate(1,0,1); Poll(s);
            Check(s.RetainedStreams==0 && s.ConnectionCredit==8 && p.Returns==1,"lifecycle conservation failed");
        }
        s.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task SchedulingQuantumBoundsConsecutiveFrames()
    {
        var s=New(window:100,connection:200,quantum:2); var a=s.Open(1,0); var b=s.Open(2,0);
        for(int i=0;i<6;i++) { await s.EnqueueAsync(a,new Packet(1),1); await s.EnqueueAsync(b,new Packet(2),1); }
        var seen=new List<byte>();
        for(int i=0;i<12;i++) { var f=Take(s); seen.Add(f.Packet.WrittenSpan[0]); Release(f); }
        Check(seen.SequenceEqual(new byte[]{1,1,2,2,1,1,2,2,1,1,2,2}),"quantum ordering wrong");
        s.Stopped(new OperationCanceledException());
    }

    [Test]
    public async Task ActualPumpDrainCannotCompleteWhileFlushIsBlocked()
    {
        await using var rig=new Rig(); var s=rig.CreateScheduler(); var h=s.Open(1,0); var p=new Packet(1);
        await s.EnqueueAsync(h,p,1); rig.Attach(s); await rig.Output.Entered.Task.WaitAsync(Limit);
        var finish=s.FinishAsync(h); Check(!finish.IsCompleted && p.Returns==0,"blocked flush lost ownership");
        rig.Output.Release(); await finish.WaitAsync(Limit);
        Check(p.Returns==1 && rig.QueuedBytes==0,"actual pump did not settle");
    }

    [Test]
    public async Task ActualPumpAbortReturnsPreparedTailButNotBlockedFrame()
    {
        await using var rig=new Rig(); var s=rig.CreateScheduler(); var h=s.Open(1,0); var a=new Packet(1); var b=new Packet(2);
        await s.EnqueueAsync(h,a,1); await s.EnqueueAsync(h,b,1); rig.Attach(s);
        await rig.Output.Entered.Task.WaitAsync(Limit); s.Abort(h,new IOException("cancel"));
        Check(a.Returns==0 && b.Returns==1,"cancel stole writer-owned frame");
        rig.Stop(); rig.Output.Release(); await rig.Join().WaitAsync(Limit);
        Check(a.Returns==1 && b.Returns==1 && rig.QueuedBytes==0,"actual abort settlement wrong");
    }

    [Test]
    public async Task ActualPumpFlushFailureReturnsPreparedTail()
    {
        await using var rig=new Rig(); var s=rig.CreateScheduler(); var h=s.Open(1,0); var a=new Packet(1); var b=new Packet(2);
        await s.EnqueueAsync(h,a,1); await s.EnqueueAsync(h,b,1); rig.Attach(s);
        await rig.Output.Entered.Task.WaitAsync(Limit); rig.Output.Release(new IOException("flush fault"));
        await rig.Join().WaitAsync(Limit);
        Check(a.Returns==1 && b.Returns==1 && rig.QueuedBytes==0 && s.RetainedStreams==0,"flush fault leaked ownership");
    }

    [Test]
    public void ExistingControllerLateRefundDoesNotFundAnotherStream()
    {
        var c=new StreamFlowController(4,4,1024); c.TryAcquireSendCredit(1,0,4);
        c.AbortSendStreams(1,new OperationCanceledException()); c.TryAcquireSendCredit(2,0,4);
        c.ApplyWindowUpdate(1,0,4); Check(c.SendConnectionCredit==0,"existing controller minted credit");
        c.ReturnUnsentCredit(2,0,4); Check(c.SendConnectionCredit==4,"first legitimate refund failed");
    }

    [Test]
    public void ExistingControllerExcessRefundUsesEffectiveDelta()
    {
        var c=new StreamFlowController(4,8,1024); c.TryAcquireSendCredit(1,0,2); c.TryAcquireSendCredit(2,0,4);
        c.ApplyWindowUpdate(1,0,int.MaxValue); Check(c.SendConnectionCredit==4,"excess delta reached connection account");
    }

    private sealed class Packet : IRpcByteBufferWriter
    {
        private readonly byte[] _bytes;
        internal Packet(byte marker,int length=24) { _bytes=new byte[length]; _bytes[0]=marker; }
        internal int Returns;
        public int WrittenCount=>_bytes.Length;
        public int Capacity=>_bytes.Length;
        public ReadOnlyMemory<byte> WrittenMemory=>_bytes;
        public Span<byte> WrittenSpan=>_bytes;
        public void Advance(int count)=>throw new NotSupportedException();
        public Memory<byte> GetMemory(int sizeHint=0)=>throw new NotSupportedException();
        public Span<byte> GetSpan(int sizeHint=0)=>throw new NotSupportedException();
        public void Clear()=>throw new NotSupportedException();
        public void Dispose() { Check(Interlocked.Increment(ref Returns)==1,"packet returned twice"); }
    }
    private sealed class Output : PipeWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer=new();
        private readonly TaskCompletionSource<FlushResult> _flush=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _flushes;
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release(Exception? error=null)
        { if(error is null) _flush.TrySetResult(new(false,false)); else _flush.TrySetException(error); }
        public override void Advance(int bytes) { _buffer.Advance(bytes); _buffer.Clear(); }
        public override Memory<byte> GetMemory(int sizeHint=0)=>_buffer.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint=0)=>_buffer.GetSpan(sizeHint);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken=default)
        {
            if(Interlocked.Increment(ref _flushes)==1) { Entered.TrySetResult(); return new(_flush.Task); }
            return new(new FlushResult(false,false));
        }
        public override void CancelPendingFlush()=>Release();
        public override void Complete(Exception? exception=null) { }
    }
    private sealed class Rig : IAsyncDisposable
    {
        private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        private static readonly Type PumpType=typeof(RpcSession).GetNestedType("SendPump",BindingFlags.NonPublic)!;
        private readonly object _pump;
        internal readonly Output Output=new();
        internal Rig()
        {
            var policy=RpcSessionFlushPolicyState.Create(null,SharpLinkPerformanceProfile.LowLatency);
            _pump=Activator.CreateInstance(PumpType,Flags,null,
                [Output,policy,1024*1024,TimeProvider.System,CancellationToken.None,
                 (Action<IRpcByteBufferWriter>)(p=>p.Dispose()),(Action<Exception>)(_=>{})],null)!;
        }
        private object? Call(string name,params object?[] args)
            =>PumpType.GetMethods(Flags).Single(m=>m.Name==name && m.GetParameters().Length==args.Length).Invoke(_pump,args);
        internal WriterReadyStreamScheduler CreateScheduler()=>New(signal:()=>Call("SignalWriterReadySource"));
        internal void Attach(IWriterReadySource source)=>Call("AttachWriterReadySource",source);
        internal void Stop()=>Call("Stop");
        internal Task Join()=>((ValueTask)Call("WaitForStopAsync")!).AsTask();
        internal long QueuedBytes=>(long)PumpType.GetProperty("QueuedBytes",Flags)!.GetValue(_pump)!;
        public async ValueTask DisposeAsync() { Stop(); Output.Release(); await Join().WaitAsync(Limit); }
    }
}
