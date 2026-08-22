from pathlib import Path
import re

client_files = [
    'src/SharpLink.Abstractions/IRpcChannel.cs',
    'src/SharpLink.Abstractions/SharpLinkInterceptors.cs',
    'src/SharpLink.Client/SharpLinkClient.CallOptions.cs',
    'src/SharpLink.Client/SharpLinkClient.DynamicChannel.cs',
    'src/SharpLink.Client/SharpLinkClient.Interceptors.cs',
    'src/SharpLink.Client/SharpLinkClient.Invokers.cs',
    'src/SharpLink.Client/SharpLinkClient.Telemetry.cs',
]
for name in client_files:
    p = Path(name)
    text = p.read_text()
    text = text.replace('SharpLinkCallOptions options', 'SharpLinkMetadata? metadata')
    text = re.sub(r'\boptions\b', 'metadata', text)
    p.write_text(text)

p = Path('src/SharpLink.Abstractions/SharpLinkInterceptors.cs')
text = p.read_text()
text = text.replace('Options = metadata;', 'Metadata = metadata;')
text = text.replace('public SharpLinkCallOptions Options { get; set; }', 'public SharpLinkMetadata? Metadata { get; set; }')
p.write_text(text)

p = Path('src/SharpLink.Client/SharpLinkClient.CallOptions.cs')
text = p.read_text()
start = text.index('    private ResolvedCallControl ResolveCallControl(')
end = text.index('    private async ValueTask<ClientConnection>', start)
replacement = '''    private ResolvedCallControl ResolveCallControl(
        SharpLinkMetadata? metadata,
        bool includeClientDefault,
        bool hasMethodTimeout,
        TimeSpan? methodTimeout)
    {
        if (methodTimeout is { } configuredMethodTimeout)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(configuredMethodTimeout, TimeSpan.Zero);
        var timeProvider = _runtimeContext.TimeProvider;
        var utcNow = timeProvider.GetUtcNow();
        var timestampNow = timeProvider.GetTimestamp();
        DateTimeOffset? utcDeadline = null;
        if (methodTimeout is { } explicitMethodTimeout)
            AddDeadlineCandidate(ref utcDeadline, AddTimeout(utcNow, explicitMethodTimeout));
        if ((includeClientDefault || hasMethodTimeout) && _hasRequestTimeout)
            AddDeadlineCandidate(ref utcDeadline, AddTimeout(utcNow, _requestTimeoutValue));

        var deadline = utcDeadline is { } value
            ? RpcDeadline.Create(
                value,
                utcNow,
                timestampNow,
                timeProvider.TimestampFrequency)
            : default;
        if (deadline.IsExpired(timestampNow))
            throw CreateDeadlineExceededException();
        return new ResolvedCallControl(
            deadline,
            metadata is { Count: > 0 } ? metadata : null,
            WaitForReady: false);
    }

'''
text = text[:start] + replacement + text[end:]
p.write_text(text)

p = Path('src/SharpLink.Abstractions/ProtocolV2.cs')
text = p.read_text()
text = text.replace('public const ushort MinorVersion = 3;', 'public const ushort MinorVersion = 4;\n    public const ushort TimeBudgetMinorVersion = 4;')
text = text.replace('HasDeadline = 1 << 2,', 'HasTimeBudget = 1 << 2,')
p.write_text(text)

for name in Path('src').rglob('*.cs'):
    text = name.read_text()
    if 'ProtocolV2FrameFlags.HasDeadline' in text:
        name.write_text(text.replace('ProtocolV2FrameFlags.HasDeadline', 'ProtocolV2FrameFlags.HasTimeBudget'))

p = Path('src/SharpLink.Abstractions/RpcDeadline.cs')
text = p.read_text()
marker = '    internal static RpcDeadline Create(DateTimeOffset utcDeadline, long timestamp)\n        => new(utcDeadline, timestamp);\n'
addition = '''    internal static RpcDeadline Create(TimeSpan timeBudget, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBudget, TimeSpan.Zero);
        var utcNow = timeProvider.GetUtcNow();
        var maximum = DateTimeOffset.MaxValue - utcNow;
        var utcDeadline = timeBudget >= maximum ? DateTimeOffset.MaxValue : utcNow.Add(timeBudget);
        var timestampNow = timeProvider.GetTimestamp();
        return new RpcDeadline(
            utcDeadline,
            timeBudget == TimeSpan.Zero
                ? timestampNow
                : SharpLinkTime.AddDuration(timestampNow, timeBudget, timeProvider.TimestampFrequency));
    }

'''
assert marker in text
text = text.replace(marker, addition + marker)
p.write_text(text)

