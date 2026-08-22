from pathlib import Path
import re

# AddTimeout is shared by the Health partial. It remains a local relative-time helper; only the
# cross-machine absolute-deadline wire representation is being removed.
p = Path('src/SharpLink.Client/SharpLinkClient.CallOptions.cs')
text = p.read_text()
if 'private static DateTimeOffset AddTimeout(' not in text:
    marker = '    private static SharpLinkException CreateDeadlineExceededException()\n'
    helper = '''    private static DateTimeOffset AddTimeout(DateTimeOffset now, TimeSpan timeout)
    {
        var maximum = DateTimeOffset.MaxValue - now;
        return timeout >= maximum ? DateTimeOffset.MaxValue : now.Add(timeout);
    }

'''
    assert marker in text
    text = text.replace(marker, helper + marker)
p.write_text(text)

# Preserve metadata-partition coverage without reintroducing a per-call options bag. The test
# interceptor assigns A/A/B to the three calls, exercising the same server admission partitioning
# through envelope metadata.
p = Path('test/SharpLink.IntegrationTests/IntegrationBehaviorTests.cs')
text = p.read_text()
old = '''    [Test]
    public async Task PartitionSelectorShouldIsolateMetadataKeys()
    {
        await using var harness = await TestHarness.CreateAsync(serverConfigure: builder =>
            builder.UseAdmissionControl(options => options.UsePartition(
                context => context.Metadata is { Count: > 0 } metadata ? metadata[0].Value : null,
                partition =>
                {
                    partition.MaxPartitions = 8;
                    partition.UseConcurrency(1);
                })));
        var service = harness.Client.Get<ITestService>();
        using var cancellation = new CancellationTokenSource();
        var tenantA = new SharpLinkCallOptions
        {
            Metadata = new SharpLinkMetadata(new KeyValuePair<string, string>("tenant", "a"))
        };
        var tenantB = new SharpLinkCallOptions
        {
            Metadata = new SharpLinkMetadata(new KeyValuePair<string, string>("tenant", "b"))
        };
        var active = service.SlowAddWithOptionsAsync(1, 2, tenantA, cancellation.Token).AsTask();
        await Task.Delay(75);

        await EnsureThrowsSharpLinkFast(
            service.DescribeCallAsync(1, tenantA, CancellationToken.None).AsTask(),
            "same partition concurrency",
            SharpLinkErrorCode.ResourceExhausted);
        var other = await service.DescribeCallAsync(2, tenantB, CancellationToken.None);
        Ensure(other.StartsWith("2:b:", StringComparison.Ordinal), "independent partition permit");
        cancellation.Cancel();
        await EnsureThrows<OperationCanceledException>(active, "partition active cancellation");
    }
'''
new = '''    [Test]
    public async Task PartitionSelectorShouldIsolateMetadataKeys()
    {
        var metadataInterceptor = new SequencedTenantMetadataInterceptor();
        await using var harness = await TestHarness.CreateAsync(
            serverConfigure: builder => builder.UseAdmissionControl(options => options.UsePartition(
                context => context.Metadata is { Count: > 0 } metadata ? metadata[0].Value : null,
                partition =>
                {
                    partition.MaxPartitions = 8;
                    partition.UseConcurrency(1);
                })),
            clientInterceptor: metadataInterceptor);
        var service = harness.Client.Get<ITestService>();
        TestService.ResetBlockingAdd();
        var active = service.BlockingAddAsync(1, 2, CancellationToken.None).AsTask();
        await TestService.WaitForBlockingAddStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await EnsureThrowsSharpLinkFast(
            service.AddAsync(1, 1).AsTask(),
            "same partition concurrency",
            SharpLinkErrorCode.ResourceExhausted);
        Ensure(await service.AddAsync(2, 2) == 4, "independent metadata partition permit");

        TestService.ReleaseBlockingAdd();
        Ensure(await active == 3, "partition active call completion");
    }
'''
assert old in text
text = text.replace(old, new)

# Allow this integration harness to install a capability-specific metadata interceptor.
old_signature = '''            Action<SharpLinkRuntimeOptions>? clientRuntimeConfigure = null,
            Action<SharpLinkServerBuilder>? serverConfigure = null,
            IRpcCodec<Person>? personCodec = null)'''
new_signature = '''            Action<SharpLinkRuntimeOptions>? clientRuntimeConfigure = null,
            Action<SharpLinkServerBuilder>? serverConfigure = null,
            IRpcCodec<Person>? personCodec = null,
            ISharpLinkClientInterceptor? clientInterceptor = null)'''
assert old_signature in text
text = text.replace(old_signature, new_signature)
old_builder = '''            if (poolConfigure is not null)
                clientBuilder.UseConnectionPool(poolConfigure);

            if (disableRequestTimeout)'''
new_builder = '''            if (poolConfigure is not null)
                clientBuilder.UseConnectionPool(poolConfigure);
            if (clientInterceptor is not null)
                clientBuilder.AddInterceptor(clientInterceptor);

            if (disableRequestTimeout)'''
assert old_builder in text
text = text.replace(old_builder, new_builder)

marker = '    private sealed class TestHarness : IAsyncDisposable\n'
interceptor = '''    private sealed class SequencedTenantMetadataInterceptor : ISharpLinkClientInterceptor
    {
        private int _invocationCount;

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            var invocation = Interlocked.Increment(ref _invocationCount);
            var tenant = invocation <= 2 ? "a" : "b";
            context.Metadata = new SharpLinkMetadata(
                new KeyValuePair<string, string>("tenant", tenant));
            return next(context);
        }
    }

'''
assert marker in text
text = text.replace(marker, interceptor + marker)
p.write_text(text)
