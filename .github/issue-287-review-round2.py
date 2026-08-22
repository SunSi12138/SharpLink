from pathlib import Path
import re


def replace_once(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    assert count == 1, (path, count, old[:80])
    p.write_text(text.replace(old, new, 1))


# 1. Server call-context migration must cover every partial dispatch file.
for name in [
    'src/SharpLink.Server/SharpLinkServer.AdmissionDispatch.cs',
    'src/SharpLink.Server/SharpLinkServer.InvocationDispatch.cs',
]:
    p = Path(name)
    text = p.read_text()
    text = text.replace(
        'request.Deadline, request.Metadata, invokeToken',
        'request.RpcDeadline, request.Metadata, invokeToken')
    p.write_text(text)

p = Path('src/SharpLink.Server/SharpLinkServer.Interceptors.cs')
text = p.read_text()
text = text.replace(
    '''                callContext.Authentication,
                callContext.Deadline,
                callContext.Metadata,
                cancellationToken);''',
    '''                callContext.Authentication,
                callContext.LocalRpcDeadline,
                callContext.DeadlineTimeProvider ?? _runtimeContext.TimeProvider,
                callContext.Metadata,
                cancellationToken);''')
p.write_text(text)

# 2. Remove the public absolute Deadline compatibility projection. The ambient context retains
# only the internal process-local RpcDeadline + its TimeProvider for correct downstream capping.
Path('src/SharpLink.Abstractions/SharpLinkCallContextSnapshot.cs').write_text('''namespace SharpLink.Abstractions;

/// <summary>Describes immutable server-side context for one RPC invocation.</summary>
public class SharpLinkCallContextSnapshot
{
    /// <summary>Creates an immutable server-side call-context snapshot.</summary>
    /// <param name="sessionId">The transport session identifier.</param>
    /// <param name="authentication">The authenticated identity, when present.</param>
    /// <param name="metadata">The immutable request metadata, when present.</param>
    public SharpLinkCallContextSnapshot(
        string sessionId,
        SharpLinkAuthenticationContext? authentication,
        SharpLinkMetadata? metadata = null)
    {
        SessionId = sessionId;
        Authentication = authentication;
        Metadata = metadata;
    }

    internal SharpLinkCallContextSnapshot(
        string sessionId,
        SharpLinkAuthenticationContext? authentication,
        RpcDeadline deadline,
        TimeProvider deadlineTimeProvider,
        SharpLinkMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(deadlineTimeProvider);
        SessionId = sessionId;
        Authentication = authentication;
        LocalRpcDeadline = deadline;
        DeadlineTimeProvider = deadlineTimeProvider;
        Metadata = metadata;
    }

    internal RpcDeadline LocalRpcDeadline { get; }
    internal TimeProvider? DeadlineTimeProvider { get; }

    /// <summary>Gets the transport session identifier.</summary>
    public string SessionId { get; }
    /// <summary>Gets the authenticated identity, when present.</summary>
    public SharpLinkAuthenticationContext? Authentication { get; }
    /// <summary>Gets immutable request metadata, when present.</summary>
    public SharpLinkMetadata? Metadata { get; }
}
''')

# Remove the same absolute projection from admission selector context. Admission scheduling already
# receives the real RpcDeadline as a separate internal argument.
p = Path('src/SharpLink.Server/Admission/SharpLinkAdmissionControlOptions.cs')
text = p.read_text()
text = text.replace(
    '''        SharpLinkAuthenticationContext? authenticationContext,
        SharpLinkMetadata? metadata,
        DateTimeOffset? deadline)''',
    '''        SharpLinkAuthenticationContext? authenticationContext,
        SharpLinkMetadata? metadata)''')
text = text.replace('        Deadline = deadline;\n', '')
text = text.replace(
    '''    /// <summary>Gets the absolute request deadline, when present.</summary>
    public DateTimeOffset? Deadline { get; }
''', '')
p.write_text(text)

p = Path('src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs')
text = p.read_text()
text = re.sub(
    r'''    internal ValueTask<AdmissionDecision> AcquireAsync\(\n        SharpLinkAdmissionContext context,\n        int retainedBytes,\n        bool allowQueue,\n        CancellationToken cancellationToken\)\n        => AcquireAsync\(\n            context,\n            retainedBytes,\n            allowQueue,\n            context\.Deadline is \{ \} deadline\n                \? RpcDeadline\.Create\(deadline, _timeProvider\)\n                : default,\n            cancellationToken\);\n\n''',
    '',
    text)
p.write_text(text)

# CreateAdmissionContext is split across server partials in newer dev. Remove the obsolete argument
# wherever the constructor call occurs.
for p in Path('src/SharpLink.Server').rglob('*.cs'):
    text = p.read_text()
    text = text.replace(
        '''            connection.AuthenticationContext,
            request.Metadata,
            request.Deadline);''',
        '''            connection.AuthenticationContext,
            request.Metadata);''')
    p.write_text(text)

# Public API tests/benchmarks must stop preserving the old DateTimeOffset contract.
p = Path('test/SharpLink.UnitTests/Abstractions/SharpLinkAuthorizationTests.cs')
text = p.read_text()
start = text.index('    [Test]\n    public void CallContextSnapshotShouldPreserveDeadlineTicksAndOffset()')
end = text.index('    [Test]\n    public async Task CallContextScopeShouldFlowAcrossAwaitAndRestoreNestedScope()', start)
replacement = '''    [Test]
    public void CallContextSnapshotShouldNotExposeAbsoluteDeadline()
    {
        Ensure(
            typeof(SharpLinkCallContextSnapshot).GetProperty("Deadline") is null,
            "2.0 call context must not expose a wall-clock Deadline compatibility projection");
    }

'''
text = text[:start] + replacement + text[end:]
p.write_text(text)

p = Path('test/SharpLink.Benchmarks/RuntimeHotPathBenchmarks.cs')
text = p.read_text()
text = text.replace('    private readonly DateTimeOffset _deadline = DateTimeOffset.UtcNow.AddSeconds(30);\n', '')
text = re.sub(
    r'''\n    \[Benchmark\]\n    public void CreateDeadlinePushAndRestoreCallContext\(\)\n    \{.*?\n    \}\n''',
    '\n',
    text,
    flags=re.S)
p.write_text(text)

# 3. Exact monotonic arithmetic: no double conversion in either direction.
Path('src/SharpLink.Abstractions/SharpLinkTime.cs').write_text('''namespace SharpLink.Abstractions;

/// <summary>Provides overflow-safe arithmetic for instance-owned monotonic clocks.</summary>
internal static class SharpLinkTime
{
    internal static long AddDuration(
        long timestamp,
        TimeSpan duration,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        if (duration == TimeSpan.Zero)
            return timestamp;

        var numerator = (UInt128)(ulong)duration.Ticks * (ulong)timestampFrequency;
        var denominator = (UInt128)TimeSpan.TicksPerSecond;
        var timestampDelta = (numerator + denominator - 1) / denominator;
        if (timestampDelta == 0)
            timestampDelta = 1;
        if (timestampDelta >= (UInt128)long.MaxValue)
            return long.MaxValue;
        var delta = (long)timestampDelta;
        return timestamp > long.MaxValue - delta
            ? long.MaxValue
            : timestamp + delta;
    }

    internal static TimeSpan GetRemaining(
        long deadlineTimestamp,
        long timestampNow,
        long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        var remainingTimestampUnits = (Int128)deadlineTimestamp - timestampNow;
        if (remainingTimestampUnits <= 0)
            return TimeSpan.Zero;

        var numerator = (UInt128)remainingTimestampUnits * (uint)TimeSpan.TicksPerSecond;
        var denominator = (UInt128)(ulong)timestampFrequency;
        var ticks = (numerator + denominator - 1) / denominator;
        if (ticks >= (UInt128)TimeSpan.MaxValue.Ticks)
            return TimeSpan.MaxValue;
        return TimeSpan.FromTicks((long)ticks);
    }
}
''')

Path('test/SharpLink.UnitTests/Abstractions/SharpLinkTimePrecisionTests.cs').write_text('''namespace SharpLink.UnitTests.Abstractions;

public class SharpLinkTimePrecisionTests
{
    [Test]
    public void GetRemainingShouldPreserveOneUnitNearLongMaxValue()
    {
        var remaining = SharpLinkTime.GetRemaining(
            long.MaxValue,
            long.MaxValue - 1,
            TimeSpan.TicksPerSecond);
        Ensure(remaining == TimeSpan.FromTicks(1),
            "one positive timestamp unit must never round down to expired");
    }

    [Test]
    public void AddDurationShouldRoundUpAtFrequencyAboveDoubleIntegerPrecision()
    {
        const long frequency = 9_007_199_254_740_993L; // 2^53 + 1
        var deadline = SharpLinkTime.AddDuration(0, TimeSpan.FromSeconds(1), frequency);
        Ensure(deadline == frequency,
            "one second must resolve to the exact custom-provider frequency");
    }

    [Test]
    public void RoundTripShouldNeverExpireEarlyAtExtremeValues()
    {
        const long frequency = 9_007_199_254_740_993L;
        var start = long.MaxValue - frequency - 10;
        var deadline = SharpLinkTime.AddDuration(start, TimeSpan.FromSeconds(1), frequency);
        var remaining = SharpLinkTime.GetRemaining(deadline, start, frequency);
        Ensure(remaining >= TimeSpan.FromSeconds(1),
            "duration -> timestamp -> duration conversion must not shorten the lifetime");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
''')

# 4. Freeze RpcDeadline before user client interceptors run. Metadata remains mutable, but terminal
# invocation and short-circuit validation share the same frozen boundary.
p = Path('src/SharpLink.Client/SharpLinkClient.Interceptors.cs')
text = p.read_text()
text = text.replace(
    '        private readonly SharpLinkClientInvocationContext _context;\n        private long _started;',
    '        private readonly SharpLinkClientInvocationContext _context;\n        private readonly ResolvedCallControl _control;\n        private long _started;')
text = text.replace(
    '''            _client = client;
            _interceptors = interceptors;
            _context = new SharpLinkClientInvocationContext(method, request, metadata, cancellationToken);''',
    '''            _client = client;
            _interceptors = interceptors;
            _control = client.ResolveCallControl(
                metadata,
                method.Kind == RpcMethodKind.Unary,
                method.HasMethodTimeout,
                method.MethodTimeout);
            _context = new SharpLinkClientInvocationContext(
                method, request, _control.Metadata, cancellationToken);''')
text = text.replace(
    '                var result = await InvokeNextAsync(0, _context).ConfigureAwait(false);\n                ValidateResult(result);',
    '                var result = await InvokeNextAsync(0, _context).ConfigureAwait(false);\n                ThrowIfFrozenDeadlineExpired();\n                ValidateResult(result);')
# The replacement above intentionally applies to all chain variants with the same sequence.
text = re.sub(
    r'''var control = Client\.ResolveCallControl\(\s*context\.Metadata,\s*(?:true|false),\s*_method\.HasMethodTimeout,\s*_method\.MethodTimeout\);''',
    'var control = GetTerminalControl(context);',
    text)
insert_marker = '''        protected void MarkTerminalSucceeded(SharpLinkClientInvocationContext context)
            => context.Status = SharpLinkInvocationStatus.Succeeded;
'''
insert = '''        protected ResolvedCallControl GetTerminalControl(SharpLinkClientInvocationContext context)
            => new(
                _control.Deadline,
                context.Metadata is { Count: > 0 } ? context.Metadata : null,
                _control.WaitForReady);

        private void ThrowIfFrozenDeadlineExpired()
        {
            if (_control.Deadline.IsExpired(_client._runtimeContext.TimeProvider))
                throw CreateDeadlineExceededException();
        }

'''
assert insert_marker in text
text = text.replace(insert_marker, insert + insert_marker)
p.write_text(text)

# 5. TimeBudget is stamped at flush/emission time, not while the request prefix is serialized.
p = Path('src/SharpLink.Client/SharpLinkClient.RpcChannel.cs')
text = p.read_text()
text = re.sub(
    r'''if \(deadline\.HasValue\)\n                \{\n                    var timeBudgetSpan = writer\.GetSpan\(sizeof\(long\)\);\n                    BinaryPrimitives\.WriteInt64LittleEndian\(\n                        timeBudgetSpan,\n                        GetWireTimeBudgetValue\(deadline\)\);\n                    writer\.Advance\(sizeof\(long\)\);\n                \}''',
    '''if (deadline.HasValue)
                {
                    // Placeholder only. RpcSession stamps the remaining TimeBudget immediately
                    // before the batch is flushed to the transport.
                    var timeBudgetSpan = writer.GetSpan(sizeof(long));
                    BinaryPrimitives.WriteInt64LittleEndian(timeBudgetSpan, 0L);
                    writer.Advance(sizeof(long));
                }''',
    text)
text = text.replace('            session.SendPacket(writer);', '            session.SendPacket(writer, deadline);')
p.write_text(text)

p = Path('src/SharpLink.Client/SharpLinkClient.CallOptions.cs')
text = p.read_text()
text = re.sub(
    r'''\n    private long GetWireTimeBudgetValue\(RpcDeadline deadline\)\n    \{.*?\n    \}\n''',
    '\n',
    text,
    flags=re.S)
p.write_text(text)

p = Path('src/SharpLink.Runtime/OwnedFrame.cs')
text = p.read_text()
text = text.replace(
    '''    TaskCompletionSource<bool>? flushCompletion,
    bool isProtocolProgress)''',
    '''    TaskCompletionSource<bool>? flushCompletion,
    bool isProtocolProgress,
    RpcDeadline deadline)''')
text = text.replace(
    '''    public bool IsProtocolProgress { get; } = isProtocolProgress;
}''',
    '''    public bool IsProtocolProgress { get; } = isProtocolProgress;

    /// <summary>Process-local lifetime boundary used only to stamp Request TimeBudget at emission.</summary>
    public RpcDeadline Deadline { get; } = deadline;
}''')
p.write_text(text)

p = Path('src/SharpLink.Runtime/RpcSession.cs')
text = p.read_text()
text = text.replace(
    '    internal void SendPacket(IRpcByteBufferWriter packet)\n',
    '    internal void SendPacket(IRpcByteBufferWriter packet, RpcDeadline deadline = default)\n')
old = '            .TryEnqueue(CreateFrame(packet, forceFlush: false, flushCompletion: null));'
assert old in text
text = text.replace(
    old,
    '            .TryEnqueue(CreateFrame(packet, forceFlush: false, flushCompletion: null, deadline));',
    1)
text = text.replace(
    '''    private static OwnedFrame CreateFrame(
        IRpcByteBufferWriter packet,
        bool forceFlush,
        TaskCompletionSource<bool>? flushCompletion)
        => new(
            packet,
            forceFlush,
            flushCompletion,
            IsProtocolProgressFrame(packet.WrittenSpan));''',
    '''    private static OwnedFrame CreateFrame(
        IRpcByteBufferWriter packet,
        bool forceFlush,
        TaskCompletionSource<bool>? flushCompletion,
        RpcDeadline deadline = default)
        => new(
            packet,
            forceFlush,
            flushCompletion,
            IsProtocolProgressFrame(packet.WrittenSpan),
            deadline);''')
p.write_text(text)

p = Path('src/SharpLink.Runtime/RpcSession.SendPump.cs')
text = p.read_text()
text = text.replace(
    '''                        pending.Add(frame);
                        WriteFrame(frame);
                        bytesAccumulated += frame.Length;''',
    '''                        pending.Add(frame);
                        bytesAccumulated += frame.Length;''')
text = text.replace(
    '''                pending.Add(frame);
                WriteFrame(frame);
                drained = true;''',
    '''                pending.Add(frame);
                drained = true;''')
old_write = '''        private void WriteFrame(OwnedFrame frame)
        {
            var source = frame.Memory.Span;
            if (source.IsEmpty)
                return;
            SharpLinkTelemetry.RecordSentBytes(source.Length);
            var destination = _output.GetSpan(source.Length);
            source.CopyTo(destination);
            _output.Advance(source.Length);
        }

        private async ValueTask FlushAndReleaseAsync(List<OwnedFrame> pending)
        {
            var result = await _output.FlushAsync(_sessionCancellation).ConfigureAwait(false);'''
new_write = '''        private void WriteFrame(OwnedFrame frame)
        {
            var source = frame.Memory.Span;
            if (source.IsEmpty)
                return;
            SharpLinkTelemetry.RecordSentBytes(source.Length);
            var destination = _output.GetSpan(source.Length);
            source.CopyTo(destination);
            if (frame.Deadline.HasValue &&
                source.Length >= ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes + sizeof(long) &&
                (ProtocolV2FrameType)source[5] == ProtocolV2FrameType.Request &&
                (((ProtocolV2FrameFlags)source[6]) & ProtocolV2FrameFlags.HasTimeBudget) != 0)
            {
                var remaining = frame.Deadline.GetRemaining(_timeProvider);
                BinaryPrimitives.WriteInt64LittleEndian(
                    destination.Slice(
                        ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes,
                        sizeof(long)),
                    remaining.Ticks);
            }
            _output.Advance(source.Length);
        }

        private async ValueTask FlushAndReleaseAsync(List<OwnedFrame> pending)
        {
            // Frames stay in their owned buffers until the batch is actually ready to flush.
            // This is the last point at which local batching/send-queue time can be deducted.
            foreach (var frame in pending)
                WriteFrame(frame);
            var result = await _output.FlushAsync(_sessionCancellation).ConfigureAwait(false);'''
assert old_write in text
text = text.replace(old_write, new_write)
p.write_text(text)

# Regression: explicit TimedBatch latency must be deducted from the emitted TimeBudget.
p = Path('test/SharpLink.UnitTests/Runtime/SendPumpTests.cs')
text = p.read_text()
marker = 'public class SendPumpTests\n{\n'
assert marker in text
new_test = '''public class SendPumpTests
{
    [Test]
    public async Task TimedBatchShouldDeductLatencyFromRequestTimeBudgetAtFlush()
    {
        var clock = new ManualTimeProvider();
        var input = new Pipe();
        var output = new Pipe();
        using var context = new SharpLinkRuntimeContextBuilder()
            .UseTimeProvider(clock)
            .Build(includeGeneratedAssemblyCatalog: false);
        var maxLatency = TimeSpan.FromSeconds(5);
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "time-budget-emission",
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(
                context,
                new RpcSessionFlushOptions(1024 * 1024, maxLatency)));
        RpcSessionTestFixture.CompleteHandshake(session);
        using var frame = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(
            frame,
            ProtocolV2FrameType.Request,
            ProtocolV2FrameFlags.HasTimeBudget,
            1);
        frame.Advance(ProtocolV2Constants.RequestPrefixBytes);
        frame.Advance(sizeof(long));
        ProtocolV2FrameWriter.EndFrame(frame, token);
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(10), clock);

        try
        {
            session.SendPacket(frame, deadline);
            for (var i = 0; i < 1000 && clock.ActiveTimerCount == 0; i++)
                await Task.Yield();
            Ensure(clock.ActiveTimerCount > 0, "timed batch must arm its provider timer");
            clock.Advance(maxLatency);

            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var bytes = read.Buffer.ToArray();
            output.Reader.AdvanceTo(read.Buffer.End);
            var budget = BinaryPrimitives.ReadInt64LittleEndian(
                bytes.AsSpan(
                    ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes,
                    sizeof(long)));
            Ensure(budget == TimeSpan.FromSeconds(5).Ticks,
                "the emitted budget must deduct the full local batching interval");
        }
        finally
        {
            await session.DisposeAsync();
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

'''
text = text.replace(marker, new_test, 1)
p.write_text(text)

# 6. Update the 2.0 migration/release compatibility contract; mixed minor-3/minor-4 peers are no
# longer supported once TimeBudget becomes the only wire lifetime representation.
p = Path('doc/migration.md')
text = p.read_text()
text = text.replace(
    'SharpLink 2.0 将进程内 Generated ABI 从 1.1.x 的 API 3 原子升级为最终基线 API 5，同时保持网络 Protocol v2 不变。',
    'SharpLink 2.0 将进程内 Generated ABI 从 1.1.x 的 API 3 原子升级为最终基线 API 5，同时把 Protocol v2 minor 升到 4，并以剩余 `TimeBudget` 取代跨机器绝对 deadline。')
text = text.replace(
    'Generated ABI 不参与网络握手。1.1.x Client 与 2.0 Server、2.0 Client 与 1.1.x Server 仍可通过 Protocol v2 互操作，但每个进程只能加载与本进程 Runtime 匹配的生成程序集，并且两端契约的 wire schema 必须兼容。',
    'Generated ABI 与网络 minor 是独立版本轴。2.0 的 Protocol v2 minor 4 是破坏性 TimeBudget 边界：1.1.x/minor-3 与 2.0/minor-4 进程不会互操作，任一方向都会在握手阶段以 `Unimplemented` 拒绝。升级必须滚动到两端都支持 minor 4 后再恢复流量；不存在 absolute-deadline fallback。')
text = text.replace(
    'Protocol v2 的当前 wire 定义见 [protocol-v2.md](protocol-v2.md)。Generated ABI（API 5）与 Protocol v2 是独立版本轴；迁移到 2.0 不改变 wire frame 或 capability negotiation。',
    'Protocol v2 的当前 wire 定义见 [protocol-v2.md](protocol-v2.md)。Generated ABI（API 5）与 Protocol v2 minor 是独立版本轴；迁移到 2.0 会把 wire lifetime 从 absolute deadline 改为 minor-4 `TimeBudget`，因此必须把 Client/Server 作为同一个协议升级边界部署。')
p.write_text(text)

p = Path('CHANGELOG.md')
text = p.read_text()
text = text.replace(
    '- Release gates now cover mixed Generator/package versions, all four SharpLink 1.1.1/2.0 Protocol v2 process pairs, five NativeAOT call shapes, generated-assembly metadata dependency scans, and collectible API 5 dynamic modules.',
    '- Release gates cover mixed Generator/package rejection, Protocol v2 minor-4 TimeBudget handshake boundaries, five NativeAOT call shapes, generated-assembly metadata dependency scans, and collectible API 5 dynamic modules. Mixed 1.1.1/minor-3 and 2.0/minor-4 processes are intentionally rejected rather than tested as interoperable pairs.')
text = text.replace(
    '''  snapshot publication or load-context retention. Protocol v2 wire format, contract/schema
  identity, and call-path performance are unchanged.''',
    '''  snapshot publication or load-context retention. Contract/schema identity remains unchanged,
  while Protocol v2 minor 4 intentionally changes request lifetime bytes from an absolute UTC
  deadline to a remaining TimeBudget duration.''')
breaking_marker = '### Breaking\n\n'
assert breaking_marker in text
text = text.replace(
    breaking_marker,
    breaking_marker + '- Protocol v2 minor 4 is a breaking wire boundary for RPC lifetime propagation. Request frames carry remaining `TimeBudget` instead of an absolute Unix-millisecond deadline, and 2.0 rejects peers below minor 4 during handshake. There is no mixed 1.1.x/2.0 deadline compatibility path.\n',
    1)
text = text.replace(
    '- Generated ABI (API 5) is a build/runtime ABI change, not a wire change. Protocol v2 remains unchanged, so separate 1.1.1 and 2.0 processes interoperate when each process uses generated assemblies matching its own Runtime and both sides expose a wire-compatible contract.',
    '- Generated ABI (API 5) remains independent from the network version, but 2.0 also introduces the Protocol v2 minor-4 TimeBudget wire break. Separate 1.1.1/minor-3 and 2.0/minor-4 processes do not interoperate; upgrade both sides across the same protocol boundary.')
p.write_text(text)
