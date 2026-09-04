from pathlib import Path


def read(path: str) -> str:
    return Path(path).read_text()


def write(path: str, text: str) -> None:
    Path(path).write_text(text)


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected one occurrence, found {count}: {old[:120]!r}")
    write(path, text.replace(old, new, 1))


# Remove the old Client array interpreter, per-layer one-shot continuation,
# continuation join state, and pool. The composed root/terminal immediately
# precede this dead fallback after the prototype patch is applied.
client = 'src/SharpLink.Client/SharpLinkClient.Interceptors.cs'
text = read(client)
start = text.index('        private ValueTask<SharpLinkClientInvocationResult> InvokeNextAsync(\n')
end = text.index('        private ValueTask<SharpLinkClientInvocationResult> InvokeTerminalTrackedAsync(\n', start)
text = text[:start] + text[end:]
write(client, text)

# The optimized Server path stores terminal facts in the already-existing
# invocation context, so the old interpreter/facts/continuation/pool class is
# entirely dead and can be removed.
server = 'src/SharpLink.Server/SharpLinkServer.Interceptors.cs'
text = read(server)
start = text.index('    private sealed class ServerPipelineFacts')
end = text.index('    private static bool IsCancellationException(Exception exception)', start)
text = text[:start] + text[end:]
write(server, text)

# The source interceptor array is only a publication input now. Nodes retain the
# interceptor instances for generation lifetime, so no duplicate snapshot field
# is needed after composition.
for path, type_name, interceptor_type, delegate_type, terminal_sig in [
    (
        'src/SharpLink.Client/SharpLinkClient.InterceptorGeneration.cs',
        'ClientInterceptorGeneration',
        'ISharpLinkClientInterceptor',
        'SharpLinkClientInvocationDelegate',
        'SharpLinkClientInvocationResult'),
    (
        'src/SharpLink.Server/SharpLinkServer.InterceptorGeneration.cs',
        'ServerInterceptorGeneration',
        'ISharpLinkServerInterceptor',
        'SharpLinkServerInvocationDelegate',
        None),
]:
    text = read(path)
    text = text.replace(
        f'        private {type_name}(\n            {interceptor_type}[] snapshot,\n            {delegate_type} entry)\n        {{\n            Snapshot = snapshot;\n            Entry = entry;\n        }}\n\n        public {interceptor_type}[] Snapshot {{ get; }}\n        public int Count => Snapshot.Length;\n',
        f'        private {type_name}(int count, {delegate_type} entry)\n        {{\n            Count = count;\n            Entry = entry;\n        }}\n\n        public int Count {{ get; }}\n')
    text = text.replace(
        f'            return new {type_name}(snapshot, next);',
        f'            return new {type_name}(snapshot.Length, next);')
    if 'Snapshot' in text:
        raise SystemExit(f'{path}: source snapshot still retained after composition')
    write(path, text)

# Make the correct-use contract explicit without promising dynamic enforcement.
abstractions = 'src/SharpLink.Abstractions/SharpLinkInterceptors.cs'
replace_once(
    abstractions,
    '/// <summary>Continues a client interceptor pipeline. Interceptors should invoke, await, or return this continuation at most once and must not retain it after the interceptor returns.</summary>',
    '/// <summary>\n/// Continues a client interceptor pipeline. If used, invoke this continuation at most once and await or directly return\n/// the resulting <see cref="ValueTask{TResult}"/>. Do not retain the continuation or invocation context after\n/// <see cref="ISharpLinkClientInterceptor.InvokeAsync"/> returns. Violating these rules is an interceptor bug and is not\n/// dynamically enforced by SharpLink.\n/// </summary>')
replace_once(
    abstractions,
    '/// <summary>Continues a server interceptor pipeline. Interceptors should invoke and await this continuation at most once and must not retain it after the interceptor returns.</summary>',
    '/// <summary>\n/// Continues a server interceptor pipeline. If used, invoke this continuation at most once and await or directly return\n/// the resulting <see cref="ValueTask"/>. Do not retain the continuation or invocation context after\n/// <see cref="ISharpLinkServerInterceptor.InvokeAsync"/> returns. Violating these rules is an interceptor bug and is not\n/// dynamically enforced by SharpLink.\n/// </summary>')

# Rewrite the three tests whose old assertions were exactly the runtime misuse
# guarantees intentionally removed by semantic option 2. Replace them with
# correct-use direct-return and single-terminal characterization.
test_path = 'test/SharpLink.IntegrationTests/InterceptorIntegrationTests.cs'
text = read(test_path)

def replace_test(name: str, replacement: str) -> None:
    global text
    signature = f'    public async Task {name}()'
    sig = text.index(signature)
    start = text.rfind('    [Test]', 0, sig)
    end = text.find('\n    [Test]', sig)
    if start < 0 or end < 0:
        raise SystemExit(f'failed to locate test {name}')
    text = text[:start] + replacement.rstrip() + '\n' + text[end:]

