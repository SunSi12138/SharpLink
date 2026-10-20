#if SHARPLINK_STATIC_BINDING_PROTOTYPE
using System;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static class StaticBindingShapeChecks
{
    // Independent copy of the old per-request predicate, not the candidate's mask builder.
    private static bool OriginalAllows(RpcMethodDescriptor descriptor, ProtocolV2FrameFlags flags)
        => descriptor.Kind is RpcMethodKind.Unary or RpcMethodKind.OneWay &&
           !descriptor.HasClientStreams && descriptor.ClientStreamCount == 0 &&
           ((flags & ProtocolV2FrameFlags.OneWay) != 0) == (descriptor.Kind == RpcMethodKind.OneWay) &&
           ((flags & ProtocolV2FrameFlags.HasReturn) != 0) == descriptor.HasResponsePayload;

    internal static void Run(SharpLinkGeneratedContractDescriptor contract, IRpcStub stub)
    {
        var predicates = 0;
        var descriptors = 0;
        foreach (var count in new[] { -1, 0, 1, 2, int.MaxValue })
            for (var kind = 0; kind <= byte.MaxValue; kind++)
                for (var bits = 0; bits < 4; bits++)
                {
                    var descriptor = new RpcMethodDescriptor(17, 19, (RpcMethodKind)kind,
                        (bits & 1) != 0, (bits & 2) != 0, true, TimeSpan.FromTicks(-3),
                        IsIdempotent: true, ClientStreamCount: count, ResponseNullable: true);
                    var method = new StaticBindingMethod(descriptor, true);
                    var clone = method with { };
                    StaticBindingEvidence.Require(method.Descriptor == descriptor &&
                        method.SupportsCancellation && clone == method, "Frozen method facts changed");
                    descriptor = descriptor with { MethodId = 23 };
                    StaticBindingEvidence.Require(method.Descriptor.MethodId == 19,
                        "Changing a local descriptor must not mutate the binding facts");
                    descriptors++;
                    for (var value = 0; value <= byte.MaxValue; value++)
                    {
                        var flags = (ProtocolV2FrameFlags)value;
                        var actual = (int)(flags & StaticBindingMethod.ShapeMask) == method.RequiredRequestFlags;
                        StaticBindingEvidence.Require(actual == OriginalAllows(method.Descriptor, flags),
                            "Frozen request shape differs from original predicate");
                        predicates++;
                    }
                }

        // Exercise the real resolver as well as the predicate, including unsupported methods.
        var table = new StaticBindingTable();
        var registration = ServiceRegistration.CreateSingleton(typeof(IBenchmarkRpc), stub,
            new BenchmarkRpcService(), ownsService: false);
        var ack = table.Install(contract.ContractId, registration, contract, 1);
        StaticBindingEvidence.Require(ack.Failure == StaticBindingFailure.None, "Shape table installation");
        var tableCases = 0;
        foreach (var generated in contract.Methods)
        {
            StaticBindingEvidence.Require(stub.TryGetMethodDescriptor(generated.MethodId, out var descriptor),
                "Generated method facts absent");
            StaticBindingMethod? first = null;
            for (var value = 0; value <= byte.MaxValue; value++)
            {
                var flags = (ProtocolV2FrameFlags)value;
                var expected = OriginalAllows(descriptor, flags)
                    ? StaticBindingFailure.None : StaticBindingFailure.UnsupportedShape;
                var result = table.Resolve(ack.Token, generated.MethodId, flags, out var resolved, out var method);
                StaticBindingEvidence.Require(result == expected && method.Descriptor == descriptor &&
                    method.SupportsCancellation == generated.SupportsCancellation,
                    "Actual resolver changed method facts or shape result");
                first ??= method;
                StaticBindingEvidence.Require(ReferenceEquals(first, method), "Resolver re-created method facts");
                StaticBindingEvidence.Require(expected == StaticBindingFailure.None
                    ? ReferenceEquals(resolved, registration) : resolved is null,
                    "Registration exposed before shape validation");
                StaticBindingEvidence.Require(table.Resolve(ack.Token ^ (1UL << 32), generated.MethodId,
                    flags, out _, out _) == StaticBindingFailure.UnknownBinding, "Full token validation bypassed");
                StaticBindingEvidence.Require(table.Resolve(ack.Token, long.MaxValue, flags, out _, out _) ==
                    StaticBindingFailure.UnknownMethod, "Unknown method validation bypassed");
                tableCases++;
            }
        }
        table.Close();
        StaticBindingEvidence.Require(table.Resolve(ack.Token, 19, 0, out _, out _) ==
            StaticBindingFailure.UnknownBinding, "Closed table accepted a token");
        Console.WriteLine($"STATIC_BINDING_SHAPE_PASS descriptors={descriptors} predicates={predicates} tableCases={tableCases}");
    }
}
#endif
