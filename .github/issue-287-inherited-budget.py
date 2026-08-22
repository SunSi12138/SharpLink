from pathlib import Path
import re

# Preserve the receiver-local monotonic deadline and its TimeProvider in the existing ambient
# server call snapshot. Public Deadline remains an informational local UTC projection; downstream
# propagation uses the monotonic boundary through these internal fields.
p = Path('src/SharpLink.Abstractions/SharpLinkCallContextSnapshot.cs')
text = p.read_text()
text = text.replace(
    '    private readonly long _deadlineOffsetTicks;\n',
    '    private readonly long _deadlineOffsetTicks;\n\n    internal RpcDeadline LocalRpcDeadline { get; }\n    internal TimeProvider? DeadlineTimeProvider { get; }\n')
old_public_tail = '''        Metadata = metadata;
    }

    /// <summary>Gets the transport session identifier.</summary>'''
new_public_tail = '''        Metadata = metadata;
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
        if (deadline.UtcDeadline is { } value)
        {
            _deadlineTicks = value.Ticks;
            _deadlineOffsetTicks = value.Offset.Ticks;
        }
        else
        {
            _deadlineTicks = NoDeadlineTicks;
        }
        Metadata = metadata;
    }

    /// <summary>Gets the transport session identifier.</summary>'''
assert old_public_tail in text
text = text.replace(old_public_tail, new_public_tail)
p.write_text(text)

# Server interceptor contexts must retain the same local monotonic deadline information.
p = Path('src/SharpLink.Abstractions/SharpLinkInterceptors.cs')
text = p.read_text()
text = text.replace(
    '''        SharpLinkAuthenticationContext? authentication,
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken,''',
    '''        SharpLinkAuthenticationContext? authentication,
        RpcDeadline deadline,
        TimeProvider deadlineTimeProvider,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken,''')
text = text.replace(
    ': base(connectionId, authentication, deadline, metadata)',
    ': base(connectionId, authentication, deadline, deadlineTimeProvider, metadata)')
p.write_text(text)

# Connection snapshots retain the server runtime's monotonic time domain.
p = Path('src/SharpLink.Server/ServerConnectionState.cs')
text = p.read_text()
text = text.replace(
    '    private readonly CancellationToken _connectionToken;\n',
    '    private readonly CancellationToken _connectionToken;\n    private readonly TimeProvider _timeProvider;\n')
text = text.replace(
    '''        DeadlineScheduler = new ServerCallDeadlineScheduler(
            CallCancellations,
            maxConcurrentCalls,
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        _connectionCancellation''',
    '''        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        DeadlineScheduler = new ServerCallDeadlineScheduler(
            CallCancellations,
            maxConcurrentCalls,
            _timeProvider);
        _connectionCancellation''')
text = text.replace(
    '''    internal SharpLinkCallContextSnapshot GetCallContextSnapshot(
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata)
    {
        if (deadline is null && metadata is null &&''',
    '''    internal SharpLinkCallContextSnapshot GetCallContextSnapshot(
        RpcDeadline deadline,
        SharpLinkMetadata? metadata)
    {
        if (!deadline.HasValue && metadata is null &&''')
text = text.replace(
    '''        return new SharpLinkCallContextSnapshot(
            Session.Id,
            Volatile.Read(ref _authenticationContext),
            deadline,
            metadata);''',
    '''        return new SharpLinkCallContextSnapshot(
            Session.Id,
            Volatile.Read(ref _authenticationContext),
            deadline,
            _timeProvider,
            metadata);''')
p.write_text(text)

# Carry RpcDeadline (not only its UTC projection) into the ambient server context.
p = Path('src/SharpLink.Server/SharpLinkServer.cs')
text = p.read_text()
text = text.replace(
    '''        long methodId,
        long requestId,
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata,''',
    '''        long methodId,
        long requestId,
        RpcDeadline deadline,
        SharpLinkMetadata? metadata,''')
