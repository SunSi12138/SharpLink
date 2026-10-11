using System.Buffers.Binary;
using System.Collections.Frozen;
using System.IO.Pipelines;
using System.Reflection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    public Task TwoWayAdmissionResumeShouldUseOriginalRegistrationAfterRemoval()
        => VerifyAdmissionSnapshotAfterRemovalAsync(oneWay: false);

    [Test]
    public Task OneWayAdmissionResumeShouldUseOriginalRegistrationAfterRemoval()
        => VerifyAdmissionSnapshotAfterRemovalAsync(oneWay: true);

    private static async Task VerifyAdmissionSnapshotAfterRemovalAsync(bool oneWay)
    {
        var stub = new CountingMethodFactStub(oneWay ? RpcMethodKind.OneWay : RpcMethodKind.Unary);
        var output = new Pipe();
        await using var harness = new ServerDispatchHarness(stub, output.Writer, maxSendQueueBytes: 4096);
        var registration = ServiceRegistration.CreateSingleton(typeof(object), stub, new object(), ownsService: false);
        var resolved = new ResolvedMethodCall(registration, 1, stub.ResolveMethodShape(1));
        var registry = (ServerServiceModuleRegistry)typeof(SharpLinkServer).GetField(
            "_serviceModuleRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Server)!;
        registry.PublishServices(new Dictionary<long, ServiceRegistration>().ToFrozenDictionary());

        var request = new byte[sizeof(long) * 2];
        BinaryPrimitives.WriteInt64LittleEndian(request, stub.InterfaceHash);
        BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(sizeof(long)), 1);
        var method = typeof(SharpLinkServer).GetMethod(
            oneWay ? "DispatchOneWayRpc" : "DispatchRpcAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = oneWay
            ? [harness.Connection, 83L, ProtocolV2FrameFlags.OneWay, new ReadOnlySequence<byte>(request),
                harness.Connection.CallCancellations, CancellationToken.None, null, null, true, 0, null, resolved]
            : [harness.Connection, 83L, ProtocolV2FrameFlags.None, new ReadOnlySequence<byte>(request),
                harness.Connection.CallCancellations, CancellationToken.None, null, null, true, null, resolved];
        await ((ValueTask)method.Invoke(harness.Server, arguments)!).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(stub.ResolveCount == 1, "admission resume must reuse the original method resolution");
        Ensure(harness.Session.IsConnected, "registry removal must not retarget an admitted registration snapshot");
        if (!oneWay)
        {
            await harness.Session.FlushSendQueueAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var read = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var remaining = read.Buffer;
            Ensure(ProtocolV2FrameParser.TryReadFrame(ref remaining, harness.Session.RuntimeContext.Protocol,
                out var header, out _), "admitted call must produce its response");
            Ensure((header.Flags & ProtocolV2FrameFlags.Error) == 0,
                "admitted original registration must not become an unknown service");
            output.Reader.AdvanceTo(read.Buffer.End);
        }
        await output.Reader.CompleteAsync();
    }
}
