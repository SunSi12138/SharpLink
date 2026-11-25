using System.Text.Json;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyRunningDecoderKeepsOriginalPhysicalBindingThroughCoordinatedReplacement(string transport)
    {
        var decoder = new NStudyHeldServerDecoder();
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, _, ct) => VerifyNStudyRunningDecodeAsync(fixture, transport, decoder, ct),
            configureClient: builder => builder.UseRuntime(options => options.Compression.Providers.Add(new TestCompressionProvider()))
                .UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
                {
                    MinimumPayloadBytes = 256,
                    MinimumSavingsBytes = 8,
                    MinimumSavingsRatio = 0.05
                }),
            configureServer: builder => builder.UseRuntime(options =>
            {
                options.Compression.Providers.Add(decoder);
                options.FlowControl.MaxConcurrentDecodesPerServer = 1;
            }));
    }

    private static async Task VerifyNStudyRunningDecodeAsync(
        NStudyCoordinationFixture fixture, string transport, NStudyHeldServerDecoder decoder, CancellationToken ct)
    {
        // This normal generated argument crosses the persistent decode threshold.
        const int labelSize = 1024 * 1024 + 1024;
        const int expectedPayload = labelSize + 7 + 2 + 3 + 1000; // No parent in this payload.
        var (server, contracts, r1Assembly, r2Assembly, r1, r2, peer, _, _) = fixture;
        var (client, session, connection, original, sibling, receiver, b) = peer;
        var contract = contracts.ContractType;
        var payloadType = contracts.GetContractType("SharpLink.DynamicPlugin.DynamicPayload");
        var r1Module = GetDynamicModule(server, r1Assembly);
        Ensure(SharpLinkAssemblyManifestLoader.TryLoad(contracts.ContractAssembly, out var manifest).Succeeded,
            "shared generated contract metadata for the method observer");
        var method = manifest!.Contracts.Single(item => item.ContractId == original.Contract).Methods
            .Single(item => item.Name == "UsePayloadAsync").Invocation;
        var recorder = new NStudyFactsInterceptor();
        server.ReplaceInterceptors([recorder]);
        var beforeBind = session.NStudyBindOperationForDiagnostics;
        var seq = 0;
        NStudyAck? next = null;
        void Record(string step)
        {
            var counts = connection.NStudyBindings.Snapshot();
            Console.WriteLine("NDECODE " + JsonSerializer.Serialize(new
            {
                step, seq = ++seq, transport, session = original.Authority.PhysicalSession,
                oldBinding = original.BindingId, oldRegistration = original.Target.Registration,
                newBinding = next?.BindingId, newRegistration = next?.Target.Registration,
                open = counts.Open, retiring = counts.Retiring, users = counts.Users,
                workers = server.DecodeStartedWorkCountForDiagnostics, started = decoder.Started, finished = decoder.Finished,
                activeDecode = server.ActiveDecodeCountForDiagnostics,
                retainedBytes = server.RetainedCompressedBytesForDiagnostics,
                decodedBytes = server.DecodedBytesInFlightForDiagnostics,
                invoked = recorder.Visits(method.MethodId)
            }));
        }

        object CreatePayload()
        {
            var payload = Activator.CreateInstance(payloadType) ?? throw new InvalidOperationException("Expected real generated payload.");
            payloadType.GetProperty("Value")!.SetValue(payload, 7);
            payloadType.GetProperty("Label")!.SetValue(payload, new string('x', labelSize));
            payloadType.GetProperty("Values")!.SetValue(payload, new List<int> { 2, 3 });
            return payload;
        }

        try
        {
            Ensure(await InvokeValueTaskAsync<int>(receiver.Service, contract, "UnaryAsync", 1, ct) == 2 &&
                   NStudyCount(r1, "Created") == 1 && server.DecodeWorkerCountForDiagnostics == 1,
                "old generated service and the selected actual persistent executor are live");
            await WaitForNStudyAsync(() => connection.NStudyBindings.Snapshot() == (2, 0, 0), ct);

            var decoding = InvokeValueTaskAsync<int>(receiver.Service, contract, "UsePayloadAsync", CreatePayload(), ct).AsTask();
            Ensure(await decoder.FirstEntered.Task.WaitAsync(ct) >= labelSize &&
                   server.DecodeStartedWorkCountForDiagnostics == 1 &&
                   server.ActiveDecodeCountForDiagnostics == 1 && server.RetainedCompressedBytesForDiagnostics > 0 &&
                   server.DecodedBytesInFlightForDiagnostics >= labelSize &&
                   connection.NStudyBindings.Snapshot() == (2, 0, 1) && r1Module.RemainingCalls == 0 &&
                   decoder.Started == 1 && decoder.Finished == 0 && !decoding.IsCompleted &&
                   recorder.Visits(method.MethodId) == 0 && await b.TouchAsync(1, ct) == 101,
                "actual decoder uses the original grant and transport byte budgets without starting the module's service");
            await WaitForNStudyAsync(() => connection.NStudyBindings.Snapshot() == (2, 0, 1), ct);
            Record("OriginalWorkerOwnsBindingAndBytesBeforeBusiness");

            static ValueTask<bool> Approve(NStudyChange _, CancellationToken __) => ValueTask.FromResult(true);
            var joining = client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server, Approve, ct);
            var replacing = server.StageNStudyReplacementAsync(connection, r1Assembly, r2Assembly, original.Contract,
                NStudyInitiator.Server, Approve, TimeSpan.FromSeconds(15), ct);
            var activated = await joining.WaitAsync(ct) ?? throw new InvalidOperationException("Expected coordinated activation.");
            next = activated.Binding;
            Ensure(next.Value.Authority == original.Authority && next.Value.Target != original.Target &&
                   next.Value.Target == server.ReadNStudyTarget(r2Assembly, original.Contract) &&
                   next.Value.BindingId != original.BindingId && activated.Change.Original == original.Target &&
                   session.NStudyBindOperationForDiagnostics == beforeBind + 1 && !decoding.IsCompleted &&
                   connection.NStudyBindings.Snapshot() == (2, 1, 1) &&
                   server.ActiveDecodeCountForDiagnostics == 1 && server.RetainedCompressedBytesForDiagnostics > 0 &&
                   server.DecodedBytesInFlightForDiagnostics >= labelSize && recorder.Visits(method.MethodId) == 0 &&
                   decoder.Started == 1 && decoder.Finished == 0 &&
                   await InvokeValueTaskAsync<int>(receiver.Service, contract, "UnaryAsync", 5, ct) == 6 &&
                   await b.TouchAsync(2, ct) == 102 && NStudyCount(r2, "Created") == 1,
                "actual R2 activation and ordinary small RPC progress without transferring or reusing the old decode grant");
            await WaitForNStudyAsync(() => connection.NStudyBindings.Snapshot() == (2, 1, 1), ct);
            Record("R2WorksWhileOriginalDecoderAndRetiringBindingAreHeld");

            decoder.Release();
            await EnsureQueuedSelectionUnavailableAsync(decoding);
            EnsureReplacementReleased((await replacing.WaitAsync(ct)).Replacement, "original service registration retirement");
            await WaitForNStudyAsync(() => connection.NStudyBindings.Snapshot() == (2, 0, 0) &&
                server.ActiveDecodeCountForDiagnostics == 0 && server.RetainedCompressedBytesForDiagnostics == 0 &&
                server.DecodedBytesInFlightForDiagnostics == 0 && server.DecodeQueueDepthForDiagnostics == 0 &&
                server.DecodeQueueReservationsForDiagnostics == 0 && connection.ActiveCalls == 0 &&
                client.PendingCallCount == 0, ct);
            Ensure(decoder.Started == 1 && decoder.Finished == 1 && recorder.Visits(method.MethodId) == 0 &&
                   NStudyCount(r1, "Disposed") == 1,
                "old valid decode completes and drains its own resources but never enters either service");
            Record("OldDecodeExitsButRequestCannotEnterEitherGeneration");

            Ensure(await InvokeValueTaskAsync<int>(receiver.Service, contract, "UsePayloadAsync", CreatePayload(), ct) == expectedPayload &&
                   decoder.Started == 2 && decoder.Finished == 2 && recorder.Visits(method.MethodId) == 1 &&
                   recorder.Methods.TryGetValue(method.MethodId, out var observed) && observed == method &&
                   session.CaptureNStudyBinding(original.Contract) == next.Value.BindingId &&
                   session.CaptureNStudyBinding(sibling.Contract) == sibling.BindingId && await b.TouchAsync(3, ct) == 103 &&
                   ReferenceEquals(session, NStudyClientSession(client)), "a distinct generated compressed call uses the exact R2 service and original B remains live");
            await WaitForNStudyAsync(() => connection.NStudyBindings.Snapshot() == (2, 0, 0) &&
                server.ActiveDecodeCountForDiagnostics == 0 && server.RetainedCompressedBytesForDiagnostics == 0 &&
                server.DecodedBytesInFlightForDiagnostics == 0 && server.DecodeQueueDepthForDiagnostics == 0 &&
                server.DecodeQueueReservationsForDiagnostics == 0 && connection.ActiveCalls == 0 &&
                client.PendingCallCount == 0, ct);
            Record("FreshCompressedCallUsesR2AndReturnsAllBudgets");
        }
        finally { decoder.Release(); }
    }

    private sealed class NStudyHeldServerDecoder : ISharpLinkCompressionProvider
    {
        private readonly TestCompressionProvider _inner = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _finished;
        public string WireProfile => _inner.WireProfile;
        internal TaskCompletionSource<int> FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Started => Volatile.Read(ref _started);
        internal int Finished => Volatile.Read(ref _finished);
        internal void Release() => _release.TrySetResult();

        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => _inner.TryCompress(input, output, maxOutputBytes, cancellationToken);

        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _started) == 1)
            {
                FirstEntered.TrySetResult(maxOutputBytes);
                _release.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            }
            _inner.Decompress(input, output, maxOutputBytes, cancellationToken);
            Interlocked.Increment(ref _finished);
        }
    }
}
