using System.Buffers;
using SharpLink.Abstractions;

namespace SharpLink.PackageSmoke;

// Compiles outside all framework friend assemblies against the actual local NuGet packages.
internal static class MethodFactsContractSmoke
{
    internal static void Run()
    {
        IRpcStub stub = new ExternalStub();
        var facts = stub.ResolveMethodFacts(9);
        if (!ReferenceEquals(facts, stub.ResolveMethodFacts(9)) ||
            !ReferenceEquals(stub.ResolveMethodFacts(10), RpcMethodFacts.UnknownMethod) ||
            facts.ToDescriptor() != new RpcMethodDescriptor(7, 9, RpcMethodKind.Unary, false,
                false, true, TimeSpan.FromTicks(123456789), true))
            throw new InvalidOperationException("Packed custom stub must preserve complete cached facts.");
    }

    private sealed class ExternalStub : IRpcStub
    {
        private static readonly RpcMethodFacts Facts = new(7, 9, RpcMethodKind.Unary, 0,
            false, false, hasMethodTimeout: true, methodTimeout: TimeSpan.FromTicks(123456789), isIdempotent: true);
        public long InterfaceHash => 7;
        public RpcMethodFacts ResolveMethodFacts(long methodHash) => methodHash == 9 ? Facts : RpcMethodFacts.UnknownMethod;
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge,
            RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
            RpcMethodFacts method, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => ValueTask.CompletedTask;
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, RpcMethodFacts method,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
