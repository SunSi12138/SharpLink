using System.Buffers.Binary;
using System.Reflection;
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
