using System.Buffers.Binary;
using System.Reflection;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

// Newly reconstructed fixture identity, not a recovered historical identity or a raw-client facade.
// The controlled peer may bind ONLY this actually registered contract/hash when explicitly opted in.
internal static class ManualClientContractFixture
{
    internal const long ContractId = 1;
    private const string Descriptor = "manual-client-fixture-v1:1:oneway-empty;2:unary-int32;3:server-int32;4:idempotent-unary-int32;5:client-int32;6:duplex-int32;301:timed-oneway-empty;302:timed-unary-int32;303:timed-oneway-stream-int32;305:timed-compressed-unary-int32;306:timed-oneway-emission;307:timed-oneway-serialization;308:timed-oneway-stream-stall;309:timed-client-stream-stall;310:timed-duplex-stall;311:timed-compressed-oneway;312:timed-unary-unrun-timer";
    private static readonly byte[] HashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(Descriptor));
    internal static readonly RpcHash128 AssemblyHash = new(
        BinaryPrimitives.ReadUInt64BigEndian(HashBytes),
        BinaryPrimitives.ReadUInt64BigEndian(HashBytes.AsSpan(8)));
    internal static readonly ISharpLinkGeneratedAssemblyManifest Manifest = new FixtureManifest();

    // Historical tests reuse numeric method IDs across incompatible shapes. Each named domain
    // is a separate admitted manifest/hash, never an incompatible union or a method-ID remap.
    internal static readonly Domain CallOptions = new("CallOptions",
    [
        new(2, RpcMethodKind.Unary, "empty", "int32"),
        new(31, RpcMethodKind.OneWay, "stream:int32", "void"),
        new(32, RpcMethodKind.ServerStreaming, "empty", "stream:int32")
    ]);
    internal static readonly Domain DeadlineClockRead = new("DeadlineClockRead",
    [
        new(291, RpcMethodKind.Unary, "empty", "int32")
    ]);
    internal static readonly Domain EndpointLogicalOwner = new("EndpointLogicalOwner",
    [
        new(293, RpcMethodKind.Unary, "empty", "empty"),
        new(294, RpcMethodKind.OneWay, "empty", "void"),
        new(295, RpcMethodKind.ClientStreaming, "stream:empty", "empty"),
        new(296, RpcMethodKind.ServerStreaming, "empty", "stream:empty"),
        new(297, RpcMethodKind.DuplexStreaming, "stream:empty", "stream:empty")
    ]);
    internal static readonly Domain InterceptorDeadline = new("InterceptorDeadline",
    [
        new(2872, RpcMethodKind.Unary, "empty", "int32"),
        new(2873, RpcMethodKind.ServerStreaming, "empty", "stream:int32")
    ]);
    internal static readonly Domain LifetimeTelemetry = new("LifetimeTelemetry",
    [
        new(901, RpcMethodKind.Unary, "empty", "int32"),
        new(902, RpcMethodKind.Unary, "empty", "int32"),
        new(903, RpcMethodKind.Unary, "empty", "int32"),
        new(904, RpcMethodKind.Unary, "empty", "int32"),
        new(905, RpcMethodKind.Unary, "empty", "int32"),
        new(906, RpcMethodKind.Unary, "empty", "int32"),
        new(907, RpcMethodKind.Unary, "empty", "int32")
    ]);
    internal static readonly Domain LogicalDeadline = new("LogicalDeadline",
    [
        new(291, RpcMethodKind.Unary, "empty", "empty"),
        new(292, RpcMethodKind.ServerStreaming, "empty", "stream:empty")
    ]);
    internal static readonly Domain OneWayLease = new("OneWayLease",
    [
        new(297, RpcMethodKind.OneWay, "stream:int32", "void")
    ]);
    internal static readonly Domain OneWayShape = new("OneWayShape",
    [
        new(295, RpcMethodKind.OneWay, "empty", "void"),
        new(296, RpcMethodKind.OneWay, "stream:int32", "void")
    ]);
    internal static readonly Domain OneWayTimeBudget = new("OneWayTimeBudget",
    [
        new(290, RpcMethodKind.OneWay, "stream:int32", "void"),
        new(291, RpcMethodKind.OneWay, "stream:int32", "void"),
        new(292, RpcMethodKind.OneWay, "stream:int32", "void"),
        new(293, RpcMethodKind.OneWay, "stream:int32", "void"),
        new(294, RpcMethodKind.OneWay, "stream:int32", "void")
    ]);
    internal static readonly Domain ServerStreamingTerminalSignal = new("ServerStreamingTerminalSignal",
    [
        new(313, RpcMethodKind.ServerStreaming, "empty", "stream:int32")
    ]);
    internal static readonly Domain TimeBudget = new("TimeBudget",
    [
        new(287, RpcMethodKind.Unary, "empty", "int32"),
        new(288, RpcMethodKind.ClientStreaming, "stream:int32", "int32"),
        new(289, RpcMethodKind.ServerStreaming, "empty", "stream:int32"),
        new(320, RpcMethodKind.Unary, "empty", "int32"),
        new(321, RpcMethodKind.OneWay, "empty", "void"),
        new(322, RpcMethodKind.ClientStreaming, "stream:int32", "int32"),
        new(323, RpcMethodKind.ServerStreaming, "empty", "stream:int32"),
        new(324, RpcMethodKind.DuplexStreaming, "stream:int32", "stream:int32"),
        new(326, RpcMethodKind.OneWay, "empty", "void")
    ]);
    internal static readonly Domain TimedUnaryEmission = new("TimedUnaryEmission",
    [
        new(291, RpcMethodKind.Unary, "empty", "int32"),
        new(292, RpcMethodKind.Unary, "empty", "int32"),
        new(293, RpcMethodKind.Unary, "empty", "int32")
    ]);
    internal static readonly Domain DynamicModuleDeadline = new("DynamicModuleDeadline",
    [
        new(1901, RpcMethodKind.ServerStreaming, "empty", "stream:int32")
    ]);
    internal static readonly Domain StreamingFlagsUnary = new("StreamingFlagsUnary",
    [
        new(933, RpcMethodKind.Unary, "empty", "int32")
    ]);
    internal static readonly Domain StreamingFlagsClient = new("StreamingFlagsClient",
    [
        new(933, RpcMethodKind.ClientStreaming, "stream:int32", "int32")
    ]);
    internal static readonly Domain StreamingFlagsServer = new("StreamingFlagsServer",
    [
        new(933, RpcMethodKind.ServerStreaming, "empty", "stream:int32")
    ]);
    internal static readonly Domain StreamingFlagsDuplex = new("StreamingFlagsDuplex",
    [
        new(933, RpcMethodKind.DuplexStreaming, "stream:int32", "stream:int32")
    ]);

    internal static Domain StreamingFlags(RpcMethodKind kind) => kind switch
    {
        RpcMethodKind.Unary => StreamingFlagsUnary,
        RpcMethodKind.ClientStreaming => StreamingFlagsClient,
        RpcMethodKind.ServerStreaming => StreamingFlagsServer,
        RpcMethodKind.DuplexStreaming => StreamingFlagsDuplex,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal readonly record struct MethodShape(long Id, RpcMethodKind Kind, string Request, string Response);

    internal sealed class Domain
    {
        internal string Name { get; }
        internal RpcHash128 AssemblyHash { get; }
        internal ISharpLinkGeneratedAssemblyManifest Manifest { get; }

        internal Domain(string name, MethodShape[] methods)
        {
            if (methods.Length == 0 || methods.Any(method => method.Id <= 0 || !Enum.IsDefined(method.Kind) ||
                    string.IsNullOrEmpty(method.Request) || string.IsNullOrEmpty(method.Response)) ||
                methods.Select(method => method.Id).Distinct().Count() != methods.Length)
                throw new InvalidOperationException("A fixture domain requires a nonempty unique method-ID set.");
            Name = name;
            var descriptor = "manual-client-domain-v1:" + name + ":" + string.Join(";", methods.Select(
                method => $"{method.Id}:{method.Kind}:{method.Request}:{method.Response}"));
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(descriptor));
            AssemblyHash = new(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8)));
            var facts = methods.Select(method => new SharpLinkGeneratedMethodDescriptor(
                name + method.Id, method.Id, method.Kind, true, method.Request, method.Response,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{descriptor}:{method.Id}"))))).ToArray();
            Manifest = new DomainManifest(descriptor, AssemblyHash, Convert.ToHexStringLower(bytes), facts);
        }

        internal TestClientTransportFactory CreateTransport(ProtocolV2Capabilities capabilities = ProtocolV2Capabilities.None)
            => new(capabilities, contractManifest: [new(ContractId, AssemblyHash)],
                acceptManualContractBindings: true, manualContractDomain: this);
    }

    private sealed class DomainManifest(string descriptor, RpcHash128 hash, string fingerprint,
        SharpLinkGeneratedMethodDescriptor[] methods) : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "manual-client-domain-v1";
        public Assembly OwnerAssembly => typeof(IContract).Assembly;
        public RpcHash128 RpcAssemblyHash => hash;
        public string CompileTimeDescriptor => descriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } =
        [new(typeof(IContract), typeof(IContract).FullName!, ContractId, fingerprint, methods,
            static (channel, _) => new Proxy(channel),
            static _ => throw new NotSupportedException("Controlled client transport fixture has no server stub."))];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }

    internal interface IContract : IService;
    private sealed class Proxy(IRpcContractChannel channel) : IContract
    {
        internal IRpcContractChannel Channel { get; } = channel;
    }

    private sealed class FixtureManifest : ISharpLinkGeneratedAssemblyManifest
    {
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "reconstructed-manual-client-fixture-v1";
        public Assembly OwnerAssembly => typeof(IContract).Assembly;
        public RpcHash128 RpcAssemblyHash => AssemblyHash;
        public string CompileTimeDescriptor => Descriptor;
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts { get; } =
        [
            new(typeof(IContract), typeof(IContract).FullName!, ContractId, Convert.ToHexStringLower(HashBytes),
            [
                Method("OneWay", 1, RpcMethodKind.OneWay, "empty", "void"),
                Method("Unary", 2, RpcMethodKind.Unary, "empty", "int32"),
                Method("ServerStreaming", 3, RpcMethodKind.ServerStreaming, "empty", "stream:int32"),
                Method("IdempotentUnary", 4, RpcMethodKind.Unary, "empty", "int32"),
                Method("ClientStreaming", 5, RpcMethodKind.ClientStreaming, "stream:int32", "int32"),
                Method("DuplexStreaming", 6, RpcMethodKind.DuplexStreaming, "stream:int32", "stream:int32"),
                // Exact IDs used by deadline-publication controls; do not alias unrelated shapes.
                Method("TimedPlainOneWay", 301, RpcMethodKind.OneWay, "empty", "void"),
                Method("TimedUnarySerialization", 302, RpcMethodKind.Unary, "int32", "int32"),
                Method("TimedOneWayStreamRefusal", 303, RpcMethodKind.OneWay, "stream:int32", "void"),
                Method("TimedUnaryCompression", 305, RpcMethodKind.Unary, "int32", "int32"),
                Method("TimedOneWayEmission", 306, RpcMethodKind.OneWay, "empty", "void"),
                Method("TimedOneWaySerialization", 307, RpcMethodKind.OneWay, "empty", "void"),
                Method("TimedOneWayStreamStall", 308, RpcMethodKind.OneWay, "stream:int32", "void"),
                Method("TimedClientStreamStall", 309, RpcMethodKind.ClientStreaming, "stream:int32", "int32"),
                Method("TimedDuplexStall", 310, RpcMethodKind.DuplexStreaming, "stream:int32", "stream:int32"),
                Method("TimedOneWayCompression", 311, RpcMethodKind.OneWay, "empty", "void"),
                Method("TimedUnaryUnrunTimer", 312, RpcMethodKind.Unary, "empty", "int32")
            ], static (channel, _) => new Proxy(channel),
                static _ => throw new NotSupportedException("Controlled client transport fixture has no server stub."))
        ];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
        private static SharpLinkGeneratedMethodDescriptor Method(string name, long id, RpcMethodKind kind,
            string request, string response)
            => new(name, id, kind, true, request, response,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Descriptor}:{id}:{kind}:{request}:{response}"))));
    }
}
