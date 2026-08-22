from pathlib import Path

# Keep the common unbounded admission call shape as an internal helper. The overload no longer
# reconstructs a deadline from public wall-clock context; production dispatch passes RpcDeadline
# explicitly when one exists.
p = Path('src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs')
text = p.read_text()
marker = '''    internal ValueTask<AdmissionDecision> AcquireAsync(
        SharpLinkAdmissionContext context,
        int retainedBytes,
        bool allowQueue,
        RpcDeadline deadline,
        CancellationToken cancellationToken)
'''
assert marker in text
helper = '''    internal ValueTask<AdmissionDecision> AcquireAsync(
        SharpLinkAdmissionContext context,
        int retainedBytes,
        bool allowQueue,
        CancellationToken cancellationToken)
        => AcquireAsync(
            context,
            retainedBytes,
            allowQueue,
            default,
            cancellationToken);

'''
text = text.replace(marker, helper + marker, 1)
p.write_text(text)

# Admission benchmark/test contexts no longer carry a public absolute deadline.
p = Path('test/SharpLink.Benchmarks/AdmissionBenchmarks.cs')
text = p.read_text().replace(
    '            1, 2, RpcMethodKind.Unary, "benchmark", null, null, null);',
    '            1, 2, RpcMethodKind.Unary, "benchmark", null, null);')
p.write_text(text)

p = Path('test/SharpLink.UnitTests/Builder/BuildPlanBuilderTests.cs')
text = p.read_text().replace(
    '''                authenticationContext: null,
                metadata: null,
                deadline: null);''',
    '''                authenticationContext: null,
                metadata: null);''')
p.write_text(text)

p = Path('test/SharpLink.UnitTests/Server/AdmissionControlTests.cs')
text = p.read_text()
text = text.replace(
    '''        var deadlineContext = new SharpLinkAdmissionContext(
            1,
            2,
            RpcMethodKind.Unary,
            "connection",
            authenticationContext: null,
            metadata: null,
            DateTimeOffset.UtcNow.AddMilliseconds(50));

        var rejected = await controller.AcquireAsync(
            deadlineContext, 1, allowQueue: true, CancellationToken.None);''',
    '''        var deadlineContext = new SharpLinkAdmissionContext(
            1,
            2,
            RpcMethodKind.Unary,
            "connection",
            authenticationContext: null,
            metadata: null);
        var deadline = RpcDeadline.Create(
            DateTimeOffset.UtcNow.AddMilliseconds(50),
            TimeProvider.System);

        var rejected = await controller.AcquireAsync(
            deadlineContext,
            retainedBytes: 1,
            allowQueue: true,
            deadline: deadline,
            cancellationToken: CancellationToken.None);''')
text = text.replace(
    '        => new(1, 2, RpcMethodKind.Unary, "connection", null, null, null);',
    '        => new(1, 2, RpcMethodKind.Unary, "connection", null, null);')
p.write_text(text)

# Server connection snapshots assert the internal monotonic boundary instead of the removed public
# DateTimeOffset projection.
p = Path('test/SharpLink.UnitTests/Server/ServerConnectionStateTests.cs')
text = p.read_text()
text = text.replace(
    '        Ensure(callContext.Deadline is null, "default call context deadline");',
    '        Ensure(!callContext.LocalRpcDeadline.HasValue, "default call context deadline");')
text = text.replace(
    '        Ensure(ReferenceEquals(callContext, state.GetCallContextSnapshot(null, null)),',
    '        Ensure(ReferenceEquals(callContext, state.GetCallContextSnapshot(default, null)),')
text = text.replace(
    '''        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var deadlineContext = state.GetCallContextSnapshot(deadline, null);
        Ensure(!ReferenceEquals(callContext, deadlineContext), "deadline calls must not reuse the default context");
        Ensure(deadlineContext.Deadline == deadline, "deadline call context");''',
    '''        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(30), TimeProvider.System);
        var deadlineContext = state.GetCallContextSnapshot(deadline, null);
        Ensure(!ReferenceEquals(callContext, deadlineContext), "deadline calls must not reuse the default context");
        Ensure(deadlineContext.LocalRpcDeadline.HasValue &&
               deadlineContext.LocalRpcDeadline.Timestamp == deadline.Timestamp,
            "deadline call context");''')
text = text.replace(
    '        var metadataContext = state.GetCallContextSnapshot(null, metadata);',
    '        var metadataContext = state.GetCallContextSnapshot(default, metadata);')
p.write_text(text)

# The new SendPump regression reads the wire budget directly.
p = Path('test/SharpLink.UnitTests/Runtime/SendPumpTests.cs')
text = p.read_text()
if 'using System.Buffers.Binary;\n' not in text:
    text = 'using System.Buffers.Binary;\n' + text
p.write_text(text)

# Public server call context intentionally no longer exposes lifetime as a wall-clock value. Keep
# this integration service focused on metadata; deadline behavior is covered by runtime tests.
p = Path('test/SharpLink.IntegrationTests/IntegrationBehaviorTests.cs')
text = p.read_text()
text = text.replace(
    '        var deadline = context?.Deadline is null ? "no-deadline" : "deadline";',
    '        const string deadline = "no-deadline";')
text = text.replace(':deadline", StringComparison.Ordinal), "metadata/deadline call context"',
                    ':no-deadline", StringComparison.Ordinal), "metadata call context"')
p.write_text(text)