text = text.replace(
    '''            connection.AuthenticationContext,
            deadline,
            metadata,
            cancellationToken,
            interceptors);''',
    '''            connection.AuthenticationContext,
            deadline,
            _runtimeContext.TimeProvider,
            metadata,
            cancellationToken,
            interceptors);''')
text = text.replace(
    '''        SharpLinkAuthenticationContext? authenticationContext,
        DateTimeOffset? deadline,
        SharpLinkMetadata? metadata,''',
    '''        SharpLinkAuthenticationContext? authenticationContext,
        RpcDeadline deadline,
        TimeProvider deadlineTimeProvider,
        SharpLinkMetadata? metadata,''')
text = text.replace(
    '''            authenticationContext,
            deadline,
            metadata,
            cancellationToken,''',
    '''            authenticationContext,
            deadline,
            deadlineTimeProvider,
            metadata,
            cancellationToken,''')
# Every server call-context construction from a decoded request must use the local monotonic value.
text = re.sub(
    r'(CreateCallContext\(\s*connection,\s*serviceInfo\.Stub,\s*request\.MethodHash,\s*requestId,\s*)request\.Deadline(,\s*request\.Metadata)',
    r'\1request.RpcDeadline\2',
    text)
p.write_text(text)

# Stage 2 lifetime semantics: an ambient parent RPC is a cap after local method/client policy
# selection. Compute the parent's remaining duration in the parent's own TimeProvider domain,
# then resolve one child-local monotonic deadline. This avoids comparing monotonic timestamps
# from different providers/process-local domains.
p = Path('src/SharpLink.Client/SharpLinkClient.CallOptions.cs')
text = p.read_text()
old = '''        var timeProvider = _runtimeContext.TimeProvider;
        var deadline = selectedTimeout is { } timeout
            ? RpcDeadline.Create(timeout, timeProvider)
            : default;'''
new = '''        var ambientCall = SharpLinkCallContext.Current;
        if (ambientCall is not null &&
            ambientCall.LocalRpcDeadline.HasValue &&
            ambientCall.DeadlineTimeProvider is { } inheritedTimeProvider)
        {
            var inheritedRemaining = ambientCall.LocalRpcDeadline.GetRemaining(inheritedTimeProvider);
            if (inheritedRemaining <= TimeSpan.Zero)
                throw CreateDeadlineExceededException();
            if (selectedTimeout is null || inheritedRemaining < selectedTimeout.Value)
                selectedTimeout = inheritedRemaining;
        }

        var timeProvider = _runtimeContext.TimeProvider;
        var deadline = selectedTimeout is { } timeout
            ? RpcDeadline.Create(timeout, timeProvider)
            : default;'''
assert old in text
text = text.replace(old, new)
p.write_text(text)

# The server has an explicit handshake implementation; enforce the same breaking minor boundary
# there, not only in the reusable negotiator used by the client/tests.
server_handshake_patched = 0
for p in Path('src/SharpLink.Server').rglob('*.cs'):
    text = p.read_text()
    marker = '''                        var unsupportedRequired = request.RequiredCapabilities & ~supportedCapabilities;
                        if (unsupportedRequired != ProtocolV2Capabilities.None)'''
    if marker not in text:
        continue
    replacement = '''                        var unsupportedRequired = request.RequiredCapabilities & ~supportedCapabilities;
                        if (request.MinorVersion < ProtocolV2Constants.MinimumCompatibleMinorVersion)
                        {
                            authResult = SharpLinkAuthenticationResult.Reject(
                                SharpLinkErrorCode.Unimplemented,
                                $"Protocol minor version {request.MinorVersion} is incompatible; " +
                                $"minimum supported is {ProtocolV2Constants.MinimumCompatibleMinorVersion}.");
                        }
                        else if (unsupportedRequired != ProtocolV2Capabilities.None)'''
    p.write_text(text.replace(marker, replacement))
    server_handshake_patched += 1
assert server_handshake_patched == 1, server_handshake_patched
