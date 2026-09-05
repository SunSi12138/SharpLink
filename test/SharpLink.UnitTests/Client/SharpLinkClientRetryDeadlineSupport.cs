using SharpLink.Client;
using SharpLink.Sdk;
using static SharpLink.UnitTests.Client.SharpLinkClientRetrySharedSupport;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientRetryDeadlineSupport
{
    internal sealed class CountingAdmissionPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        public int AcquireCount { get; private set; }
        public int ReportCount { get; private set; }

        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint,
            in RpcMethodDescriptor method)
        {
            _ = endpoint;
            _ = method;
            AcquireCount++;
            return new SharpLinkEndpointAdmissionDecision(true, Token: AcquireCount, RetryAfter: null);
        }

        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            _ = outcome;
            Ensure(token == AcquireCount,
                "the admitted attempt must report its exact acquisition token");
            ReportCount++;
        }
    }
}
