from pathlib import Path


def replace_once(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    assert count == 1, (path, count, old[:160])
    p.write_text(text.replace(old, new, 1))


# The rollback plugin is a current-build fixture. Keep its locator on the current generated API
# instead of pinning the pre-#287 development value.
replace_once(
    'test/SharpLink.RollbackPlugin/RollbackManifest.cs',
    '''[assembly: SharpLinkGeneratedAssemblyManifest(
    typeof(SharpLink.RollbackPlugin.RollbackManifest),
    5,
    2,
    "rollback-test")]''',
    '''[assembly: SharpLinkGeneratedAssemblyManifest(
    typeof(SharpLink.RollbackPlugin.RollbackManifest),
    SharpLinkGeneratedManifestVersions.Api,
    SharpLinkGeneratedManifestVersions.Protocol,
    "rollback-test")]''')

# API 4 is current in 2.0. Negative locator fixtures must use actually unsupported versions.
p = Path('test/SharpLink.UnitTests/Runtime/GeneratedManifestLocatorTests.cs')
text = p.read_text()
text = text.replace(
    '''    [Arguments(4, 2)]
    [Arguments(5, 2)]
    [Arguments(5, 3)]''',
    '''    [Arguments(3, 2)]
    [Arguments(5, 2)]
    [Arguments(4, 3)]''', 1)
text = text.replace(
    '''    [Arguments(4, 2, CurrentGeneratorVersion)]
    [Arguments(4, 3, CurrentGeneratorVersion)]
    [Arguments(4, 2, "phase17-other-generator")]''',
    '''    [Arguments(3, 2, CurrentGeneratorVersion)]
    [Arguments(4, 3, CurrentGeneratorVersion)]
    [Arguments(4, 2, "phase17-other-generator")]''', 1)
p.write_text(text)

# Protocol v2.4 is the minimum accepted wire boundary. These tests exercise limit intersection,
# not legacy-minor compatibility, so keep both peers on the current minor and leave old-minor
# rejection to the explicit compatibility tests.
p = Path('test/SharpLink.UnitTests/Protocol/ProtocolV2NegotiatorTests.cs')
text = p.read_text()
old = '''    [Test]
    public void ServerNegotiationShouldIntersectMinorAndLimitsAtBoundaries()
    {
        var cases = new[]
        {
            new
            {
                Offer = CreatePolicy(
                    ProtocolV2Capabilities.None,
                    minorVersion: 3,
                    maxFramePayloadBytes: 8192,
                    streamReceiveWindowBytes: 4096,
                    connectionReceiveWindowBytes: 8192),
                Server = CreatePolicy(
                    ProtocolV2Capabilities.None,
                    minorVersion: 1,
                    maxFramePayloadBytes: 4096,
                    streamReceiveWindowBytes: 2048,
                    connectionReceiveWindowBytes: 4096),
                ExpectedMinor = (ushort)1,
                ExpectedFrame = 4096,
                ExpectedStream = 2048,
                ExpectedConnection = 4096
            },
            new
            {
                Offer = CreatePolicy(
                    ProtocolV2Capabilities.None,
                    minorVersion: 0,
                    maxFramePayloadBytes: SharpLinkProtocolOptions.MinMaxFramePayloadBytes,
                    streamReceiveWindowBytes: 1,
                    connectionReceiveWindowBytes: 1),
                Server = CreatePolicy(
                    ProtocolV2Capabilities.None,
                    maxFramePayloadBytes: SharpLinkProtocolOptions.MaxMaxFramePayloadBytes,
                    streamReceiveWindowBytes: int.MaxValue,
                    connectionReceiveWindowBytes: int.MaxValue),
                ExpectedMinor = (ushort)0,
                ExpectedFrame = SharpLinkProtocolOptions.MinMaxFramePayloadBytes,
                ExpectedStream = 1,
                ExpectedConnection = 1
            }
        };'''
new = '''    [Test]
    public void ServerNegotiationShouldIntersectLimitsAtCurrentMinorBoundaries()
    {
        var cases = new[]
        {
            new
            {
                Offer = CreatePolicy(
                    ProtocolV2Capabilities.None,
                    minorVersion: ProtocolV2Constants.MinorVersion,
                    maxFramePayloadBytes: 8192,
                    streamReceiveWindowBytes: 4096,
                    connectionReceiveWindowBytes: 8192),
                Server = CreatePolicy(
                    ProtocolV2Capabilities.None,
                    minorVersion: ProtocolV2Constants.MinorVersion,
                    maxFramePayloadBytes: 4096,
                    streamReceiveWindowBytes: 2048,
                    connectionReceiveWindowBytes: 4096),
                ExpectedMinor = ProtocolV2Constants.MinorVersion,
                ExpectedFrame = 4096,
                ExpectedStream = 2048,
                ExpectedConnection = 4096
            },
            new
            {
                Offer = CreatePolicy(
                    ProtocolV2Capabilities.None,
                    minorVersion: ProtocolV2Constants.MinorVersion,
                    maxFramePayloadBytes: SharpLinkProtocolOptions.MinMaxFramePayloadBytes,
                    streamReceiveWindowBytes: 1,
                    connectionReceiveWindowBytes: 1),
                Server = CreatePolicy(
                    ProtocolV2Capabilities.None,
                    minorVersion: ProtocolV2Constants.MinorVersion,
                    maxFramePayloadBytes: SharpLinkProtocolOptions.MaxMaxFramePayloadBytes,
                    streamReceiveWindowBytes: int.MaxValue,
                    connectionReceiveWindowBytes: int.MaxValue),
                ExpectedMinor = ProtocolV2Constants.MinorVersion,
                ExpectedFrame = SharpLinkProtocolOptions.MinMaxFramePayloadBytes,
                ExpectedStream = 1,
                ExpectedConnection = 1
            }
        };'''
assert text.count(old) == 1
text = text.replace(old, new, 1)
text = text.replace(
    '''            offeredCapabilities,
            serverProviders,
            minorVersion: 2,
            maxFramePayloadBytes: 8192,''',
    '''            offeredCapabilities,
            serverProviders,
            minorVersion: ProtocolV2Constants.MinorVersion,
            maxFramePayloadBytes: 8192,''', 1)
p.write_text(text)

# RpcDeadline contains a UTC diagnostic projection plus monotonic state and doubled OwnedFrame's
# hot-queue footprint. Do not retain it in every queued frame. The request's unsent TimeBudget
# slot temporarily carries the process-local monotonic timestamp and is converted to remaining
# ticks immediately before transport flush.
Path('src/SharpLink.Runtime/OwnedFrame.cs').write_text('''namespace SharpLink.Runtime;

/// <summary>
/// Transfers one encoded frame and its backing writer to the session send pump.
/// Only the pump may return the owner after the frame has been flushed or drained.
/// </summary>
internal readonly struct OwnedFrame(
    IRpcByteBufferWriter owner,
    bool forceFlush,
    TaskCompletionSource<bool>? flushCompletion,
    bool isProtocolProgress)
{
    public IRpcByteBufferWriter Owner { get; } = owner;

    public ReadOnlyMemory<byte> Memory { get; } = owner.WrittenMemory;

    public int Length { get; } = owner.WrittenCount;

    public bool ForceFlush { get; } = forceFlush;

    public TaskCompletionSource<bool>? FlushCompletion { get; } = flushCompletion;

    /// <summary>
    /// True when the frame carries protocol progress (ping/pong, window
    /// update, go-away) rather than RPC data. The send pump admits and
    /// drains progress frames against a small reserved byte headroom and a
    /// bounded priority burst so stream saturation cannot starve them.
    /// </summary>
    public bool IsProtocolProgress { get; } = isProtocolProgress;
}
''')

# Stamp the local monotonic timestamp into the still-private request buffer before enqueue.
p = Path('src/SharpLink.Runtime/RpcSession.cs')
text = p.read_text()
old = '''    private static OwnedFrame CreateFrame(
        IRpcByteBufferWriter packet,
        bool forceFlush,
        TaskCompletionSource<bool>? flushCompletion,
        RpcDeadline deadline = default)
        => new(
            packet,
            forceFlush,
            flushCompletion,
            IsProtocolProgressFrame(packet.WrittenSpan),
            deadline);'''
new = '''    private static OwnedFrame CreateFrame(
        IRpcByteBufferWriter packet,
        bool forceFlush,
        TaskCompletionSource<bool>? flushCompletion,
        RpcDeadline deadline = default)
    {
        if (deadline.HasValue)
        {
            var span = packet.WrittenSpan;
            var budgetOffset = ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes;
            if (span.Length >= budgetOffset + sizeof(long) &&
                (ProtocolV2FrameType)span[5] == ProtocolV2FrameType.Request &&
                (((ProtocolV2FrameFlags)span[6]) & ProtocolV2FrameFlags.HasTimeBudget) != 0)
            {
                BinaryPrimitives.WriteInt64LittleEndian(
                    span.Slice(budgetOffset, sizeof(long)),
                    deadline.Timestamp);
            }
        }

        return new OwnedFrame(
            packet,
            forceFlush,
            flushCompletion,
            IsProtocolProgressFrame(packet.WrittenSpan));
    }'''
assert text.count(old) == 1
p.write_text(text.replace(old, new, 1))

# Restore the original progress/normal write ordering for batches that contain no deadline.
# Once the first deadline-bearing request is encountered, defer that suffix so its budget can be
# stamped at the true flush boundary; later frames stay behind it to preserve transport order.
p = Path('src/SharpLink.Runtime/RpcSession.SendPump.cs')
text = p.read_text()
text = text.replace(
    '''            var bytesAccumulated = 0;
            var batchDeadline = 0L;''',
    '''            var bytesAccumulated = 0;
            var batchDeadline = 0L;
            var writtenCount = 0;
            var deferWrites = false;''', 1)

# Progress drain at loop top.
text = text.replace(
    'if (await DrainProgressQueueAsync(pending).ConfigureAwait(false))',
    'if (await DrainProgressQueueAsync(pending, deferWrites).ConfigureAwait(false))', 1)
text = text.replace(
    '''                        if (pending.Count > 0)
                        {
                            await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                            bytesAccumulated = 0;
                        }
                        batchDeadline = 0;''',
    '''                        if (!deferWrites)
                            writtenCount = pending.Count;
                        if (pending.Count > 0)
                        {
                            await FlushAndReleaseAsync(pending, writtenCount).ConfigureAwait(false);
                            bytesAccumulated = 0;
                            writtenCount = 0;
                            deferWrites = false;
                        }
                        batchDeadline = 0;''', 1)

old = '''                        pending.Add(frame);
                        bytesAccumulated += frame.Length;'''
new = '''                        pending.Add(frame);
                        if (!deferWrites)
                        {
                            if (HasTimeBudget(frame))
                                deferWrites = true;
                            else
                            {
                                WriteFrame(frame);
                                writtenCount++;
                            }
                        }
                        bytesAccumulated += frame.Length;'''
assert text.count(old) == 1
text = text.replace(old, new, 1)

# Flushes in normal loop: there are two identical calls (threshold and interleave caller).
text = text.replace(
    '''                            await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                            bytesAccumulated = 0;
                            batchDeadline = 0;''',
    '''                            await FlushAndReleaseAsync(pending, writtenCount).ConfigureAwait(false);
                            bytesAccumulated = 0;
                            batchDeadline = 0;
                            writtenCount = 0;
                            deferWrites = false;''', 1)

# Progress interleave call and flush.
text = text.replace(
    'if (await DrainProgressQueueAsync(pending).ConfigureAwait(false))',
    'if (await DrainProgressQueueAsync(pending, deferWrites).ConfigureAwait(false))', 1)
text = text.replace(
    '''                                if (pending.Count > 0)
                                {
                                    await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                                    bytesAccumulated = 0;
                                }
                                batchDeadline = 0;''',
    '''                                if (!deferWrites)
                                    writtenCount = pending.Count;
                                if (pending.Count > 0)
                                {
                                    await FlushAndReleaseAsync(pending, writtenCount).ConfigureAwait(false);
                                    bytesAccumulated = 0;
                                    writtenCount = 0;
                                    deferWrites = false;
                                }
                                batchDeadline = 0;''', 1)

# Final batch flush.
text = text.replace(
    '''                    await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                    bytesAccumulated = 0;
                    batchDeadline = 0;''',
    '''                    await FlushAndReleaseAsync(pending, writtenCount).ConfigureAwait(false);
                    bytesAccumulated = 0;
                    batchDeadline = 0;
                    writtenCount = 0;
                    deferWrites = false;''', 1)

old = '''        private async ValueTask<bool> DrainProgressQueueAsync(List<OwnedFrame> pending)
        {
            // The drain runs until the progress queue is empty so the service
            // rate always matches the arrival rate: any threshold break here
            // would cap progress service per check point and let the backlog
            // grow without bound (observed with LowLatency, where the
            // threshold is one frame). LowLatency preserves its per-frame
            // flush contract inside the loop; the other modes flush once in
            // the caller after the full drain.
            var drained = false;
            var drainedCount = 0;
            while (drainedCount < ProgressFramesPerDrain &&
                   _progressQueue.Reader.TryRead(out var frame))
            {
                pending.Add(frame);
                drained = true;
                drainedCount++;
                if (_flushMode == FlushMode.LowLatency)
                {
                    // The caller resets the byte accumulator after its flush.
                    await FlushAndReleaseAsync(pending).ConfigureAwait(false);
                }
            }
            return drained;
        }

        private bool TryWriteFrame(OwnedFrame frame)
        {
            var source = frame.Memory.Span;
            if (source.IsEmpty)
                return true;

            TimeSpan remaining = default;
            var hasTimeBudget = frame.Deadline.HasValue &&
                source.Length >= ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes + sizeof(long) &&
                (ProtocolV2FrameType)source[5] == ProtocolV2FrameType.Request &&
                (((ProtocolV2FrameFlags)source[6]) & ProtocolV2FrameFlags.HasTimeBudget) != 0;
            if (hasTimeBudget)
            {
                remaining = frame.Deadline.GetRemaining(_timeProvider);
                if (remaining <= TimeSpan.Zero)
                    return false;
            }

            SharpLinkTelemetry.RecordSentBytes(source.Length);
            var destination = _output.GetSpan(source.Length);
            source.CopyTo(destination);
            if (hasTimeBudget)
            {
                BinaryPrimitives.WriteInt64LittleEndian(
                    destination.Slice(
                        ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes,
                        sizeof(long)),
                    remaining.Ticks);
            }
            _output.Advance(source.Length);
            return true;
        }

        private async ValueTask FlushAndReleaseAsync(List<OwnedFrame> pending)
        {
            // Frames stay in their owned buffers until the batch is actually ready to flush.
            // This is the last point at which local batching/send-queue time can be deducted.
            for (var index = 0; index < pending.Count;)
            {
                var frame = pending[index];
                if (TryWriteFrame(frame))
                {
                    index++;
                    continue;
                }

                pending.RemoveAt(index);
                CompleteReserved(
                    frame,
                    new SharpLinkException(
                        SharpLinkErrorCode.DeadlineExceeded,
                        "Request deadline expired before transport emission."),
                    completeFlushWaiter: true);
            }

            if (pending.Count == 0)
                return;

            var result = await _output.FlushAsync(_sessionCancellation).ConfigureAwait(false);
            if (result.IsCanceled || result.IsCompleted)
                throw CreateTransportClosedException();
            ReleaseBatch(pending, exception: null);
        }'''
new = '''        private async ValueTask<bool> DrainProgressQueueAsync(
            List<OwnedFrame> pending,
            bool deferWrites)
        {
            // The drain runs until the progress queue is empty so the service
            // rate always matches the arrival rate. If an earlier deadline-bearing
            // frame is deferred, progress stays behind it; otherwise preserve the
            // original immediate-copy ordering and only delay the transport flush.
            var drained = false;
            var drainedCount = 0;
            while (drainedCount < ProgressFramesPerDrain &&
                   _progressQueue.Reader.TryRead(out var frame))
            {
                pending.Add(frame);
                if (!deferWrites)
                    WriteFrame(frame);
                drained = true;
                drainedCount++;
                if (_flushMode == FlushMode.LowLatency)
                {
                    await FlushAndReleaseAsync(
                        pending,
                        deferWrites ? 0 : pending.Count).ConfigureAwait(false);
                }
            }
            return drained;
        }

        private static bool HasTimeBudget(OwnedFrame frame)
        {
            var source = frame.Memory.Span;
            return source.Length >=
                       ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes + sizeof(long) &&
                   (ProtocolV2FrameType)source[5] == ProtocolV2FrameType.Request &&
                   (((ProtocolV2FrameFlags)source[6]) & ProtocolV2FrameFlags.HasTimeBudget) != 0;
        }

        private void WriteFrame(OwnedFrame frame)
        {
            var source = frame.Memory.Span;
            if (source.IsEmpty)
                return;
            SharpLinkTelemetry.RecordSentBytes(source.Length);
            var destination = _output.GetSpan(source.Length);
            source.CopyTo(destination);
            _output.Advance(source.Length);
        }

        private bool TryWriteFrameAtEmission(OwnedFrame frame)
        {
            var source = frame.Memory.Span;
            if (source.IsEmpty)
                return true;
            if (!HasTimeBudget(frame))
            {
                WriteFrame(frame);
                return true;
            }

            var budgetOffset = ProtocolV2Constants.HeaderBytes + ProtocolV2Constants.RequestPrefixBytes;
            var deadlineTimestamp = BinaryPrimitives.ReadInt64LittleEndian(
                source.Slice(budgetOffset, sizeof(long)));
            var remaining = RpcDeadline.GetRemaining(
                deadlineTimestamp,
                _timeProvider.GetTimestamp(),
                _timeProvider.TimestampFrequency);
            if (remaining <= TimeSpan.Zero)
                return false;

            SharpLinkTelemetry.RecordSentBytes(source.Length);
            var destination = _output.GetSpan(source.Length);
            source.CopyTo(destination);
            BinaryPrimitives.WriteInt64LittleEndian(
                destination.Slice(budgetOffset, sizeof(long)),
                remaining.Ticks);
            _output.Advance(source.Length);
            return true;
        }

        private async ValueTask FlushAndReleaseAsync(
            List<OwnedFrame> pending,
            int writtenCount)
        {
            // Only the suffix beginning with the first deadline-bearing request stays in
            // owned buffers. Convert its private monotonic timestamp to a remaining wire
            // budget at the last possible point before FlushAsync.
            for (var index = writtenCount; index < pending.Count;)
            {
                var frame = pending[index];
                if (TryWriteFrameAtEmission(frame))
                {
                    index++;
                    continue;
                }

                pending.RemoveAt(index);
                CompleteReserved(
                    frame,
                    new SharpLinkException(
                        SharpLinkErrorCode.DeadlineExceeded,
                        "Request deadline expired before transport emission."),
                    completeFlushWaiter: true);
            }

            if (pending.Count == 0)
                return;

            var result = await _output.FlushAsync(_sessionCancellation).ConfigureAwait(false);
            if (result.IsCanceled || result.IsCompleted)
                throw CreateTransportClosedException();
            ReleaseBatch(pending, exception: null);
        }'''
assert text.count(old) == 1
text = text.replace(old, new, 1)
p.write_text(text)
