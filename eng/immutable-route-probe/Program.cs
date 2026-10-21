using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpLink.Abstractions;

namespace ImmutableRouteProbe;

// This executable is a feasibility gate, NOT a transport implementation or RPC benchmark.
// It uses the actual production descriptor and public endpoint-policy interface. The route
// state model is deliberately separate and must not be mistaken for tested production code.
internal static class Program
{
    private const long ContractId = 101;
    private const long MethodId = 202;
    private const long OldRouteId = 303;
    private const long NewRouteId = 404;

    private static int Main()
    {
        var checks = new List<object>();
        try
        {
            var logical = new RpcMethodDescriptor(
                ContractId, MethodId, RpcMethodKind.Unary, true, false, false, null);
            var substituted = logical with { ContractId = OldRouteId };
            var policy = new LogicalContractPolicy(ContractId);
            var endpoint = default(SharpLinkEndpointCandidate);
            Require(policy.TryAcquire(endpoint, logical).IsAllowed, "Baseline logical identity rejected.");
            Require(!policy.TryAcquire(endpoint, substituted).IsAllowed,
                "The counterexample policy unexpectedly accepted a wire route as logical identity.");

            Span<byte> baselinePrefix = stackalloc byte[16];
            Span<byte> substitutedPrefix = stackalloc byte[16];
            WritePrefix(logical, baselinePrefix);
            WritePrefix(substituted, substitutedPrefix);
            Require(BinaryPrimitives.ReadInt64LittleEndian(substitutedPrefix) == OldRouteId,
                "Substitution did not put the route on the wire.");
            Require(BinaryPrimitives.ReadInt64LittleEndian(baselinePrefix) == ContractId,
                "Baseline prefix changed.");
            Require(substituted.MethodId == logical.MethodId, "Method identity changed.");
            checks.Add(new
            {
                name = "same_shape_substitution_counterexample",
                testPassed = true,
                candidatePreservesClientPolicy = false,
                logicalContractId = ContractId,
                observedPolicyContractId = substituted.ContractId,
                wireRouteId = BinaryPrimitives.ReadInt64LittleEndian(substitutedPrefix),
                requestPrefixBytes = substitutedPrefix.Length
            });

            var oneWay = logical with { Kind = RpcMethodKind.OneWay, HasResponsePayload = false };
            var substitutedOneWay = oneWay with { ContractId = OldRouteId };
            Require(policy.TryAcquire(endpoint, oneWay).IsAllowed, "Baseline OneWay identity rejected.");
            Require(!policy.TryAcquire(endpoint, substitutedOneWay).IsAllowed,
                "OneWay substitution unexpectedly preserved the logical policy.");
            WritePrefix(substitutedOneWay, substitutedPrefix);
            Require(BinaryPrimitives.ReadInt64LittleEndian(substitutedPrefix) == OldRouteId,
                "OneWay substitution did not put the route on the wire.");
            checks.Add(new { name = "oneway_substitution_counterexample", testPassed = true,
                candidatePreservesClientPolicy = false });

            // The simple independent-ID alternative preserves both meanings, but is a DIFFERENT
            // ABI hypothesis. This does not assert that all possible representations need 40 bytes.
            var expanded = new SeparateIdentityDescriptor(logical, OldRouteId);
            Require(policy.TryAcquire(endpoint, expanded.LogicalMethod).IsAllowed,
                "Separate identity did not preserve logical policy.");
            WriteExpandedPrefix(expanded, substitutedPrefix);
            Require(BinaryPrimitives.ReadInt64LittleEndian(substitutedPrefix) == OldRouteId,
                "Separate identity did not preserve wire routing.");
            checks.Add(new
            {
                name = "separate_identity_layout",
                testPassed = true,
                baselineBytes = Unsafe.SizeOf<RpcMethodDescriptor>(),
                candidateBytes = Unsafe.SizeOf<SeparateIdentityDescriptor>(),
                performanceConclusion = "not_measured"
            });

            RouteModelChecks.Run(checks, ContractId, OldRouteId, NewRouteId);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                referenceBaseline = "13928a35df4bff3cd99b14bd1df131aa3eb3bcdb",
                sourceValidation = "Run run.py to validate the production sources against the reference baseline.",
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                checks,
                gate = "STOP_NAIVE_CONTRACT_ID_SUBSTITUTION",
                integratedTransportCorrectness = "not_run",
                rpcPerformance = "not_run",
                scope = "Public descriptor/policy counterexample and deterministic protocol model only"
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0; // The gate successfully detected the rejected candidate; not a feature pass.
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    // Mirrors the two existing writes in SharpLinkClient.InvokerSend.cs. These routines intentionally
    // exclude serialization, admission, transport, async lifetimes and all production performance.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WritePrefix(RpcMethodDescriptor method, Span<byte> prefix)
    {
        BinaryPrimitives.WriteInt64LittleEndian(prefix, method.ContractId);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[8..], method.MethodId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteExpandedPrefix(SeparateIdentityDescriptor method, Span<byte> prefix)
    {
        BinaryPrimitives.WriteInt64LittleEndian(prefix, method.RouteId);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[8..], method.LogicalMethod.MethodId);
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly record struct SeparateIdentityDescriptor(RpcMethodDescriptor LogicalMethod, long RouteId);

    private sealed class LogicalContractPolicy(long expectedContractId) : ISharpLinkEndpointAdmissionPolicy
    {
        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
            => new(method.ContractId == expectedContractId, 0, null);

        public void Report(in SharpLinkEndpointOutcome outcome, long token) { }
    }
}
