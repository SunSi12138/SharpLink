using System.Collections.Frozen;
using System.Reflection;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.BindingTests;

internal static partial class Program
{
    private static async Task CheckBindingTableAsync(ISharpLinkServer server, SharpLinkGeneratedContractDescriptor contract)
    {
        // Inspect the real server's already-built registration; do not introduce a product test API.
        var registry = (ServerServiceModuleRegistry)typeof(SharpLinkServer)
            .GetField("_serviceModuleRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var services = registry.ServicesStorage;
        var registration = services[contract.ContractId];
        var table = new StaticBindingTable(new StaticBindingBudget());
        var tokens = new HashSet<ulong>();
        StaticBindingAck first = default;
        for (var operation = 1UL; operation <= StaticBindingWire.Capacity; operation++)
        {
            table.ObserveOperation(operation);
            var ack = table.Install(contract.ContractId, registration, contract, 0);
            if (ack.Failure != StaticBindingFailure.None) throw new InvalidOperationException("Valid slot rejected.");
            if (operation == 1) first = ack;
            tokens.Add(ack.Token);
        }
        Check(table.Count == StaticBindingWire.Capacity, "binding table has exact bounded capacity");
        Check(tokens.Count == StaticBindingWire.Capacity, "distinct creators never alias full tokens");
        table.ObserveOperation(65);
        Check(table.Install(contract.ContractId, registration, contract, 0).Failure == StaticBindingFailure.Capacity,
            "65th binding rejects without table growth");
        await Expect<SharpLinkProtocolViolationException>(() => { table.ObserveOperation(65); return Task.CompletedTask; },
            "duplicate Bind operation is a protocol violation");
        var ping = contract.Methods.Single(m => m.Name == nameof(ISliceRpc.PingAsync));
        var status = table.Resolve(first.Token, ping.MethodId, ProtocolV2FrameFlags.HasReturn, out var captured, out var method);
        Check(status == StaticBindingFailure.None && ReferenceEquals(captured, registration) && method.Descriptor.MethodId == ping.MethodId,
            "captured binding keeps exact registration and method");
        Check(table.Resolve(uint.MaxValue, ping.MethodId, ProtocolV2FrameFlags.HasReturn, out _, out _) == StaticBindingFailure.UnknownBinding && table.Count == 64,
            "sparse wire token cannot expand table");
        Check(table.Resolve(first.Token, ping.MethodId, ProtocolV2FrameFlags.None, out _, out _) == StaticBindingFailure.UnsupportedShape,
            "method shape conflict rejects before invocation");
        table.Close();
        Check(table.Count == 0 && table.Resolve(first.Token, ping.MethodId, ProtocolV2FrameFlags.HasReturn, out _, out _) == StaticBindingFailure.UnknownBinding,
            "closed table detaches old entries");
        Check(table.Install(contract.ContractId, registration, contract, 0).Failure == StaticBindingFailure.Unavailable,
            "closed table cannot bind again");
    }
}
