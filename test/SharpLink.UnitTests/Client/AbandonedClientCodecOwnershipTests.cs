using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public class AbandonedClientCodecOwnershipTests
{
    [Test]
    [Arguments(RpcMethodKind.Unary, false)]
    [Arguments(RpcMethodKind.OneWay, false)]
    [Arguments(RpcMethodKind.ClientStreaming, false)]
    [Arguments(RpcMethodKind.Unary, true)]
    [Arguments(RpcMethodKind.OneWay, true)]
    [Arguments(RpcMethodKind.ClientStreaming, true)]
    public async Task LateInterceptorCannotReleaseModuleWhileCodecExecutes(RpcMethodKind kind, bool transformFailure)
    {
        var interceptor = new DelayedNextInterceptor(transformFailure);
        var codec = new BlockingCodec();
        await using var fixture = new AbandonedClientOwnershipTests.Fixture(interceptor);
        await fixture.Client.ConnectAsync();
        try
        {
            var invocation = AbandonedClientOwnershipTests.Invoke(fixture.Channel, kind, requestCodec: codec);
            await interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            interceptor.StartNext.TrySetResult();
            await codec.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            Exception? failure = null;
            try { await invocation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (SharpLinkException exception) { failure = exception; }
            Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded }, "public deadline");
            fixture.Module.TryBeginDraining();
            Ensure(!fixture.Module.WaitForDrainAsync().IsCompleted, "blocked request codec still owns module");
            Ensure(fixture.Module.RemainingCalls > 0, "module physical lease cannot follow logical pending count");
            codec.Release.Set();
            await fixture.Module.WaitForDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await interceptor.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(codec.Completed, "codec completes before module release");
            Ensure(interceptor.CaughtTerminal, "late next observes selected deadline before interceptor transforms it");
        }
        finally
        {
            interceptor.StartNext.TrySetResult();
            codec.Release.Set();
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class DelayedNextInterceptor(bool transformFailure) : ISharpLinkClientInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StartNext { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CaughtTerminal;
        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
        {
            Entered.TrySetResult();
            await StartNext.Task.ConfigureAwait(false);
            try
            {
                return await next(context).ConfigureAwait(false);
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.DeadlineExceeded)
            {
                CaughtTerminal = true;
                if (transformFailure)
                    throw new InvalidOperationException("interceptor transformed late deadline", exception);
                return context.Method.Kind == RpcMethodKind.OneWay ? default : new SharpLinkClientInvocationResult(42);
            }
            finally { Finished.TrySetResult(); }
        }
    }

    private sealed class BlockingCodec : IRpcCodec<RpcEmptyRequest>
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);
        internal bool Completed;
        public void Serialize(in RpcEmptyRequest value, IBufferWriter<byte> buffer)
        {
            Entered.TrySetResult();
            Release.Wait();
            Completed = true;
        }
        public RpcEmptyRequest Deserialize(in ReadOnlySequence<byte> buffer) => default;
    }
}
