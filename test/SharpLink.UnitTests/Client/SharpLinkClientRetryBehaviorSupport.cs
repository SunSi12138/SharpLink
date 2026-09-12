using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientRetrySharedSupport;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientRetryBehaviorSupport
{
    internal static SharpLinkClient CreateRetryClient(
        TestClientTransportFactory transport,
        ISharpLinkRetryPolicy? policy,
        int maxAttempts,
        TimeSpan? initialBackoff = null,
        TimeSpan? requestTimeout = null)
    {
        var options = RetryOptions(maxAttempts, initialBackoff ?? TimeSpan.Zero);
        return ClientBuilderTestHelper.Build(transport, builder =>
        {
            ConfigureRetry(builder, options);
            if (requestTimeout is { } timeout)
                builder.UseRequestTimeout(timeout);
            if (policy is not null)
                builder.UseRetry(policy);
        });
    }

    internal static async Task WaitForReadyConnectionCountAsync(SharpLinkClient client, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.ReadyConnectionCount < expected && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Ensure(client.ReadyConnectionCount >= expected, $"expected {expected} ready connections");
    }

    internal sealed class RecordingRetryPolicy : ISharpLinkRetryPolicy
    {
        public int Count { get; private set; }
        public SharpLinkRetryContext LastContext { get; private set; }

        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
        {
            Count++;
            LastContext = context;
            return new SharpLinkRetryDecision(true, TimeSpan.Zero);
        }
    }

    internal sealed class DelayingRetryPolicy(TimeSpan delay) : ISharpLinkRetryPolicy
    {
        private readonly TaskCompletionSource _evaluationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EvaluationStarted => _evaluationStarted.Task;

        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
        {
            _evaluationStarted.TrySetResult();
            return new SharpLinkRetryDecision(true, delay);
        }
    }

    internal sealed class NegativeDelayPolicy : ISharpLinkRetryPolicy
    {
        public int Count { get; private set; }

        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
        {
            Count++;
            return new SharpLinkRetryDecision(true, TimeSpan.FromMilliseconds(-1));
        }
    }

    internal sealed class CountingInterceptor : ISharpLinkClientInterceptor
    {
        public int Count { get; private set; }

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Count++;
            return await next(context);
        }
    }

    internal sealed class HugeDelayPolicy : ISharpLinkRetryPolicy
    {
        private readonly TaskCompletionSource _evaluationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EvaluationStarted => _evaluationStarted.Task;
        public int Count { get; private set; }

        public SharpLinkRetryDecision Evaluate(in SharpLinkRetryContext context)
        {
            Count++;
            _evaluationStarted.TrySetResult();
            return new SharpLinkRetryDecision(true, TimeSpan.MaxValue);
        }
    }

    internal sealed class FirstAvailableSelector : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context)
            => (context.ExcludedMask & 1UL) == 0 ? 0 : 1;
    }

    internal sealed class FirstUnexcludedSelector : ISharpLinkEndpointSelector
    {
        public int Select(in SharpLinkEndpointSelectionContext context)
        {
            for (var index = 0; index < context.Count; index++)
            {
                if ((context.ExcludedMask & (1UL << index)) == 0)
                    return index;
            }
            return -1;
        }
    }

    internal sealed class RejectFirstEndpointPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        public int AcquireCount { get; private set; }
        public int ReportCount { get; private set; }
        public SharpLinkEndpointOutcome LastOutcome { get; private set; }

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            AcquireCount++;
            return endpoint.Endpoint.Id == "first"
                ? new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: null)
                : new SharpLinkEndpointAdmissionDecision(true, Token: 7, RetryAfter: null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            ReportCount++;
            LastOutcome = outcome;
            Ensure(token == 7, "admission token preserved");
        }
    }

    internal sealed class RejectOnceWithRetryAfterPolicy(TimeSpan retryAfter) : ISharpLinkEndpointAdmissionPolicy
    {
        public int AcquireCount { get; private set; }
        public int ReportCount { get; private set; }

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            AcquireCount++;
            return AcquireCount == 1
                ? new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: retryAfter)
                : new SharpLinkEndpointAdmissionDecision(true, Token: 1, RetryAfter: null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            ReportCount++;
            Ensure(token == 1, "admitted retry token");
        }
    }

    internal sealed class RejectFirstEndpointWithDelayPolicy(TimeSpan retryAfter) : ISharpLinkEndpointAdmissionPolicy
    {
        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
            => endpoint.Endpoint.Id == "first"
                ? new SharpLinkEndpointAdmissionDecision(false, Token: 0, RetryAfter: retryAfter)
                : new SharpLinkEndpointAdmissionDecision(true, Token: 1, RetryAfter: null);

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
        }
    }
}
