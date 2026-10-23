namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueuedCompressedPersistentPayloadShouldKeepItsSelectedGeneration(bool replace)
    {
        var compression = new QueuedCompressionProvider();
        await using var harness = await DynamicHarness.CreateAsync(compressionProvider: compression);
        using var plugin = PluginBundle.Load($"queue-compressed-{replace}");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin);
        var server = (SharpLinkServer)harness.Server;
        var proxy = GetProxy(harness.Client, plugin.ContractType);
        var payloadType = plugin.ContractAssembly.GetType("SharpLink.DynamicPlugin.DynamicPayload", throwOnError: true)!;
        var payload = Activator.CreateInstance(payloadType)!;
        payloadType.GetProperty("Value")!.SetValue(payload, 42);
        payloadType.GetProperty("Label")!.SetValue(payload, new string('q', 16 * 1024));
        payloadType.GetProperty("Parent")!.SetValue(payload, payload);
        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var replacement = replacementContext.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
        var holder = await HoldQueuedAdmissionAsync(server);
        Task<int>? queued = null;
        try
        {
            queued = InvokeValueTaskAsync<int>(
                proxy, plugin.ContractType, "UsePayloadAsync", payload, CancellationToken.None).AsTask();
            await WaitUntilAsync(() => server.CurrentAdmissionProgramForTests!.Kernel.QueuedCalls == 1);
            Ensure(compression.CompressedRequests > 0, "the retained queued request is compressed on the wire");
            Ensure(compression.DecodedRequests == 0, "admission precedes compressed persistent decoding");
            if (replace)
            {
                var result = await server.ReplaceAssemblyAsync(plugin.ServiceAssembly, replacement, TimeSpan.Zero);
                Ensure(result.Succeeded && result.ReferencesReleased, "queued compressed R1 retires normally");
            }
            holder.Lease!.Dispose();
            if (replace)
            {
                await EnsureQueuedSelectionUnavailableAsync(queued);
                Ensure(compression.DecodedRequests == 0, "retired selected generation is rejected before decoding");
                Ensure(plugin.GetStaticInt("Created") == 0, "retired compressed request does not activate R1");
            }
            else
            {
                Ensure(await queued.WaitAsync(TimeSpan.FromSeconds(5)) == 42 + 16 * 1024,
                    "queued compressed persistent payload preserves its circular reference and value");
                Ensure(compression.DecodedRequests == 1, "selected live generation decodes exactly once");
            }
            Ensure(await InvokeValueTaskAsync<int>(
                proxy, plugin.ContractType, "UnaryAsync", 41, CancellationToken.None) == 42,
                "the connection remains usable after compressed queue completion");
            EnsureClientAndServerCountersAreZero(harness, "queued compressed generation");
        }
        finally
        {
            holder.Lease!.Dispose();
            if (queued is not null)
                await ObserveQueuedTerminalAsync(queued);
            _ = await server.UnregisterAssemblyAsync(replace ? replacement : plugin.ServiceAssembly, TimeSpan.FromSeconds(2));
            replacementContext.Unload();
        }
    }

    private sealed class QueuedCompressionProvider : ISharpLinkCompressionProvider
    {
        private readonly TestCompressionProvider _inner = new();
        private int _compressedRequests;
        private int _decodedRequests;

        public string WireProfile => _inner.WireProfile;
        internal int CompressedRequests => Volatile.Read(ref _compressedRequests);
        internal int DecodedRequests => Volatile.Read(ref _decodedRequests);

        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            var compressed = _inner.TryCompress(input, output, maxOutputBytes, cancellationToken);
            if (compressed)
                Interlocked.Increment(ref _compressedRequests);
            return compressed;
        }

        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _decodedRequests);
            _inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }
}
