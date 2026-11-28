namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyApprovalDoesNotWaitForOrRetargetAlreadyEncodedOldPayload(string transport)
    {
        var probe = new NStudyOutboundProbe();
        var sender = new NStudyHeldCompression();
        var decoder = new NStudyCountingCompression();
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            (fixture, trace, ct) => VerifyNStudyCompressionFenceAsync(fixture, trace, probe, sender, decoder, ct),
            factory => new NStudyFenceTransportFactory(factory, probe),
            configureClient: builder => builder
                .UseRuntime(options => options.Compression.Providers.Add(sender))
                .UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
                {
                    MinimumPayloadBytes = 256,
                    MinimumSavingsBytes = 8,
                    MinimumSavingsRatio = 0.05
                }),
            configureServer: builder => builder.UseRuntime(options => options.Compression.Providers.Add(decoder)));
    }

    private static async Task VerifyNStudyCompressionFenceAsync(NStudyCoordinationFixture fixture,
        NStudyTrace trace, NStudyOutboundProbe probe, NStudyHeldCompression sender,
        NStudyCountingCompression decoder, CancellationToken ct)
    {
        var (server, contracts, oldAssembly, newAssembly, _, _, first, _, _) = fixture;
        var (client, session, peer, a1, b1, receiver, b) = first;
        var a = receiver.Service;
        var priorBind = session.NStudyBindOperationForDiagnostics;
        var payloadType = contracts.GetContractType("SharpLink.DynamicPlugin.DynamicPayload");
        object Payload()
        {
            var value = Activator.CreateInstance(payloadType) ?? throw new InvalidOperationException("Expected real payload.");
            payloadType.GetProperty("Value")!.SetValue(value, 7);
            payloadType.GetProperty("Label")!.SetValue(value, new string('x', 4096));
            payloadType.GetProperty("Values")!.SetValue(value, new List<int> { 2, 3 });
            payloadType.GetProperty("Parent")!.SetValue(value, value);
            return value;
        }

        var blockedPayload = Payload();
        probe.StartRecording();
        var withheld = Task.Run(async () =>
            await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UsePayloadAsync", blockedPayload, ct), ct);
        try
        {
            Ensure(await sender.Entered.Task.WaitAsync(ct) >= 4096 &&
                   !withheld.IsCompleted && decoder.Decoded == 0 && await b.TouchAsync(1, ct) == 101,
                "the genuine typed old request was encoded and is awaiting the negotiated compressor, not a published head");
            trace.Record("ActualEncodedOldRequestWaitsBeforeOutboundPublication", a1, b1);

            static ValueTask<bool> Approve(NStudyChange _, CancellationToken __) => ValueTask.FromResult(true);
            var clientAttempt = client.NegotiateNStudyReplacementAsync(NStudyInitiator.Server, Approve, ct);
            var replacing = server.StageNStudyReplacementAsync(peer, oldAssembly, newAssembly, a1.Contract,
                NStudyInitiator.Server, Approve, TimeSpan.FromSeconds(15), ct);
            var activated = await clientAttempt.WaitAsync(ct) ?? throw new InvalidOperationException("Expected exact activation.");
            EnsureReplacementReleased((await replacing.WaitAsync(ct)).Replacement, "server can retire while unadmitted local payload waits");
            Ensure(probe.Flushed.Any(frame => frame.Control is { Phase: NStudyPhase.Ready } control &&
                           control.Change == activated.Change) &&
                   activated.Binding.Target == server.ReadNStudyTarget(newAssembly, a1.Contract) &&
                   !withheld.IsCompleted && decoder.Decoded == 0 && session.NStudyBindOperationForDiagnostics == priorBind + 1 &&
                   !probe.Flushed.Any(IsCompressedRequest) && await b.TouchAsync(2, ct) == 102,
                "Ready and activation do not include or wait on the held pre-admission encoded request");
            trace.Record("ExactReadyAndR2WhileOldCompressorStillWaits", a1, activated.Binding);

            sender.Release();
            try
            {
                _ = await withheld.WaitAsync(ct);
                throw new InvalidOperationException("Expected stale encoded head to be refused once.");
            }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable) { }
            Ensure(sender.Compressed == 1 && decoder.Decoded == 0 && !probe.Flushed.Any(IsCompressedRequest),
                "the completed real old compressor cannot put that head on the physical writer");
            trace.Record("ReleasedOldCompressedHeadNeverUsesTheWriter", a1, activated.Binding);

            var newValue = await InvokeValueTaskAsync<int>(a, contracts.ContractType, "UsePayloadAsync", Payload(), ct);
            await WaitForNStudyAsync(() => probe.Flushed.Any(IsCompressedRequest), ct);
            Ensure(newValue == 4108 &&
                   sender.Compressed == 2 && decoder.Decoded == 1 && probe.Flushed.Count(IsCompressedRequest) == 1 &&
                   session.CaptureNStudyBinding(b1.Contract) == b1.BindingId && await b.TouchAsync(3, ct) == 103 &&
                   ReferenceEquals(session, NStudyClientSession(client)),
                "the same provider, real output and typed server decoder handle the new exact generation only");
            trace.Record("NewCompressedRequestUsesActualR2AndSameSession", a1, activated.Binding);
        }
        finally
        {
            sender.Release();
        }
    }

    private static bool IsCompressedRequest(NStudyWriterFrame frame)
        => frame.Header.Type == ProtocolV2FrameType.Request && frame.Header.Flags.HasFlag(ProtocolV2FrameFlags.Compressed);

    private sealed class NStudyHeldCompression : ISharpLinkCompressionProvider
    {
        private readonly TestCompressionProvider _inner = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<long> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _compressed;
        public string WireProfile => _inner.WireProfile;
        internal int Compressed => Volatile.Read(ref _compressed);
        internal void Release() => _release.TrySetResult();

        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            if (Entered.TrySetResult(input.Length))
                _release.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            var compressed = _inner.TryCompress(input, output, maxOutputBytes, cancellationToken);
            if (compressed)
                Interlocked.Increment(ref _compressed);
            return compressed;
        }

        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => _inner.Decompress(input, output, maxOutputBytes, cancellationToken);
    }
}
