using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;
using SharpLink.Sdk;

[assembly: SharpLinkClusterContractAssembly("n1", typeof(SharpLink.N1.StaticBindEvidence.IProbe))]

namespace SharpLink.N1.StaticBindEvidence;

[RpcCodec(typeof(ProbePayloadCodec))]
public sealed class ProbePayload { public int Value { get; set; } }
[RpcCodecSemanticIdentity(0x7751000000000001UL, 0x7751000000000002UL)]
public sealed class ProbePayloadCodec : IRpcCodec<ProbePayload>
{
    public static int Serializes, Deserializes;
    public void Serialize(in ProbePayload value, IBufferWriter<byte> writer)
    {
        Interlocked.Increment(ref Serializes);
        BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value.Value); writer.Advance(4);
    }
    public ProbePayload Deserialize(in ReadOnlySequence<byte> buffer)
    {
        Interlocked.Increment(ref Deserializes);
        if (buffer.Length != 4) throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Probe payload length differs.");
        Span<byte> bytes = stackalloc byte[4]; buffer.CopyTo(bytes);
        return new() { Value = BinaryPrimitives.ReadInt32LittleEndian(bytes) };
    }
}
[RpcContract]
public interface IProbe : IService
{
    ValueTask<int> EchoAsync(ProbePayload payload, CancellationToken cancellationToken);
    [Oneway, NonCancellable] ValueTask NotifyAsync(ProbePayload payload);
}
[RpcService]
public sealed class ProbeService : IProbe
{
    public static int Created, Echoes, Notifications;
    public static TaskCompletionSource QueueBlockEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static TaskCompletionSource QueueBlockRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static TaskCompletionSource BlockEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static TaskCompletionSource BlockRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static TaskCompletionSource? NotifyRelease;
    public static readonly TaskCompletionSource Notified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ProbeService() => Interlocked.Increment(ref Created);
    public ValueTask<int> EchoAsync(ProbePayload payload, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Echoes);
        if (payload.Value is 99 or 101 or 102) return SlowEchoAsync(payload.Value, cancellationToken);
        return ValueTask.FromResult(payload.Value + 7);
    }
    private static async ValueTask<int> SlowEchoAsync(int value, CancellationToken cancellationToken)
    {
        if (value == 99) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        else if (value == 102) { QueueBlockEntered.TrySetResult(); await QueueBlockRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        else { BlockEntered.TrySetResult(); await BlockRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        return value + 7;
    }
    public ValueTask NotifyAsync(ProbePayload payload)
    { Interlocked.Increment(ref Notifications); if (payload.Value == 31) Notified.TrySetResult(); return NotifyRelease is { } release ? new ValueTask(release.Task) : ValueTask.CompletedTask; }
}

internal static partial class Program
{
    private static int _checks;
    internal static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("ASSERT: " + name); _checks++; Console.WriteLine("PASS " + name); }
    internal static TException Throws<TException>(Action action, string name) where TException : Exception
    {
        try { action(); } catch (TException exception) { Check(true, name); return exception; }
        throw new InvalidOperationException("ASSERT expected " + typeof(TException).Name + ": " + name);
    }
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "components") Components();
            else if (args.Length == 1 && args[0] == "selection-components") SelectionComponents();
            else if (args.Length == 1 && args[0] == "publication-components") await PublicationComponentsAsync().ConfigureAwait(false);
            else if (args.Length == 2 && args[0] == "rpc" && args[1] == "N1-STATIC-BIND-20261115-V1")
                await RunRpcAsync().ConfigureAwait(false);
            else throw new ArgumentException("Use components, or the exact reviewed hosted protocol UID.");
            WriteResult(args, null);
            Console.WriteLine($"N1_RESULT success checks={_checks}"); return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("N1_FIRST_FAILURE " + exception);
            try { WriteResult(args, exception); } catch (Exception reportFailure) { Console.Error.WriteLine("N1_REPORT_FAILURE " + reportFailure); }
            return 1;
        }
    }
    private static void WriteResult(string[] args, Exception? failure)
    {
        var path = Environment.GetEnvironmentVariable("N1_RESULT_PATH") ?? Path.Combine(AppContext.BaseDirectory, "n1-result.json");
        var primary = failure;
        while (primary is AggregateException aggregate && aggregate.InnerExceptions.Count != 0) primary = aggregate.InnerExceptions[0];
        var classification = primary is TimeoutException ? "guard" : primary?.Message.StartsWith("FIXTURE", StringComparison.Ordinal) == true ? "fixture" : "source";
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            uid = "N1-STATIC-BIND-20261115-V1", mode = args.FirstOrDefault(), source = Environment.GetEnvironmentVariable("N1_SOURCE_SHA"),
            outcome = failure is null ? "PASS" : primary is TimeoutException ? "INCONCLUSIVE" : "FAIL", classification = failure is null ? null : classification,
            checks = _checks, runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(), firstFailure = failure?.ToString()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void Components()
    {
        Check(Unsafe.SizeOf<RpcMethodDescriptor>() == 32 && !RuntimeHelpers.IsReferenceOrContainsReferences<RpcMethodDescriptor>(), "facts-32B-scalar");
        foreach (var ticks in new long?[] { null, 0, -1, 1, 10001, long.MaxValue })
        {
            var method = new RpcMethodDescriptor(9, 11, RpcMethodKind.Unary, true, false, true,
                ticks is { } t ? TimeSpan.FromTicks(t) : null) { SupportsCancellation = true };
            Check(method.MethodTimeout?.Ticks == ticks && method.SupportsCancellation && ContractMethodFacts.IsValid(method, 9), "facts-ticks-" + ticks);
        }
        var request = new ContractBindRequest(9, new(1, 2), RpcRegistrationRequirement.Compatible, null);
        var bytes = ContractBindingWire.WriteRequest(request);
        Check(bytes.Length == 40 && ContractBindingWire.ReadRequest(new(bytes)) == request, "bind-40B-roundtrip");
        var receipt = new RpcRegistrationReceipt(Guid.Parse("77500000-0000-0000-0000-000000000001"), Guid.Parse("77500000-0000-0000-0000-000000000002"), 123, 9, new(1, 2), 42);
        request = request with { Receipt = receipt, Requirement = RpcRegistrationRequirement.ExactRegistration };
        bytes = ContractBindingWire.WriteRequest(request);
        Check(bytes.Length == 112 && ContractBindingWire.ReadRequest(new(bytes)) == request, "bind-112B-roundtrip");
        var ack = new ContractBindAck(ContractBindStatus.Success, 9, (5UL << 32) | 64, 42, 123);
        Check(ContractBindingWire.ReadAck(new(ContractBindingWire.WriteAck(ack))) == ack, "ack-40B-roundtrip");
        Throws<SharpLinkException>(() => ContractBindingWire.ReadAck(new(ContractBindingWire.WriteAck(ack with { Token = 64 }))), "reject-zero-incarnation");
        Throws<SharpLinkException>(() => ContractBindingWire.ReadRequest(new(new byte[41])), "reject-trailing-bind");
        var server = Guid.Parse("77500000-0000-0000-0000-000000000001");
        var epoch = Guid.Parse("77500000-0000-0000-0000-000000000002");
        var handshakeWriter = new ArrayBufferWriter<byte>();
        ProtocolV2PayloadCodec.WriteHandshakeResponse(handshakeWriter, new(6, ProtocolV2Capabilities.ContractManifest,
            4 * 1024 * 1024, 1024 * 1024, 16 * 1024 * 1024, server, epoch));
        var handshake = ProtocolV2PayloadCodec.ReadHandshakeResponse(new(handshakeWriter.WrittenMemory), new SharpLinkProtocolOptions());
        Check(handshakeWriter.WrittenCount == 55 && handshake.ServerInstanceId == server && handshake.RegistryEpoch == epoch, "wire6-55B-real-identity-shape");
        var manifestWriter = new ArrayBufferWriter<byte>();
        var controlManifest = new ProtocolV2ContractManifest(server, epoch, 123, new[] { new KeyValuePair<long, RpcHash128>(9, new(1, 2)) });
        ProtocolV2ContractManifestCodec.Write(manifestWriter, controlManifest, new SharpLinkProtocolOptions());
        var decodedManifest = ProtocolV2ContractManifestCodec.Read(new(manifestWriter.WrittenMemory), new SharpLinkProtocolOptions());
        Check(manifestWriter.WrittenCount == 68 && decodedManifest.ServerInstanceId == server && decodedManifest.RegistryEpoch == epoch, "wire6-44B-manifest-header");
        Check(ProtocolV2Constants.MinorVersion == 6 && ProtocolV2Constants.MinimumCompatibleMinorVersion == 6, "wire6-no-legacy-floor");
        var badFacts = new RpcMethodDescriptor(9, 11, RpcMethodKind.Unary, true, true, false, null, ClientStreamCount: 1);
        Check(!ContractMethodFacts.IsValid(badFacts, 9), "cold-facts-reject-unary-input");
        var quota = new ContractBindingQuota();
        for (var i = 0; i < 65536; i++) if (!quota.TryReserveBinding()) throw new InvalidOperationException("Premature endpoint binding capacity.");
        Check(!quota.TryReserveBinding() && quota.Bindings == 65536, "endpoint-binding-hard-limit");
        for (var i = 0; i < 65536; i++) quota.ReturnBinding();
        for (var i = 0; i < 1024; i++) if (!quota.TryReserveOperation()) throw new InvalidOperationException("Premature operation capacity.");
        Check(!quota.TryReserveOperation() && quota.Operations == 1024, "endpoint-operation-hard-limit");
        for (var i = 0; i < 1024; i++) quota.ReturnOperation();
        Check(quota.Bindings == 0 && quota.Operations == 0, "quota-physical-return");
        var manifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Single(m => m.OwnerAssembly == typeof(IProbe).Assembly);
        var contract = manifest.Contracts.Single(c => c.ContractType == typeof(IProbe));
        using var context = new SharpLinkRuntimeContextBuilder().Build();
        var stub = contract.StubFactory(context.Codecs);
        Check(manifest.ApiVersion == 6 && SharpLinkGeneratedManifestVersions.AbiIdentity == "sharplink-3.0-api6-contract-channel-v1", "generated-api6-exact-identity");
        foreach (var method in contract.Methods)
            Check(stub.TryResolveMethod(method.MethodId, out var facts) && facts == method.Facts && facts.SupportsCancellation == method.SupportsCancellation, "generated-fullfacts-" + method.Name);
        Check(!stub.TryResolveMethod(long.MaxValue, out _), "required-resolver-unknown-is-false");
    }

}