for filename in [
    'src/SharpLink.Client/SharpLinkClient.Invokers.cs',
    'src/SharpLink.Client/SharpLinkClient.RpcChannel.cs',
]:
    p = Path(filename)
    text = p.read_text()
    text = text.replace('DateTimeOffset? deadline,\n        SharpLinkMetadata? metadata)', 'RpcDeadline deadline,\n        SharpLinkMetadata? metadata)')
    text = text.replace('if (deadline is not null)\n            flags |= ProtocolV2FrameFlags.HasTimeBudget;', 'if (deadline.HasValue)\n            flags |= ProtocolV2FrameFlags.HasTimeBudget;')
    text = text.replace('''if (deadline is { } absoluteDeadline)
                {
                    var deadlineSpan = writer.GetSpan(sizeof(long));
                    BinaryPrimitives.WriteInt64LittleEndian(
                        deadlineSpan,
                        absoluteDeadline.ToUnixTimeMilliseconds());
                    writer.Advance(sizeof(long));
                }''', '''if (deadline.HasValue)
                {
                    var lifetimeSpan = writer.GetSpan(sizeof(long));
                    BinaryPrimitives.WriteInt64LittleEndian(
                        lifetimeSpan,
                        GetWireLifetimeValue(session, deadline));
                    writer.Advance(sizeof(long));
                }''')
    text = text.replace('control.Deadline.UtcDeadline,\n                    control.Metadata)', 'control.Deadline,\n                    control.Metadata)')
    text = text.replace('control.Deadline.UtcDeadline,\n                control.Metadata)', 'control.Deadline,\n                control.Metadata)')
    p.write_text(text)

p = Path('src/SharpLink.Client/SharpLinkClient.CallOptions.cs')
text = p.read_text()
marker = '    private static SharpLinkException CreateDeadlineExceededException()\n'
helper = '''    private long GetWireLifetimeValue(RpcSession session, RpcDeadline deadline)
    {
        if (session.NegotiatedOptions?.ProtocolMinorVersion >= ProtocolV2Constants.TimeBudgetMinorVersion)
        {
            var remaining = deadline.GetRemaining(_runtimeContext.TimeProvider);
            if (remaining <= TimeSpan.Zero)
                throw CreateDeadlineExceededException();
            return remaining.Ticks;
        }

        return deadline.UtcDeadline!.Value.ToUnixTimeMilliseconds();
    }

'''
assert marker in text
text = text.replace(marker, helper + marker)
p.write_text(text)

p = Path('src/SharpLink.Server/ServerRequestEnvelopeReader.cs')
text = p.read_text()
start = text.index('        var deadline = default(RpcDeadline);')
end = text.index('\n        SharpLinkMetadata? metadata = null;', start)
replacement = '''        var deadline = default(RpcDeadline);
        if ((flags & ProtocolV2FrameFlags.HasTimeBudget) != 0)
        {
            if (!reader.TryReadLittleEndian(out long lifetimeValue))
                throw new SharpLinkProtocolViolationException(ProtocolViolationReason.MalformedFrame, "Request time budget is truncated.");

            if (session.NegotiatedOptions?.ProtocolMinorVersion >= ProtocolV2Constants.TimeBudgetMinorVersion)
            {
                if (lifetimeValue < 0)
                {
                    throw new SharpLinkProtocolViolationException(
                        ProtocolViolationReason.MalformedFrame,
                        "Request time budget cannot be negative.");
                }
                deadline = RpcDeadline.Create(TimeSpan.FromTicks(lifetimeValue), timeProvider);
            }
            else
            {
                try
                {
                    var utcDeadline = DateTimeOffset.FromUnixTimeMilliseconds(lifetimeValue);
                    deadline = RpcDeadline.Create(
                        utcDeadline,
                        timeProvider.GetUtcNow(),
                        timeProvider.GetTimestamp(),
                        timeProvider.TimestampFrequency);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    throw new SharpLinkException(
                        SharpLinkErrorCode.ProtocolViolation,
                        "Request deadline is outside the supported UTC range.",
                        exception);
                }
            }
        }
'''
text = text[:start] + replacement + text[end:]
p.write_text(text)
