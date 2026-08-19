using System.Buffers;
using System.IO.Pipelines;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class PreCreditBudgetConfigurationTests
{
    private const int DefaultBudgetBytes = 4 * 1024 * 1024;

    [Test]
    public void DefaultBudgetShouldBeIndependentFromWireWindowAndPerformanceProfile()
    {
        using var defaults = new SharpLinkRuntimeContextBuilder()
            .Build(includeGeneratedAssemblyCatalog: false);
        using var smallWireWindow = new SharpLinkRuntimeContextBuilder()
            .Configure(options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 256 * 1024;
                options.FlowControl.ConnectionReceiveWindowBytes = 1024 * 1024;
            })
            .Build(includeGeneratedAssemblyCatalog: false);
        using var largeWireWindow = new SharpLinkRuntimeContextBuilder()
            .Configure(options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = 4 * 1024 * 1024;
                options.FlowControl.ConnectionReceiveWindowBytes = 64 * 1024 * 1024;
            })
            .Build(includeGeneratedAssemblyCatalog: false);
        using var lowLatency = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.PerformanceProfile = SharpLinkPerformanceProfile.LowLatency)
            .Build(includeGeneratedAssemblyCatalog: false);
        using var throughput = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.PerformanceProfile = SharpLinkPerformanceProfile.Throughput)
            .Build(includeGeneratedAssemblyCatalog: false);

        Ensure(defaults.Options.FlowControl.MaxPreCreditSerializedBytes == DefaultBudgetBytes,
            "the balanced default should be the documented 4 MiB local policy");
        Ensure(smallWireWindow.Options.FlowControl.MaxPreCreditSerializedBytes == DefaultBudgetBytes,
            "shrinking the wire receive window must not shrink the local pre-credit default");
        Ensure(largeWireWindow.Options.FlowControl.MaxPreCreditSerializedBytes == DefaultBudgetBytes,
            "growing the wire receive window must not grow the local pre-credit default");
        Ensure(lowLatency.Options.FlowControl.MaxPreCreditSerializedBytes == DefaultBudgetBytes,
            "the local default should not be implicitly rewritten by LowLatency");
        Ensure(throughput.Options.FlowControl.MaxPreCreditSerializedBytes == DefaultBudgetBytes,
            "the local default should not be implicitly rewritten by Throughput");
    }

    [Test]
    public async Task SessionBudgetShouldRemainDefaultWhenNegotiatedWireWindowChanges()
    {
        var first = await CreateStarvedSessionAsync(
            "pre-credit-default-small-wire",
            budgetBytes: null,
            wireWindowBytes: 1024 * 1024);
        await using var firstSession = first.Session;
        using var firstContext = first.Context;
        var firstSend = firstSession.SendStreamChunkAsync(1, 1, new Payload(1024)).AsTask();
        Ensure(!firstSend.IsCompleted, "the first default-budget send should wait for exhausted wire credit");
        Ensure(firstSession.PreCreditSerializedByteLimit == DefaultBudgetBytes,
            "the default local budget must not derive from a 1 MiB negotiated window");
        Ensure(firstSession.NegotiatedOptions?.ConnectionReceiveWindowBytes == 1024 * 1024,
            "the negotiated wire window should remain 1 MiB");
        await TerminateAsync(firstSession, firstSend, "small wire cleanup");

        var second = await CreateStarvedSessionAsync(
            "pre-credit-default-large-wire",
            budgetBytes: null,
            wireWindowBytes: 4 * 1024 * 1024);
        await using var secondSession = second.Session;
        using var secondContext = second.Context;
        var secondSend = secondSession.SendStreamChunkAsync(2, 1, new Payload(1024)).AsTask();
        Ensure(!secondSend.IsCompleted, "the second default-budget send should wait for exhausted wire credit");
        Ensure(secondSession.PreCreditSerializedByteLimit == DefaultBudgetBytes,
            "the default local budget must not derive from a 4 MiB negotiated window");
        Ensure(secondSession.NegotiatedOptions?.ConnectionReceiveWindowBytes == 4 * 1024 * 1024,
            "the negotiated wire window should remain 4 MiB");
        await TerminateAsync(secondSession, secondSend, "large wire cleanup");
    }

    [Test]
    public async Task ConfiguredBudgetSmallerThanWireWindowShouldTightenOnlyLocalAdmission()
    {
        const int budgetBytes = 1024 * 1024;
        const int wireWindowBytes = 4 * 1024 * 1024;
        var fixture = await CreateStarvedSessionAsync(
            "pre-credit-config-smaller",
            budgetBytes,
            wireWindowBytes);
        await using var session = fixture.Session;
        using var context = fixture.Context;

        var owner = session.SendStreamChunkAsync(10, 1, new Payload(budgetBytes)).AsTask();
        var waiter = session.SendStreamChunkAsync(11, 1, new Payload(budgetBytes)).AsTask();
        var rejected = session.SendStreamChunkAsync(12, 1, new Payload(budgetBytes)).AsTask();

        Ensure(!owner.IsCompleted && !waiter.IsCompleted,
            "one configured-budget owner and one bounded waiter should remain pending");
        Ensure(session.PreCreditSerializedByteLimit == budgetBytes,
            "the local byte limit should use the configured 1 MiB value");
        Ensure(session.PreCreditSerializedBytes == budgetBytes,
            "the configured budget should admit exactly one 1 MiB owner");
        Ensure(session.PreCreditSerializedWaiterCount == 1,
            "a budget below max-frame should derive one serialized waiter");
        Ensure(session.NegotiatedOptions?.ConnectionReceiveWindowBytes == wireWindowBytes,
            "the 4 MiB wire window must not be mutated by the smaller local budget");
        await ExpectResourceExhausted(rejected);

        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "smaller budget cleanup");
        session.NotifyDisconnected(terminal);
        await ExpectSameException(owner, terminal);
        await ExpectSameException(waiter, terminal);
        Ensure(session.PreCreditSerializedBytes == 0 && session.PreCreditSerializedWaiterCount == 0,
            "configured smaller-budget terminal cleanup must return accounting to zero");
    }

    [Test]
    public async Task ConfiguredBudgetLargerThanWireWindowShouldExpandOnlyLocalAdmission()
    {
        const int budgetBytes = 8 * 1024 * 1024;
        const int wireWindowBytes = 1024 * 1024;
        var fixture = await CreateStarvedSessionAsync(
            "pre-credit-config-larger",
            budgetBytes,
            wireWindowBytes);
        await using var session = fixture.Session;
        using var context = fixture.Context;

        var blocked = session.SendStreamChunkAsync(20, 1, new Payload(1024)).AsTask();
        Ensure(!blocked.IsCompleted, "the send should wait after the independent wire credit is exhausted");
        Ensure(session.PreCreditSerializedByteLimit == budgetBytes,
            "the local byte limit should use the configured 8 MiB value");
        Ensure(session.NegotiatedOptions?.ConnectionReceiveWindowBytes == wireWindowBytes,
            "the 1 MiB wire window must not be mutated by the larger local budget");
        Ensure(context.Options.FlowControl.ConnectionReceiveWindowBytes == wireWindowBytes,
            "configuring the local budget must not rewrite configured wire receive credit");

        await TerminateAsync(session, blocked, "larger budget cleanup");
    }

    [Test]
    public void ConfiguredBudgetShouldValidateAndFreezeWithRuntimeSnapshots()
    {
        var zeroFailure = CaptureFailure(new SharpLinkFlowControlOptions
        {
            MaxPreCreditSerializedBytes = 0
        }.Validate);
        var negativeFailure = CaptureFailure(new SharpLinkFlowControlOptions
        {
            MaxPreCreditSerializedBytes = -1
        }.Validate);
        Ensure(zeroFailure is ArgumentOutOfRangeException
        {
            ParamName: nameof(SharpLinkFlowControlOptions.MaxPreCreditSerializedBytes)
        }, "zero local pre-credit budget must fail its own public validation");
        Ensure(negativeFailure is ArgumentOutOfRangeException
        {
            ParamName: nameof(SharpLinkFlowControlOptions.MaxPreCreditSerializedBytes)
        }, "negative local pre-credit budget must fail its own public validation");

        var builder = new SharpLinkRuntimeContextBuilder()
            .Configure(options => options.FlowControl.MaxPreCreditSerializedBytes = 2 * 1024 * 1024);
        using var first = builder.Build(includeGeneratedAssemblyCatalog: false);
        builder.Configure(options => options.FlowControl.MaxPreCreditSerializedBytes = 6 * 1024 * 1024);
        using var second = builder.Build(includeGeneratedAssemblyCatalog: false);

        var leakedCopy = first.Options;
        leakedCopy.FlowControl.MaxPreCreditSerializedBytes = 16 * 1024 * 1024;

        Ensure(first.Options.FlowControl.MaxPreCreditSerializedBytes == 2 * 1024 * 1024,
            "the first built context must retain its frozen local budget snapshot");
        Ensure(second.Options.FlowControl.MaxPreCreditSerializedBytes == 6 * 1024 * 1024,
            "a later build may use a different local budget without mutating the first context");
    }

    private static async Task<(SharpLinkRuntimeContext Context, RpcSession Session)> CreateStarvedSessionAsync(
        string id,
        int? budgetBytes,
        int wireWindowBytes)
    {
        var codec = new PayloadCodec();
        var builder = new SharpLinkRuntimeContextBuilder()
            .Configure(options =>
            {
                options.FlowControl.StreamReceiveWindowBytes = wireWindowBytes;
                options.FlowControl.ConnectionReceiveWindowBytes = wireWindowBytes;
                if (budgetBytes.HasValue)
                    options.FlowControl.MaxPreCreditSerializedBytes = budgetBytes.Value;
            })
            .AddCodec(codec);
        var context = builder.Build(includeGeneratedAssemblyCatalog: false);
        var input = new Pipe();
        var output = new Pipe();
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            id,
            input.Reader,
            output.Writer,
            RpcSessionTestFixture.ClientOptions(context),
            completeHandshake: false);
        RpcSessionTestFixture.CompleteHandshake(
            session,
            ProtocolV2Capabilities.FlowControl,
            streamReceiveWindowBytes: wireWindowBytes,
            connectionReceiveWindowBytes: wireWindowBytes);

        // Consume all peer-advertised send credit without serializing a benchmark payload. The
        // next unsized item is therefore guaranteed to exercise local pre-credit admission.
        await session.AcquireStreamSendCreditAsync(
            requestId: 900_000,
            streamId: 1,
            encodedBytes: wireWindowBytes,
            CancellationToken.None);
        return (context, session);
    }

    private static async Task TerminateAsync(RpcSession session, Task blocked, string message)
    {
        var terminal = new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, message);
        session.NotifyDisconnected(terminal);
        await ExpectSameException(blocked, terminal);
        Ensure(session.PreCreditSerializedBytes == 0 && session.PreCreditSerializedWaiterCount == 0,
            "terminal cleanup must return configured pre-credit accounting to zero");
    }

    private static async Task ExpectResourceExhausted(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ResourceExhausted)
        {
            return;
        }
        throw new InvalidOperationException("Expected configured pre-credit admission to reject the excess sender.");
    }

    private static async Task ExpectSameException(Task task, Exception expected)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (ReferenceEquals(exception, expected))
        {
            return;
        }
        throw new InvalidOperationException("The blocked configured-budget send did not observe the expected terminal.");
    }

    private static Exception? CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string scenario)
    {
        if (!condition)
            throw new InvalidOperationException($"Pre-credit configuration assertion failed: {scenario}.");
    }

    private readonly record struct Payload(int Bytes);

    private sealed class PayloadCodec : IRpcCodec<Payload>
    {
        public void Serialize(in Payload value, IBufferWriter<byte> buffer)
        {
            var span = buffer.GetSpan(value.Bytes);
            span[..value.Bytes].Fill(0x2d);
            buffer.Advance(value.Bytes);
        }

        public Payload Deserialize(in ReadOnlySequence<byte> buffer)
            => new(checked((int)buffer.Length));
    }
}
