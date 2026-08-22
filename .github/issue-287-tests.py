from pathlib import Path
import re


def remove_test_blocks_containing(path: str, needle: str) -> None:
    p = Path(path)
    text = p.read_text()
    marker = '\n    [Test]\n'
    parts = text.split(marker)
    if len(parts) == 1:
        return
    kept = [parts[0]]
    for part in parts[1:]:
        if needle in part:
            continue
        kept.append(part)
    p.write_text(marker.join(kept))

# Interceptor demos now mutate explicit envelope metadata rather than a call-options bag.
p = Path('demo/InterceptorsTelemetry/Program.cs')
text = p.read_text()
old = '''        context.Options = context.Options with
        {
            Metadata = new SharpLinkMetadata(
                new KeyValuePair<string, string>("demo", "interceptor"))
        };'''
new = '''        context.Metadata = new SharpLinkMetadata(
            new KeyValuePair<string, string>("demo", "interceptor"));'''
assert old in text
p.write_text(text.replace(old, new))

# Low-level test helper exposes only metadata + cancellation, matching IRpcChannel.
p = Path('test/SharpLink.UnitTests/Client/ClientInvokerTestHelper.cs')
text = p.read_text()
text = text.replace('SharpLinkCallOptions options = default,', 'SharpLinkMetadata? metadata = null,')
text = re.sub(r'\boptions,\n            cancellationToken\);', 'metadata,\n            cancellationToken);', text)
p.write_text(text)

# Obsolete value-type API coverage is removed with the API.
Path('test/SharpLink.UnitTests/Abstractions/SharpLinkCallOptionsTests.cs').unlink()

# SDK forwarding no longer advertises the removed contract type.
p = Path('test/SharpLink.UnitTests/SdkTypeForwardingTests.cs')
text = p.read_text().replace('        "SharpLink.Sdk.SharpLinkCallOptions",\n', '')
p.write_text(text)

# Existing CallOptions-specific client tests exercised removed per-call deadline/wait-ready knobs.
# Retain unrelated admission/endpoint tests in the file and migrate wire terminology.
remove_test_blocks_containing(
    'test/SharpLink.UnitTests/Client/SharpLinkClientCallOptionsTests.cs',
    'SharpLinkCallOptions')
p = Path('test/SharpLink.UnitTests/Client/SharpLinkClientCallOptionsTests.cs')
text = p.read_text().replace('SharpLinkClientCallOptionsTests', 'SharpLinkClientCallControlTests')
text = text.replace('ProtocolV2FrameFlags.HasDeadline', 'ProtocolV2FrameFlags.HasTimeBudget')
p.write_text(text)

# Per-call timeout unit test becomes a client-policy timeout test.
p = Path('test/SharpLink.UnitTests/Client/SharpLinkClientTimeoutTests.cs')
text = p.read_text()
text = text.replace(
    'await using var client = ClientBuilderTestHelper.Build(transport);\n\n        await client.ConnectAsync();\n\n        var invokeTask = ClientInvokerTestHelper.InvokeUnaryAsync(\n            client,\n            new SharpLinkCallOptions { Timeout = TimeSpan.FromMilliseconds(80) }).AsTask();',
    'await using var client = ClientBuilderTestHelper.Build(\n            transport,\n            builder => builder.UseRequestTimeout(TimeSpan.FromMilliseconds(80)));\n\n        await client.ConnectAsync();\n\n        var invokeTask = ClientInvokerTestHelper.InvokeUnaryAsync(client).AsTask();',
    1)
p.write_text(text)

# Retry deadline tests use the client-level lifetime policy. The one test whose purpose was
# specifically per-call WaitForReady is removed.
p = Path('test/SharpLink.UnitTests/Client/SharpLinkClientRetryTests.cs')
text = p.read_text()
text = text.replace(
    'deadlineTransport, policy: null, maxAttempts: 2, initialBackoff: TimeSpan.FromMilliseconds(50));',
    'deadlineTransport, policy: null, maxAttempts: 2, initialBackoff: TimeSpan.FromMilliseconds(50),\n            requestTimeout: TimeSpan.FromMilliseconds(20));')
text = text.replace(
    '''var deadlineInvocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(
            deadlineClient, new SharpLinkCallOptions { Timeout = TimeSpan.FromMilliseconds(20) }).AsTask();''',
    'var deadlineInvocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(deadlineClient).AsTask();')
text = text.replace(
    'await using var client = CreateRetryClient(transport, policy, maxAttempts: 2);',
    'await using var client = CreateRetryClient(\n            transport, policy, maxAttempts: 2, requestTimeout: TimeSpan.FromSeconds(1));',
    1)
text = text.replace(
    '''var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(
            client,
            new SharpLinkCallOptions { Timeout = TimeSpan.FromSeconds(1) }).AsTask();''',
    'var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();',
    1)
# Remove obsolete WaitForReady-specific retry-admission test.
pattern = re.compile(
    r'\n    \[Test\]\n    public async Task ClientStopShouldCancelRetryAdmissionDelayPromptly\(\)\n    \{.*?\n    \}\n(?=\n    \[Test\])',
    re.S)