replace_test('ServerInterceptorMustJoinAnInvokedContinuation', r'''    [Test]
    [NotInParallel]
    public async Task ServerInterceptorMayReturnContinuationDirectly()
    {
        InterceptorTestService.ResetDelayedCall();
        await using var harness = await InterceptorHarness.CreateAsync(
            serverInterceptor: new ReturningServerInterceptor());
        var call = harness.Client.Get<IInterceptorTestService>().DelayedAsync().AsTask();
        try
        {
            await InterceptorTestService.DelayedCallStarted.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(50);
            Ensure(!call.IsCompleted,
                "a directly returned Server continuation must represent downstream completion");
        }
        finally
        {
            InterceptorTestService.ReleaseDelayedCall();
        }
        Ensure(await call.WaitAsync(TimeSpan.FromSeconds(3)) == 42,
            "directly returned Server continuation response");
    }
''')

replace_test('ClientInterceptorMustJoinAnInvokedContinuation', r'''    [Test]
    [NotInParallel]
    public async Task ClientInterceptorMayReturnContinuationDirectly()
    {
        InterceptorTestService.ResetDelayedCall();
        await using var harness = await InterceptorHarness.CreateAsync(
            clientInterceptor: new ReturningClientInterceptor());
        var call = harness.Client.Get<IInterceptorTestService>().DelayedAsync().AsTask();
        try
        {
            await InterceptorTestService.DelayedCallStarted.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(50);
            Ensure(!call.IsCompleted,
                "a directly returned Client continuation must represent downstream completion");
        }
        finally
        {
            InterceptorTestService.ReleaseDelayedCall();
        }
        Ensure(await call.WaitAsync(TimeSpan.FromSeconds(3)) == 42,
            "directly returned Client continuation response");
    }
''')

replace_test('InterceptorContinuationShouldExecuteEachTerminalAtMostOnce', r'''    [Test]
    public async Task CorrectContinuationUsageShouldExecuteEachTerminalOnce()
    {
        InterceptorTestService.ResetInvocationCount();
        await using (var clientHarness = await InterceptorHarness.CreateAsync(
                         clientInterceptor: new ReturningClientInterceptor()))
        {
            _ = await clientHarness.Client.Get<IInterceptorTestService>().CountInvocationAsync();
        }
        var clientInvocationCount = InterceptorTestService.InvocationCount;

        InterceptorTestService.ResetInvocationCount();
        await using (var serverHarness = await InterceptorHarness.CreateAsync(
                         serverInterceptor: new ReturningServerInterceptor()))
        {
            _ = await serverHarness.Client.Get<IInterceptorTestService>().CountInvocationAsync();
        }
        var serverInvocationCount = InterceptorTestService.InvocationCount;

        Ensure(clientInvocationCount == 1 && serverInvocationCount == 1,
            $"correct continuations execute one terminal each; client={clientInvocationCount}, server={serverInvocationCount}");
    }
''')

# Replace misuse-only helper interceptors with correct direct-return helpers.
client_helpers_start = text.index('    private sealed class DoubleNextClientInterceptor')
client_helpers_end = text.index('    private sealed class RecordingServerInterceptor', client_helpers_start)
text = text[:client_helpers_start] + r'''    private sealed class ReturningClientInterceptor : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
            => next(context);
    }

''' + text[client_helpers_end:]

server_helpers_start = text.index('    private sealed class DoubleNextServerInterceptor')
server_helpers_end = text.index('    private sealed class DelayedFirstServerInterceptor', server_helpers_start)
text = text[:server_helpers_start] + r'''    private sealed class ReturningServerInterceptor : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
            => next(context);
    }

''' + text[server_helpers_end:]
write(test_path, text)

# Record the implementation/contract change in Unreleased.
changelog = 'CHANGELOG.md'
replace_once(
    changelog,
    '### Changed\n\n',
    '### Changed\n\n'
    '- Client and Server interceptor generations are now composed once when published instead of interpreting an interceptor array through per-RPC continuation state. Correct `next` usage is an interceptor-author contract: invoke it at most once, await or directly return it, and do not retain it after the interceptor returns. SharpLink no longer allocates/verifies per-layer duplicate, retained, or fire-and-forget continuation misuse; generation capture, deadline/re-entry guards, legal short-circuit behavior, and the response-bearing Server terminal check remain enforced.\n\n')

# Production-shape invariants: the old interpreter/pool must really be gone.
for path, forbidden in {
    client: [
        'ClientInterceptorContinuation',
        'ClientContinuationState',
        'InvokeNextAsync(',
        'JoinAndReturnAsync',
    ],
    server: [
        'ServerPipelineFacts',
        'ServerInterceptorContinuation',
        'ServerContinuationState',
        'InvokeNextAsync(',
        'JoinAndReturnAsync',
    ],
    test_path: [
        'DoubleNextClientInterceptor',
        'DoubleNextServerInterceptor',
        'AbandoningClientInterceptor',
        'AbandoningServerInterceptor',
        'MustJoinAnInvokedContinuation',
        'ExecuteEachTerminalAtMostOnce',
    ],
}.items():
    value = read(path)
    for token in forbidden:
        if token in value:
            raise SystemExit(f'{path}: forbidden legacy token remains: {token}')

print('issue 406 production cleanup applied')
