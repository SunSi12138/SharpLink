using System;
using SharpLink.Abstractions;
using SharpLink.Client;
namespace SharpLink.N1.StaticBindEvidence;
internal static partial class Program
{
    private static void SelectionComponents()
    {
        var candidates = new[] {
            new SharpLinkEndpointCandidate(new() { Id = "A", Address = new SharpLinkTcpAddress("127.0.0.1", 1) }, 2, 4, 1),
            new SharpLinkEndpointCandidate(new() { Id = "B", Address = new SharpLinkTcpAddress("127.0.0.1", 2) }, 1, 3, 1) };
        var roundRobin = -1; var leastPending = -1;
        Check(EndpointSelectionKernel.SelectBuiltInEndpoint(candidates, SharpLinkLoadBalancingStrategy.PowerOfTwoChoices, 0,
            ref roundRobin, ref leastPending) == 0, "shared-picker-normalized-load-preserved");
        Check(EndpointSelectionKernel.SelectBuiltInEndpoint(candidates, SharpLinkLoadBalancingStrategy.PowerOfTwoChoices, 1,
            ref roundRobin, ref leastPending) == 1, "shared-picker-exclusion-index-preserved");
        Check(EndpointSelectionKernel.SelectBuiltInEndpoint(candidates, SharpLinkLoadBalancingStrategy.LeastPending, 0,
            ref roundRobin, ref leastPending) == 1, "shared-picker-least-pending-preserved");
        Check(EndpointSelectionKernel.SelectBuiltInEndpoint(candidates, SharpLinkLoadBalancingStrategy.RoundRobin, 0,
            ref roundRobin, ref leastPending) == 0 && EndpointSelectionKernel.SelectBuiltInEndpoint(candidates,
            SharpLinkLoadBalancingStrategy.RoundRobin, 0, ref roundRobin, ref leastPending) == 1,
            "shared-picker-round-robin-cursor-preserved");
    }
}
