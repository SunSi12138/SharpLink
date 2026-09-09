using System.Linq;
using System.Text;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Runtime;

[NotInParallel]
public sealed class Stack13ClockTests
{
    [Test]
    public async Task UntimedCallShouldNotReadUnusedClock()
    {
        var clock = new CountingClock();
        await using var publicClient = CreateClient(clock);
        var client = (SharpLinkClient)publicClient;
        clock.Reads = 0;
        var call = client.ResolveCallControl(null, true, false, null);
        Ensure(!call.Deadline.HasValue && clock.Reads == 0, "untimed call has no clock dependency");
    }

    [Test]
    public async Task RuntimeTimeoutEnableAndDisableShouldKeepClockChecks()
    {
        var clock = new CountingClock();
        await using var publicClient = CreateClient(clock);
        var client = (SharpLinkClient)publicClient;
        client.UpdateRequestTimeout(TimeSpan.FromSeconds(1));
        clock.Reads = 0;
        var call = client.ResolveCallControl(null, true, false, null);
        Ensure(call.Deadline.HasValue && clock.Reads == 2, "timed call captures and checks its boundary");
        client.DisableRequestTimeout();
        clock.Reads = 0;
        call = client.ResolveCallControl(null, true, false, null);
        Ensure(!call.Deadline.HasValue && clock.Reads == 0, "disable only affects future calls");
    }

    [Test]
    public async Task UntimedChildMustStillInheritParentBoundary()
    {
        var clock = new CountingClock();
        await using var publicClient = CreateClient(clock);
        var client = (SharpLinkClient)publicClient;
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), clock);
        using var scope = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot("parent", null, deadline, clock));
        clock.Now += 2 * clock.TimestampFrequency;
        var call = client.ResolveCallControl(null, true, false, null);
        Ensure(call.Deadline.HasValue && call.Deadline.Timestamp == deadline.Timestamp,
            "shared parent boundary must not be re-anchored or omitted");
        clock.Now += 3 * clock.TimestampFrequency;
        try
        {
            _ = client.ResolveCallControl(null, true, false, null);
            throw new Exception("expired parent was accepted");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.DeadlineExceeded, "expired parent error");
        }
    }

    [Test]
    public async Task UntimedChildMustProjectDifferentParentClock()
    {
        var parentClock = new CountingClock();
        var childClock = new CountingClock();
        await using var publicClient = CreateClient(childClock);
        var client = (SharpLinkClient)publicClient;
        var deadline = RpcDeadline.Create(TimeSpan.FromSeconds(5), parentClock);
        using var scope = SharpLinkCallContext.Push(new SharpLinkCallContextSnapshot("parent", null, deadline, parentClock));
        parentClock.Now += 2 * parentClock.TimestampFrequency;
        var call = client.ResolveCallControl(null, true, false, null);
        Ensure(call.Deadline.HasValue && call.Deadline.GetRemaining(childClock) == TimeSpan.FromSeconds(3),
            "cross-clock parent remaining budget must survive untimed local policy");
    }

    private static ISharpLinkClient CreateClient(TimeProvider clock)
        => SharpClientBuilder.Create().UseTransport(new UnusedTransport())
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTimeProvider(clock).DisableRequestTimeout().Build();

    private static void Ensure(bool value, string message)
    {
        if (!value)
            throw new Exception(message);
    }

    private sealed class CountingClock : TimeProvider
    {
        internal long Now = 1000000;
        internal int Reads;
        public override long TimestampFrequency => 1000000000;
        public override long GetTimestamp()
        {
            Reads++;
            return Now;
        }
    }

    private sealed class UnusedTransport : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new InvalidOperationException("transport must not start"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingWriter : IBufferWriter<byte>
    {
        private readonly ArrayBufferWriter<byte> _writer = new(4096);
        internal int SpanCalls;
        internal ReadOnlyMemory<byte> Written => _writer.WrittenMemory;
        public void Advance(int count) => _writer.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _writer.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0)
        {
            SpanCalls++;
            return _writer.GetSpan(sizeHint);
        }
    }
}
