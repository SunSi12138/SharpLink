using System.Collections.Generic;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientLogicalDeadlineTests
{
    [Test]
    public async Task LateInterceptorContinuationShouldNotEnterLaterUserCodeAfterDeadline()
    {
        var timeProvider = new ManualTimeProvider();
        var delayed = new DelayedContinuationInterceptor();
        var later = new CountingInterceptor();
        await using var client = ClientBuilderTestHelper.Build(
            new TestClientTransportFactory(),
            builder =>
            {
                builder.UseTimeProvider(timeProvider);
                builder.AddInterceptor(delayed);
                builder.AddInterceptor(later);
            });

        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 291,
            Kind: RpcMethodKind.Unary,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var invocation = channel.InvokeUnaryAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            RpcEmptyRequestCodec.Instance,
            metadata: null,
            cancellationToken: default).AsTask();

        await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var spin = 0; spin < 100 && timeProvider.ActiveTimerCount == 0; spin++)
            await Task.Yield();
        Ensure(timeProvider.ActiveTimerCount != 0, "the frozen interceptor deadline must be armed");

        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var callerFailure = await CaptureSharpLinkExceptionAsync(invocation).WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(callerFailure.Code == SharpLinkErrorCode.DeadlineExceeded,
            "the caller must observe the frozen deadline");

        delayed.Release();
        var continuationFailure = await delayed.ContinuationFailure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(continuationFailure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "the retained continuation must observe the same logical deadline terminal");
        Ensure(later.InvocationCount == 0,
            "a retained continuation must not enter later interceptor user code after the deadline won");
    }

    [Test]
    public async Task ExpiredShortCircuitStreamShouldNotInvokeNextMoveNext()
    {
        var timeProvider = new ManualTimeProvider();
        var stream = new CountingStream();
        await using var client = ClientBuilderTestHelper.Build(
            new TestClientTransportFactory(),
            builder =>
            {
                builder.UseTimeProvider(timeProvider);
                builder.AddInterceptor(new ShortCircuitStreamInterceptor(stream));
            });

        var method = new RpcMethodDescriptor(
            ContractId: 1,
            MethodId: 292,
            Kind: RpcMethodKind.ServerStreaming,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: true,
            MethodTimeout: TimeSpan.FromSeconds(5));
        var channel = (IRpcChannel)client;
        var request = default(RpcEmptyRequest);
        var enumerator = channel.InvokeServerStreamingAsync(
            method,
            in request,
            RpcEmptyRequestCodec.Instance,
            RpcEmptyRequestCodec.Instance,
            metadata: null,
            cancellationToken: default).GetAsyncEnumerator();
        try
        {
            Ensure(await enumerator.MoveNextAsync(), "short-circuit stream first item");
            Ensure(stream.MoveNextCount == 1, "the first MoveNext must execute exactly once");

            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var failure = await CaptureSharpLinkExceptionAsync(
                enumerator.MoveNextAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(2));
            Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "the next stream move must observe the logical deadline");
            Ensure(stream.MoveNextCount == 1,
                "expired short-circuit enumeration must reject before invoking user MoveNextAsync again");
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private static async Task<SharpLinkException> CaptureSharpLinkExceptionAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }

        throw new Exception("expected SharpLinkException");
    }

    private sealed class DelayedContinuationInterceptor : ISharpLinkClientInterceptor
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Exception> ContinuationFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.TrySetResult();

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            try
            {
                return await next(context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ContinuationFailure.TrySetResult(exception);
                throw;
            }
        }
    }

    private sealed class CountingInterceptor : ISharpLinkClientInterceptor
    {
        internal int InvocationCount;

        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Interlocked.Increment(ref InvocationCount);
            return ValueTask.FromResult(new SharpLinkClientInvocationResult(default(RpcEmptyRequest)));
        }
    }

    private sealed class ShortCircuitStreamInterceptor(CountingStream stream) : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
            => ValueTask.FromResult(new SharpLinkClientInvocationResult(stream));
    }

    private sealed class CountingStream : IAsyncEnumerable<RpcEmptyRequest>, IAsyncEnumerator<RpcEmptyRequest>
    {
        internal int MoveNextCount;

        public RpcEmptyRequest Current => default;

        public IAsyncEnumerator<RpcEmptyRequest> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
            => this;

        public ValueTask<bool> MoveNextAsync()
            => ValueTask.FromResult(Interlocked.Increment(ref MoveNextCount) == 1);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
