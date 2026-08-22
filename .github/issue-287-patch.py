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

        // Method policy overrides the client-wide fallback. These are policy-selection layers,
        // not independent lifetime caps. A parameterless [Timeout] deliberately falls back to
        // the client-wide value even on call shapes that do not otherwise use the client default.
        var selectedTimeout = methodTimeout;
        if (selectedTimeout is null && (includeClientDefault || hasMethodTimeout) && _hasRequestTimeout)
            selectedTimeout = _requestTimeoutValue;

        var timeProvider = _runtimeContext.TimeProvider;
        var deadline = selectedTimeout is { } timeout
            ? RpcDeadline.Create(timeout, timeProvider)
            : default;
        if (deadline.IsExpired(timeProvider))
            throw CreateDeadlineExceededException();
        return new ResolvedCallControl(
            deadline,
            metadata is { Count: > 0 } ? metadata : null,
            WaitForReady: false);
    }

'''
text = text[:start] + replacement + text[end:]
text = re.sub(
    r'\n    private static void AddDeadlineCandidate\(.*?\n    \}\n(?=\n    private bool WouldReachDeadline)',
    '\n',
    text,
    flags=re.S)
text = re.sub(
    r'\n    private static DateTimeOffset AddTimeout\(.*?\n    \}\n(?=\n    private static SharpLinkException CreateDeadlineExceededException)',
    '\n',
    text,
    flags=re.S)
p.write_text(text)

p = Path('src/SharpLink.Abstractions/ProtocolV2.cs')
text = p.read_text()
text = text.replace(
    'public const ushort MinorVersion = 3;',
    'public const ushort MinorVersion = 4;\n\n    /// <summary>Old protocol minors used absolute wall-clock deadlines and are not wire-compatible.</summary>\n    public const ushort MinimumCompatibleMinorVersion = 4;')
text = text.replace('The request prefix contains a deadline.', 'The request prefix contains a remaining RPC time budget.')
text = text.replace('HasDeadline = 1 << 2,', 'HasTimeBudget = 1 << 2,')
p.write_text(text)

for name in Path('src').rglob('*.cs'):
    text = name.read_text()
    if 'ProtocolV2FrameFlags.HasDeadline' in text:
        name.write_text(text.replace('ProtocolV2FrameFlags.HasDeadline', 'ProtocolV2FrameFlags.HasTimeBudget'))

# Protocol 2.4 is the breaking boundary for TimeBudget wire semantics. Do not negotiate older
# minors and then reinterpret their absolute-deadline field as a duration.
p = Path('src/SharpLink.Runtime/ProtocolV2/ProtocolV2Negotiator.cs')
text = p.read_text()
text = text.replace(
    '''        if (minorVersion > ProtocolV2Constants.MinorVersion)
            throw new ArgumentOutOfRangeException(nameof(minorVersion));''',
    '''        if (minorVersion < ProtocolV2Constants.MinimumCompatibleMinorVersion ||
            minorVersion > ProtocolV2Constants.MinorVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(minorVersion));
        }''')
text = text.replace(
    '''        if (response.MinorVersion > offer.MinorVersion)
        {
            throw Failure(
                SharpLinkErrorCode.Unimplemented,
                $"Server requires unsupported protocol minor version {response.MinorVersion}.");
        }''',
    '''        if (response.MinorVersion < ProtocolV2Constants.MinimumCompatibleMinorVersion)
        {
            throw Failure(
                SharpLinkErrorCode.Unimplemented,
                $"Server selected incompatible protocol minor version {response.MinorVersion}; " +
                $"minimum supported is {ProtocolV2Constants.MinimumCompatibleMinorVersion}.");
        }
        if (response.MinorVersion > offer.MinorVersion)
        {
            throw Failure(
                SharpLinkErrorCode.Unimplemented,
                $"Server requires unsupported protocol minor version {response.MinorVersion}.");
        }''')
text = text.replace(
    '''    private static void ValidatePeerOffer(in ProtocolV2HandshakeRequest offer)
    {
        ValidatePeerLimits(''',
    '''    private static void ValidatePeerOffer(in ProtocolV2HandshakeRequest offer)
    {
        if (offer.MinorVersion < ProtocolV2Constants.MinimumCompatibleMinorVersion)
        {
            throw Failure(
                SharpLinkErrorCode.Unimplemented,
                $"Peer protocol minor version {offer.MinorVersion} is incompatible; " +
                $"minimum supported is {ProtocolV2Constants.MinimumCompatibleMinorVersion}.");
        }
        ValidatePeerLimits(''')
p.write_text(text)

p = Path('src/SharpLink.Abstractions/RpcDeadline.cs')
text = p.read_text()
text = text.replace(
    '''/// <summary>
/// Keeps the wire UTC deadline separate from the monotonic timestamp used for local timing.
/// </summary>''',
    '''/// <summary>
/// Represents a process-local RPC lifetime boundary using a monotonic timestamp.
/// </summary>''')
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
                    var timeBudgetSpan = writer.GetSpan(sizeof(long));
                    BinaryPrimitives.WriteInt64LittleEndian(
                        timeBudgetSpan,
                        GetWireTimeBudgetValue(deadline));
                    writer.Advance(sizeof(long));
                }''')
    text = text.replace('control.Deadline.UtcDeadline,\n                    control.Metadata)', 'control.Deadline,\n                    control.Metadata)')
    text = text.replace('control.Deadline.UtcDeadline,\n                control.Metadata)', 'control.Deadline,\n                control.Metadata)')
    p.write_text(text)

p = Path('src/SharpLink.Client/SharpLinkClient.CallOptions.cs')
text = p.read_text()
marker = '    private static SharpLinkException CreateDeadlineExceededException()\n'
helper = '''    private long GetWireTimeBudgetValue(RpcDeadline deadline)
    {
        var remaining = deadline.GetRemaining(_runtimeContext.TimeProvider);
        if (remaining <= TimeSpan.Zero)
            throw CreateDeadlineExceededException();
        return remaining.Ticks;
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
            if (!reader.TryReadLittleEndian(out long timeBudgetTicks))
            {
                throw new SharpLinkProtocolViolationException(
                    ProtocolViolationReason.MalformedFrame,
                    "Request time budget is truncated.");
            }
            if (timeBudgetTicks < 0)
            {
                throw new SharpLinkProtocolViolationException(
                    ProtocolViolationReason.MalformedFrame,
                    "Request time budget cannot be negative.");
            }
            deadline = RpcDeadline.Create(TimeSpan.FromTicks(timeBudgetTicks), timeProvider);
        }
'''
text = text[:start] + replacement + text[end:]
p.write_text(text)