text, count = pattern.subn('\n', text, count=1)
assert count == 1
text = text.replace(
    'builder.UseEndpointAdmission(admission);\n            });\n        try\n        {\n            await client.ConnectAsync();\n            var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(\n                client,\n                new SharpLinkCallOptions { Timeout = TimeSpan.FromSeconds(5) }).AsTask();',
    'builder.UseEndpointAdmission(admission);\n                builder.UseRequestTimeout(TimeSpan.FromSeconds(5));\n            });\n        try\n        {\n            await client.ConnectAsync();\n            var invocation = ClientInvokerTestHelper.InvokeIdempotentUnaryAsync(client).AsTask();')
text = text.replace(
    '''        TimeSpan? initialBackoff = null)
    {
        var options = RetryOptions(maxAttempts, initialBackoff ?? TimeSpan.Zero);''',
    '''        TimeSpan? initialBackoff = null,
        TimeSpan? requestTimeout = null)
    {
        var options = RetryOptions(maxAttempts, initialBackoff ?? TimeSpan.Zero);''')
text = text.replace(
    '''            ConfigureRetry(builder, options);
            if (policy is not null)''',
    '''            ConfigureRetry(builder, options);
            if (requestTimeout is { } timeout)
                builder.UseRequestTimeout(timeout);
            if (policy is not null)''')
p.write_text(text)

# Generated integration contracts no longer expose control parameters.
p = Path('test/SharpLink.IntegrationTests/IntegrationBehaviorTests.cs')
text = p.read_text()
start = text.index('    [Test]\n    public async Task CallOptionsShouldCarryMetadataAndUseEarliestDeadline()')
end = text.index('\n    [Test]', start + 10)
replacement = '''    [Test]
    public async Task MethodTimeoutShouldPopulateCallContextAndExpire()
    {
        await using var harness = await TestHarness.CreateAsync();
        var svc = harness.Client.Get<ITestService>();

        var summary = await svc.DescribeCallAsync(42, CancellationToken.None);
        Ensure(summary.StartsWith("42:missing:deadline", StringComparison.Ordinal),
            "method timeout should surface as a server call-context deadline");

        await EnsureThrowsSharpLinkFast(
            svc.SlowAddWithMethodTimeoutAsync(1, 2, CancellationToken.None).AsTask(),
            "method timeout",
            SharpLinkErrorCode.DeadlineExceeded);
    }
'''
text = text[:start] + replacement + text[end:]
text = text.replace('ValueTask<int> SlowAddWithOptionsAsync(', '[Sdk.Timeout(0.1)]\n    ValueTask<int> SlowAddWithMethodTimeoutAsync(')
text = text.replace('        SharpLinkCallOptions options,\n        CancellationToken cancellationToken);', '        CancellationToken cancellationToken);', 1)
text = text.replace('    ValueTask<string> DescribeCallAsync(\n        int value,\n        SharpLinkCallOptions options,\n        CancellationToken cancellationToken);',
                    '    [Sdk.Timeout(2)]\n    ValueTask<string> DescribeCallAsync(\n        int value,\n        CancellationToken cancellationToken);')
text = text.replace('public async ValueTask<int> SlowAddWithOptionsAsync(', 'public async ValueTask<int> SlowAddWithMethodTimeoutAsync(')
text = text.replace('        SharpLinkCallOptions options,\n        CancellationToken cancellationToken)\n    {\n        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);',
                    '        CancellationToken cancellationToken)\n    {\n        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);', 1)
text = text.replace('    public ValueTask<string> DescribeCallAsync(\n        int value,\n        SharpLinkCallOptions options,\n        CancellationToken cancellationToken)',
                    '    public ValueTask<string> DescribeCallAsync(\n        int value,\n        CancellationToken cancellationToken)')
p.write_text(text)

# Interceptor integration is now the explicit metadata propagation coverage.
p = Path('test/SharpLink.IntegrationTests/InterceptorIntegrationTests.cs')
text = p.read_text()
text = text.replace('var result = await service.DescribeAsync(17, default);', 'var result = await service.DescribeAsync(17);')
old = '''            context.Options = context.Options with
            {
                Metadata = new SharpLinkMetadata(
                    new KeyValuePair<string, string>("source", "client-interceptor"))
            };'''
new = '''            context.Metadata = new SharpLinkMetadata(
                new KeyValuePair<string, string>("source", "client-interceptor"));'''
assert old in text
text = text.replace(old, new)
text = text.replace('ValueTask<string> DescribeAsync(int value, SharpLinkCallOptions options);', 'ValueTask<string> DescribeAsync(int value);')
text = text.replace('public ValueTask<string> DescribeAsync(int value, SharpLinkCallOptions options)', 'public ValueTask<string> DescribeAsync(int value)')
text = text.replace('        var source = options.Metadata is { Count: > 0 } metadata\n            ? metadata[0].Value\n            : "missing";\n        var context = SharpLinkCallContext.Current;',
                    '        var context = SharpLinkCallContext.Current;\n        var source = context?.Metadata is { Count: > 0 } metadata\n            ? metadata[0].Value\n            : "missing";')
p.write_text(text)

# Wire flag terminology across test sources.
for p in Path('test').rglob('*.cs'):
    text = p.read_text()
    if 'ProtocolV2FrameFlags.HasDeadline' in text:
        p.write_text(text.replace('ProtocolV2FrameFlags.HasDeadline', 'ProtocolV2FrameFlags.HasTimeBudget'))
