using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class ColdSessionFenceResearchTests
{
    [Test]
    [NotInParallel]
    public async Task UntrackedOneWaySerializationMustDrainBeforeSendingTheDelimiter()
    {
        ColdFenceResearchService.Reset();
        await using var harness = await Harness.CreateAsync(admission: false);
        var proxy = harness.Client.Get<IColdFenceResearchService>();
        var descriptor = proxy.GetType().GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(RpcMethodDescriptor))
            .Select(field => (RpcMethodDescriptor)field.GetValue(null)!)
            .Single(method => method.Kind == RpcMethodKind.OneWay);
        using var codec = new GatedEmptyRequestCodec();
        var call = Task.Run(async () =>
        {
            var request = default(RpcEmptyRequest);
            var streams = default(RpcNoClientStreams);
            await ((IRpcChannel)harness.Client).InvokeOneWayAsync(
                descriptor, in request, codec, in streams, null, default);
        });
        try
        {
            await codec.Entered.WaitAsync(PhaseTimeout);
            await using var pause = await ResearchPause.AcquireAsync(harness.Client);
            Ensure(harness.Connection.CallAdmissionReservationCount == 0 &&
                   harness.Connection.PendingCalls.ActiveCount == 0 &&
                   harness.Connection.ActiveCallCount == 1,
                "serialization owns untracked-call accounting after reservation transfer without any pending entry");
            var clientDrain = WaitForClientDrainAsync(harness.Connection);
            Ensure(!clientDrain.IsCompleted, "reservation/pending-only drain would miss the old untracked serialization");
            codec.Release();
            await call.WaitAsync(PhaseTimeout);
            await clientDrain;
            await FenceAsync(harness.Connection);
            await ColdFenceResearchService.Entered.WaitAsync(PhaseTimeout);
            Ensure(harness.Server.ActiveCallCountForDiagnostics == 1,
                "the pre-cut untracked call must be received before the parsing delimiter");
            ColdFenceResearchService.ReleaseWork();
            await WaitForServerDrainAsync(harness.Server);
            await pause.DisposeAsync();
            await AssertSameSessionUsableAsync(harness);
        }
        finally
        {
            codec.Release();
            ColdFenceResearchService.ReleaseAll();
            await call.WaitAsync(PhaseTimeout);
        }
    }

    private sealed class GatedEmptyRequestCodec : IRpcCodec<RpcEmptyRequest>, IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        internal Task Entered => _entered.Task;
        internal void Release() => _release.Set();
        public void Serialize(in RpcEmptyRequest value, IBufferWriter<byte> buffer)
        {
            _entered.TrySetResult();
            Ensure(_release.Wait(PhaseTimeout), "untracked request serialization release");
            RpcEmptyRequestCodec.Instance.Serialize(in value, buffer);
        }
        public RpcEmptyRequest Deserialize(in ReadOnlySequence<byte> buffer)
            => RpcEmptyRequestCodec.Instance.Deserialize(in buffer);
        public void Dispose() => _release.Dispose();
    }
}
