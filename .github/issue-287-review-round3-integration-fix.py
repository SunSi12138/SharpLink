from pathlib import Path


def replace_once(path: str, old: str, new: str):
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    assert count == 1, (path, count, old[:120])
    p.write_text(text.replace(old, new, 1))


# Keep the existing Get<T>() reflection/API shape unambiguous. Caller-selected metadata is a
# separate capability rather than an overload of the longstanding proxy factory.
for path in [
    'src/SharpLink.Abstractions/ISharpLinkClient.cs',
    'src/SharpLink.Abstractions/ISharpLinkMultiClusterClient.cs',
]:
    p = Path(path)
    text = p.read_text()
    old = 'TContract Get<TContract>(SharpLinkMetadata metadata) where TContract : IService'
    assert text.count(old) == 1, path
    p.write_text(text.replace(old,
        'TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService', 1))

replace_once(
    'src/SharpLink.Client/SharpLinkClient.Lifecycle.cs',
    '    public T Get<T>(SharpLinkMetadata metadata) where T : IService',
    '    public T GetWithMetadata<T>(SharpLinkMetadata metadata) where T : IService')

p = Path('src/SharpLink.Client/SharpLinkMultiClusterClient.cs')
text = p.read_text()
assert text.count('    public TContract Get<TContract>(SharpLinkMetadata metadata) where TContract : IService') == 1
text = text.replace(
    '    public TContract Get<TContract>(SharpLinkMetadata metadata) where TContract : IService',
    '    public TContract GetWithMetadata<TContract>(SharpLinkMetadata metadata) where TContract : IService', 1)
assert text.count('route.Slot.Client.Get<TContract>(metadata)') == 1
text = text.replace('route.Slot.Client.Get<TContract>(metadata)',
                    'route.Slot.Client.GetWithMetadata<TContract>(metadata)', 1)
p.write_text(text)

# Preserve the reviewed capability with concurrent caller-selected metadata while using the
# capability-specific API name.
p = Path('test/SharpLink.IntegrationTests/IntegrationBehaviorTests.cs')
text = p.read_text()
assert text.count('harness.Client.Get<ITestService>(new SharpLinkMetadata(') == 2
text = text.replace('harness.Client.Get<ITestService>(new SharpLinkMetadata(',
                    'harness.Client.GetWithMetadata<ITestService>(new SharpLinkMetadata(')

# Absolute wall-clock Deadline is intentionally no longer public on SharpLinkCallContext.
# The behavior contract is that the method policy still expires the RPC with DeadlineExceeded.
text = text.replace(
    'public async Task MethodTimeoutShouldPopulateCallContextAndExpire()',
    'public async Task MethodTimeoutShouldExpireWithoutPublicCallContextDeadline()', 1)
old = '''        Ensure(summary.StartsWith("42:missing:deadline", StringComparison.Ordinal),
            "method timeout should surface as a server call-context deadline");'''
new = '''        Ensure(summary.StartsWith("42:missing:no-deadline", StringComparison.Ordinal),
            "method timeout should not recreate a public absolute call-context deadline");'''
assert text.count(old) == 1
p.write_text(text.replace(old, new, 1))
