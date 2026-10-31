// Authored reproduction probes. These call the actual runtime, not a copied pump.
// Timing is characterization only; preserve every sample and baseline failure.
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpLink.Abstractions;
using SharpLink.Runtime;

class Program
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes).GetFields().Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
    static string N(string s) => Regex.Replace(s, @"d__\d+", "d__N");
    static string Signature(MemberInfo m) => N((m.DeclaringType?.FullName ?? "") + ":" + m);
    static void Main(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException("Usage: structure|witness|late-recovery|held-cleanup OUTPUT; fast OUTPUT WARMUP OPS; saturated OUTPUT WARMUP BATCHES CONTENDERS CANCELLABLE CANCEL_BLOCKED");
        if (args[0] == "held-cleanup") { HeldCleanup(args[1]); return; }
        if (args[0] == "late-recovery") { LateRecovery(args[1]); return; }
        if (args[0] == "witness") { Witness(args[1]); return; }
        if (args[0] == "structure") { Structure(args[1]); return; }
        if (args[0] == "fast") { Fast(args[1], int.Parse(args[2]), int.Parse(args[3])); return; }
        if (args[0] == "saturated") { Saturated(args[1], int.Parse(args[2]), int.Parse(args[3]), args.Length > 4 ? int.Parse(args[4]) : 16, args.Length > 5 && bool.Parse(args[5]), args.Length > 6 && bool.Parse(args[6])); return; }
        throw new ArgumentException("structure output; fast output warmup operations; saturated output warmup batches");
    }
    static void Write(string path, object report) => File.WriteAllText(path, JsonSerializer.Serialize(report, Json));
    static string[] IL(MethodBase method)
    {
        byte[] bytes = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var result = new List<string>(); int pos = 0;
        while (pos < bytes.Length)
        {
            int start = pos; ushort value = bytes[pos++]; if (value == 0xfe) value = (ushort)(0xfe00 | bytes[pos++]); var op = Opcodes[value];
            string arg = ""; int token;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineI: arg = ((sbyte)bytes[pos++]).ToString(); break;
                case OperandType.ShortInlineVar: arg = bytes[pos++].ToString(); break;
                case OperandType.InlineVar: arg = BitConverter.ToUInt16(bytes, pos).ToString(); pos += 2; break;
                case OperandType.InlineI: arg = BitConverter.ToInt32(bytes, pos).ToString(); pos += 4; break;
                case OperandType.InlineI8: arg = BitConverter.ToInt64(bytes, pos).ToString(); pos += 8; break;
                case OperandType.ShortInlineR: arg = BitConverter.ToSingle(bytes, pos).ToString("R"); pos += 4; break;
                case OperandType.InlineR: arg = BitConverter.ToDouble(bytes, pos).ToString("R"); pos += 8; break;
                case OperandType.ShortInlineBrTarget: arg = $"IL_{pos + 1 + (sbyte)bytes[pos]:x4}"; pos++; break;
                case OperandType.InlineBrTarget: arg = $"IL_{pos + 4 + BitConverter.ToInt32(bytes, pos):x4}"; pos += 4; break;
                case OperandType.InlineSwitch:
                    int count = BitConverter.ToInt32(bytes, pos); pos += 4; int end = pos + 4 * count; var cases = new List<string>(); for (int i = 0; i < count; i++) { cases.Add($"IL_{end + BitConverter.ToInt32(bytes, pos):x4}"); pos += 4; }
                    arg = string.Join(",", cases); break;
                default:
                    token = BitConverter.ToInt32(bytes, pos); pos += 4;
                    try { arg = op.OperandType == OperandType.InlineString ? JsonSerializer.Serialize(method.Module.ResolveString(token)) : op.OperandType == OperandType.InlineSig ? Convert.ToHexString(method.Module.ResolveSignature(token)) : Signature(method.Module.ResolveMember(token, method.DeclaringType?.GetGenericArguments(), method.IsGenericMethod ? method.GetGenericArguments() : null)!); }
                    catch { arg = $"token:0x{token:x8}"; }
                    break;
            }
            result.Add($"IL_{start:x4} {op.Name} {arg}".TrimEnd());
        }
        return result.ToArray();
    }
    static object Method(MethodInfo m)
    {
        var b = m.GetMethodBody(); var bytes = b?.GetILAsByteArray() ?? [];
        return new { signature = Signature(m), ilLength = bytes.Length, ilHex = Convert.ToHexString(bytes), sha256 = Convert.ToHexString(SHA256.HashData(bytes)), locals = b?.LocalVariables.Select(v => new { v.LocalIndex, type = N(v.LocalType.ToString()), v.IsPinned }), exceptionClauses = b?.ExceptionHandlingClauses.Select(c => new { kind = c.Flags.ToString(), c.TryOffset, c.TryLength, c.HandlerOffset, c.HandlerLength }), il = IL(m) };
    }
    static long FieldOffset(object target, FieldInfo field)
    {
        var dm = new DynamicMethod("offset", typeof(long), [typeof(object)], typeof(Program), true); var il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, field.DeclaringType!); il.Emit(OpCodes.Ldflda, field); il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Ret);
        return ((Func<object, long>)dm.CreateDelegate(typeof(Func<object, long>)))(target);
    }
    static long ObjectBytes(Type type)
    {
        for (int i = 0; i < 100; i++) GC.KeepAlive(RuntimeHelpers.GetUninitializedObject(type));
        long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) GC.KeepAlive(RuntimeHelpers.GetUninitializedObject(type)); return (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
    }
    static int ValueBytes<T>() => Unsafe.SizeOf<T>();
    static object Layout(Type type)
    {
        object? obj = type.IsValueType ? null : RuntimeHelpers.GetUninitializedObject(type);
        return new { type = N(type.FullName!), isValueType = type.IsValueType, allocatedObjectBytes = type.IsValueType ? (long?)null : ObjectBytes(type), valueBytes = type.IsValueType ? (int?)typeof(Program).GetMethod(nameof(ValueBytes), All)!.MakeGenericMethod(type).Invoke(null, null) : null, fields = type.GetFields(All).Where(f => !f.IsStatic).Select(f => new { f.Name, type = N(f.FieldType.FullName ?? f.FieldType.ToString()), offsetFromObjectReference = obj == null ? (long?)null : FieldOffset(obj, f) }) };
    }
    static void Structure(string output)
    {
        var session = typeof(RpcSession); var pump = session.GetNestedType("SendPump", All)!;
        var targets = new HashSet<string> { "SendPacketWithBackpressureAsync", "SendPacket", "AwaitBackpressureEnqueueAsync", "CaptureBackpressuredSendDrain", "GetOrCreatePumpOrReturn", "CreateFrame", "PrepareOutboundPacket", "ValidateOutboundPacketOrReturn", "TryEnqueue", "TryEnqueueForBackpressure", "TryReserve", "ReserveAsync", "EnqueueAsync", "EnterBackpressuredSend", "ExitBackpressuredSend", "PulseCapacityWaiters", "EnqueueWhenFull", "CompletePendingEnqueue", "CancelPendingEnqueue", "ResumePendingEnqueues", "ReleaseBatch", "CompleteReserved", "RunAsync", "FlushAndReleaseAsync" };
        var methods = new[] { session, pump }.SelectMany(t => t.GetMethods(All)).Where(m => targets.Contains(m.Name)).ToList();
        var stateMachines = methods.Select(m => m.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType).Where(t => t != null).Cast<Type>().ToArray();
        methods.AddRange(stateMachines.Select(t => t.GetMethod("MoveNext", All)!));
        Write(output, new { runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), assembly = typeof(RpcSession).Assembly.Location, assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RpcSession).Assembly.Location))), methods = methods.OrderBy(Signature).Select(Method), layouts = new[] { session, pump, typeof(OwnedFrame) }.Concat(stateMachines).Concat(pump.GetNestedTypes(All).Where(t => t.Name is "PendingEnqueue" or "PendingEnqueueQueue")).Select(Layout) });
    }
    static RpcSession Session(SharpLinkRuntimeContext context, PipeWriter writer)
    {
        var session = new RpcSession(new Transport(writer), new RpcSessionCreationOptions(RpcSessionRole.Server, context, new RpcSessionFlushOptions(1, TimeSpan.FromMilliseconds(1))));
        session.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion, ProtocolV2Capabilities.None, context.Protocol.MaxFramePayloadBytes, context.FlowControl.StreamReceiveWindowBytes, context.FlowControl.ConnectionReceiveWindowBytes));
        return session;
    }
    static IRpcByteBufferWriter Frame(RpcSession session, int payload = 32, ulong id = 1)
    {
        var w = session.RuntimeContext.Buffers.Rent(); using (w.BeginPacketScope(ProtocolV2FrameType.Response, ProtocolV2FrameFlags.None, id)) { w.GetSpan(payload).Slice(0, payload).Clear(); w.Advance(payload); }
        return w;
    }
    static void Wait(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (!condition()) { if (Stopwatch.GetTimestamp() > deadline) throw new TimeoutException("harness did not quiesce"); Thread.SpinWait(32); Thread.Yield(); }
    }
    static void WaitSession(RpcSession session)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (session.QueuedSendBytes != 0 || !session.HasPendingSendPumpIdleWait) { if (Stopwatch.GetTimestamp() > deadline) throw new TimeoutException("session not idle"); Thread.SpinWait(32); Thread.Yield(); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void FastOnce(RpcSession s)
    {
        var v = s.SendPacketWithBackpressureAsync(Frame(s)); if (!v.IsCompletedSuccessfully) throw new Exception("fast scenario left immediate accepted branch");
        WaitSession(s);
    }
    static void Fast(string path, int warmup, int operations)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var writer = new SinkWriter(); var s = Session(context, writer);
        for (int i = 0; i < warmup; i++) FastOnce(s);
        Thread.Sleep(500);
        var samples = new List<object>();
        for (int batch = 0; batch < 8; batch++)
        {
            long totalBefore = GC.GetTotalAllocatedBytes(true), threadBefore = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            for (int i = 0; i < operations; i++) FastOnce(s);
            long stop = Stopwatch.GetTimestamp(), thread = GC.GetAllocatedBytesForCurrentThread() - threadBefore, total = GC.GetTotalAllocatedBytes(true) - totalBefore;
            samples.Add(new { batch, operations, threadAllocatedBytes = thread, processAllocatedBytes = total, threadBytesPerOp = thread / (double)operations, processBytesPerOp = total / (double)operations, elapsedNs = (stop - start) * 1e9 / Stopwatch.Frequency, nsPerOp = (stop - start) * 1e9 / Stopwatch.Frequency / operations });
        }
        s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Write(path, new { scenario = "accepted normal response; real asynchronous pump, sink writer, wait for empty+idle; producer includes frame rent/build/idle wait; process includes pump", warmup, operations, runtime = RuntimeInformation.FrameworkDescription, samples });
    }
    static void HeldCleanup(string path)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Configure(o => o.FlowControl.MaxSendQueueBytes = 1024).Build(includeGeneratedAssemblyCatalog: false);
        var writer = new GateWriter(); var s = Session(context, writer); using var cts = new CancellationTokenSource();
        writer.Arm(); s.SendPacket(Frame(s, 800)); Wait(() => writer.Blocked);
        var held = new HeldReturnWriter(Frame(s, 800), context.Buffers);
        var cancelled = s.SendPacketWithBackpressureAsync(held, cts.Token);
        if (cancelled.IsCompleted) throw new Exception("expected Full");
        var cancelThread = new Thread(cts.Cancel) { IsBackground = true }; cancelThread.Start(); Wait(() => held.Entered);
        writer.Release(); WaitSession(s);
        var pump = typeof(RpcSession).GetField("_pump", All)!.GetValue(s)!;
        var slot = pump.GetType().GetField("_capacityChanged", All)!;
        var gate = (System.Threading.Lock)pump.GetType().GetField("_admissionGate", All)!.GetValue(pump)!;
        var samples = new List<object>();
        for (int i = 0; i < 10; i++)
        {
            var send = s.SendPacketWithBackpressureAsync(Frame(s)); if (!send.IsCompletedSuccessfully) throw new Exception("ordinary send not accepted"); WaitSession(s);
            var queue = slot.GetValue(pump); var head = queue?.GetType().GetField("Head", All)?.GetValue(queue);
            samples.Add(new { i, slotNull = queue is null, linkedCompleting = head?.GetType().GetField("Completing", All)?.GetValue(head), cancelPending = !cancelled.IsCompleted, queued = s.QueuedSendBytes, idle = s.HasPendingSendPumpIdleWait });
        }
        bool idleUnderGate, ordinaryAccepted; long bytesUnderGate;
        using (gate.EnterScope())
        {
            var send = s.SendPacketWithBackpressureAsync(Frame(s)); ordinaryAccepted = send.IsCompletedSuccessfully; if (!ordinaryAccepted) throw new Exception("ordinary send blocked before release");
            Wait(() => s.QueuedSendBytes == 0); Thread.Sleep(100);
            idleUnderGate = s.HasPendingSendPumpIdleWait; bytesUnderGate = s.QueuedSendBytes;
        }
        WaitSession(s); bool idleAfterGate = true;
        held.Release(); if (!cancelThread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("cancellation callback join"); try { cancelled.AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); throw new Exception("expected canceled result"); } catch (OperationCanceledException) { }
        s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Write(path, new { scenario = "Canceled Full writer Return/Dispose held; all real capacity waiters gone; ordinary accepted sends release; actual admission gate held for final ordinary release", samples, ordinaryAccepted, idleUnderGate, bytesUnderGate, idleAfterGate });
    }
    sealed class HeldReturnWriter(IRpcByteBufferWriter inner, IRpcBufferWriterPool pool) : IRpcByteBufferWriter
    {
        readonly ManualResetEventSlim _release = new(false); int _entered;
        public bool Entered => Volatile.Read(ref _entered) != 0;
        public void Release() => _release.Set();
        public int WrittenCount => inner.WrittenCount; public ReadOnlyMemory<byte> WrittenMemory => inner.WrittenMemory; public Span<byte> WrittenSpan => inner.WrittenSpan; public int Capacity => inner.Capacity;
        public void Advance(int count) => inner.Advance(count); public Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint); public Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint); public void Clear() => inner.Clear();
        public void Dispose() { Volatile.Write(ref _entered, 1); if (!_release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("held return release"); pool.Return(inner); }
    }
    static void LateRecovery(string path)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Configure(o => o.FlowControl.MaxSendQueueBytes = 1024).Build(includeGeneratedAssemblyCatalog: false);
        var writer = new GateWriter(); var s = Session(context, writer); var samples = new List<object>();
        for (int batch = -20; batch < 100; batch++)
        {
            writer.Arm(); s.SendPacket(Frame(s, 800)); Wait(() => writer.Blocked);
            var frame = Frame(s, 800); var pump = typeof(RpcSession).GetField("_pump", All)!.GetValue(s)!;
            var gate = (System.Threading.Lock)pump.GetType().GetField("_admissionGate", All)!.GetValue(pump)!;
            long allocation = -1; bool synchronous = false; Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var result = s.SendPacketWithBackpressureAsync(frame);
                    synchronous = result.IsCompletedSuccessfully;
                    result.GetAwaiter().GetResult();
                    allocation = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception ex) { error = ex; }
            });
            using (gate.EnterScope())
            {
                thread.Start();
                Wait(() => (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0);
                writer.Release(); WaitSession(s);
            }
            if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("late recovery producer");
            if (error != null) throw error;
            WaitSession(s);
            if (!synchronous) throw new Exception("late capacity did not complete synchronously");
            if (batch >= 0) samples.Add(new { batch, producerAllocatedBytes = allocation, synchronous });
        }
        s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Write(path, new { scenario = "Force Full before admission gate; wait for producer ThreadState.WaitSleepJoin on held actual gate; release first flush and observe empty+idle while gate held; allow locked recheck", samples });
    }
    static void Witness(string path)
    {
        int[] order;
        using (var context = new SharpLinkRuntimeContextBuilder().Configure(o => o.FlowControl.MaxSendQueueBytes = 1024).Build(includeGeneratedAssemblyCatalog: false))
        {
            var writer = new RecordingGateWriter(); writer.Arm(); var s = Session(context, writer);
            s.SendPacket(Frame(s, 800, 1)); Wait(() => writer.Blocked);
            var old = s.SendPacketWithBackpressureAsync(Frame(s, 800, 2));
            if (old.IsCompleted) throw new Exception("expected old Full send");
            var fence = s.SendPacketAsync(Frame(s, 32, 3), waitForCapacity: true, forceFlush: true);
            writer.Release(); Task.WhenAll(old.AsTask(), fence.AsTask()).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            s.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            order = writer.Ids.ToArray(); s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        object falseEmpty;
        using (var context = new SharpLinkRuntimeContextBuilder().Configure(o => o.FlowControl.MaxSendQueueBytes = 32768).Build(includeGeneratedAssemblyCatalog: false))
        {
            var s = Session(context, new SinkWriter()); using var cts = new CancellationTokenSource();
            var old = s.SendPacketWithBackpressureAsync(Frame(s, 32768), cts.Token);
            var queuedBefore = s.QueuedSendBytes; var pendingBefore = !old.IsCompleted;
            var flush = s.FlushSendQueueAsync().AsTask(); flush.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            falseEmpty = new { queuedBefore, pendingBefore, flushCompleted = flush.IsCompletedSuccessfully, queuedAfter = s.QueuedSendBytes, pendingAfter = !old.IsCompleted };
            cts.Cancel(); try { old.AsTask().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); throw new Exception("expected cancellation"); } catch (OperationCanceledException) { }
            s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        Write(path, new { fenceWireOrder = order, falseEmpty });
    }
    static void Saturated(string path, int warmup, int batches, int contenders, bool cancellable, bool cancelWhileBlocked)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Configure(o => o.FlowControl.MaxSendQueueBytes = 1024).Build(includeGeneratedAssemblyCatalog: false);
        var writer = new GateWriter(); var s = Session(context, writer);
        var samples = new List<object>();
        for (int batch = -warmup; batch < batches; batch++)
        {
            using var cancellation = cancellable ? new CancellationTokenSource() : null;
            var token = cancellation?.Token ?? CancellationToken.None;
            writer.Arm();
            var first = s.SendPacketWithBackpressureAsync(Frame(s, 800)); if (!first.IsCompletedSuccessfully) throw new Exception("first frame was not accepted");
            Wait(() => writer.Blocked);
            var frames = new IRpcByteBufferWriter[contenders]; var waits = new ValueTask[contenders]; for (int i = 0; i < contenders; i++) frames[i] = Frame(s, 800);
            long totalBefore = GC.GetTotalAllocatedBytes(true), threadBefore = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            for (int i = 0; i < contenders; i++) { waits[i] = s.SendPacketWithBackpressureAsync(frames[i], token); if (waits[i].IsCompleted) throw new Exception("saturated branch did not suspend"); }
            long enqueued = Stopwatch.GetTimestamp(), producer = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
            int cancelled = 0;
            if (cancelWhileBlocked) cancellation!.Cancel(); else writer.Release();
            for (int i = 0; i < contenders; i++)
            {
                var deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
                while (!waits[i].IsCompleted)
                {
                    if (Stopwatch.GetTimestamp() > deadline)
                    {
                        Console.Error.WriteLine($"TIMEOUT batch={batch} contender={i} queued={s.QueuedSendBytes} idle={s.HasPendingSendPumpIdleWait} connected={s.IsConnected} completed={string.Join(",", waits.Select(v => v.IsCompleted))}");
                        var pump = typeof(RpcSession).GetField("_pump", All)!.GetValue(s)!;
                        foreach (var f in pump.GetType().GetFields(All).Where(f => f.Name is "_capacityChanged" or "_pumpTask" or "_pumpLifetime" or "_queuedBytes" or "_stopped")) Console.Error.WriteLine($"PUMP {f.Name}={f.GetValue(pump)}");
                        for (int j = 0; j < contenders; j++) if (!waits[j].IsCompleted)
                            {
                                var task = waits[j].AsTask(); Console.Error.WriteLine($"PENDING {j} type={task.GetType()} status={task.Status}");
                                foreach (var f in task.GetType().GetFields(All)) Console.Error.WriteLine($"TASK {f.Name}={f.GetValue(task)}");
                            }
                        Write(path + ".partial", new { warmup, batches, contenders, cancellable, cancelWhileBlocked, failedBatch = batch, failedContender = i, samples });
                        throw new TimeoutException("saturated pending enqueue not completed");
                    }
                    Thread.SpinWait(32); Thread.Yield();
                }
                try { waits[i].GetAwaiter().GetResult(); } catch (OperationCanceledException) when (cancelWhileBlocked) { cancelled++; }
            }
            if (cancelWhileBlocked) { if (cancelled != contenders) throw new Exception("not every Full send was cancelled"); writer.Release(); }
            WaitSession(s);
            long ended = Stopwatch.GetTimestamp(), total = GC.GetTotalAllocatedBytes(true) - totalBefore;
            Console.WriteLine($"completed batch={batch}");
            if (batch >= 0) samples.Add(new { batch, contenders, cancelled, producerAllocatedBytes = producer, processAllocatedBytes = total, producerBytesPerSend = producer / (double)contenders, processBytesPerSend = total / (double)contenders, submitNsPerSend = (enqueued - start) * 1e9 / Stopwatch.Frequency / contenders, drainNsPerSend = (ended - start) * 1e9 / Stopwatch.Frequency / contenders });
        }
        s.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Write(path, new { scenario = "queue capacity=1024; 800-byte payload frames; stalled flush; concurrent Full sends; precreated frames; drain all; cancellation policy recorded in cancelWhileBlocked", warmup, batches, contenders, cancellable, cancelWhileBlocked, runtime = RuntimeInformation.FrameworkDescription, samples });
    }
}
class Transport(PipeWriter output) : ITransportConnection
{
    public string Id => "backpressure-evidence"; public PipeReader Input { get; } = new Pipe().Reader; public PipeWriter Output => output; public EndPoint? LocalEndPoint => null; public EndPoint? RemoteEndPoint => null;
    public async ValueTask DisposeAsync() { await Output.CompleteAsync(); await Input.CompleteAsync(); }
}
class SinkWriter : PipeWriter
{
    protected readonly byte[] bytes = new byte[4096]; public override void Advance(int count) { }
    public override Memory<byte> GetMemory(int sizeHint = 0) => bytes; public override Span<byte> GetSpan(int sizeHint = 0) => bytes; public override void CancelPendingFlush() { }
    public override void Complete(Exception? exception = null) { }
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => new(new FlushResult(false, false));
}
class GateWriter : SinkWriter
{
    TaskCompletionSource<FlushResult>? gate; volatile bool blocked; public bool Blocked => blocked;
    public void Arm() { blocked = false; gate = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void Release() { var g = Interlocked.Exchange(ref gate, null); g!.SetResult(new(false, false)); }
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) { var g = Volatile.Read(ref gate); if (g == null) return base.FlushAsync(cancellationToken); blocked = true; return new(g.Task); }
}

class RecordingGateWriter : GateWriter
{
    public List<int> Ids { get; } = [];
    public override void Advance(int count)
    {
        int pos = 0; while (pos < count) { Ids.Add(checked((int)System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(pos + 7)))); pos += ProtocolV2Constants.HeaderBytes + System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 1)); }
    }
}
