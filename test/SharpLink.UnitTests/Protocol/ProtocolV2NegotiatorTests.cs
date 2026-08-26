using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SharpLink.UnitTests.Protocol;

public class ProtocolV2NegotiatorTests
{
    [Test]
    public void ClientOfferShouldContainOnlyPolicyAndAuthenticationInputs()
    {
        var providers = Bindings("client-first", "client-second");
        var policy = ProtocolV2Negotiator.CreateImplementedPolicy(
            8192,
            2048,
            4096,
            providers);
        var authentication = new byte[] { 1, 2, 3 };

        var offer = ProtocolV2Negotiator.CreateClientOffer(
            policy,
            ProtocolV2Capabilities.Metadata | ProtocolV2Capabilities.Compression,
            authentication);

        Ensure(offer.MinorVersion == ProtocolV2Constants.MinorVersion,
            "the implemented policy must advertise the current minor version");
        Ensure(offer.SupportedCapabilities == RpcSessionProtocolRules.KnownCapabilities,
            "one central implemented-capability set must drive the offer");
        Ensure(offer.RequiredCapabilities ==
               (ProtocolV2Capabilities.Metadata | ProtocolV2Capabilities.Compression),
            "the caller's required capabilities must remain explicit policy input");
        Ensure(offer.MaxFramePayloadBytes == 8192 &&
               offer.StreamReceiveWindowBytes == 2048 &&
               offer.ConnectionReceiveWindowBytes == 4096,
            "the offer must publish the complete local limit policy");
        Ensure(offer.AuthenticationPayload.Span.SequenceEqual(authentication),
            "the negotiator must carry the opaque authentication payload without interpreting it");
        Ensure(offer.CompressionProfiles.Span.SequenceEqual(new[] { "client-first", "client-second" }),
            "the client provider order must become the offer preference order");
    }

    [Test]
    public void PolicyConstructionShouldRejectEveryIllegalLocalState()
    {
        var binding = Bindings("valid")[0];
        var cases = new Action[]
        {
            () => CreatePolicy((ProtocolV2Capabilities)(1UL << 63)),
            () => CreatePolicy(ProtocolV2Capabilities.None,
                minorVersion: checked((ushort)(ProtocolV2Constants.MinorVersion + 1))),
            () => CreatePolicy(ProtocolV2Capabilities.None,
                maxFramePayloadBytes: SharpLinkProtocolOptions.MinMaxFramePayloadBytes - 1),
            () => CreatePolicy(ProtocolV2Capabilities.None,
                maxFramePayloadBytes: SharpLinkProtocolOptions.MaxMaxFramePayloadBytes + 1),
            () => CreatePolicy(ProtocolV2Capabilities.None, streamReceiveWindowBytes: 0),
            () => CreatePolicy(ProtocolV2Capabilities.None,
                streamReceiveWindowBytes: 2,
                connectionReceiveWindowBytes: 1),
            () => CreatePolicy(ProtocolV2Capabilities.Compression),
            () => CreatePolicy(ProtocolV2Capabilities.None, [binding]),
            () => CreatePolicy(ProtocolV2Capabilities.Compression, [binding, binding]),
            () => CreatePolicy(
                ProtocolV2Capabilities.Compression,
                [new SharpLinkCompressionProviderBinding("different", binding.Provider)])
        };

        foreach (var item in cases)
        {
            var failure = CaptureArgumentException(item);
            Ensure(failure.ParamName is not null,
                "every illegal local policy must fail immediately with an actionable argument name");
        }
    }

    [Test]
    public void CapabilityMatrixShouldProduceSymmetricServerAndClientResults()
    {
        var capabilities = new[]
        {
            ProtocolV2Capabilities.Metadata,
            ProtocolV2Capabilities.Compression,
            ProtocolV2Capabilities.FlowControl,
            ProtocolV2Capabilities.HealthCheck,
            ProtocolV2Capabilities.CancellationReason
        };

        foreach (var capability in capabilities)
        {
            foreach (var serverSupports in new[] { false, true })
            {
                var clientProviders = capability == ProtocolV2Capabilities.Compression
                    ? Bindings("shared")
                    : Array.Empty<SharpLinkCompressionProviderBinding>();
                var serverProviders = capability == ProtocolV2Capabilities.Compression && serverSupports
                    ? Bindings("shared")
                    : Array.Empty<SharpLinkCompressionProviderBinding>();
                var offer = CreateOffer(capability, ProtocolV2Capabilities.None, clientProviders);
                var serverPolicy = CreatePolicy(
                    serverSupports ? capability : ProtocolV2Capabilities.None,
                    serverProviders);
                var clientPolicy = CreatePolicy(capability, clientProviders);

                var server = ProtocolV2Negotiator.NegotiateServer(
                    offer,
                    serverPolicy);
                var client = ProtocolV2Negotiator.ValidateServerResponse(
                    offer,
                    server.Response,
                    clientPolicy);
                var expected = serverSupports ? capability : ProtocolV2Capabilities.None;

                Ensure(server.Options.Capabilities == expected && client.Capabilities == expected,
                    $"{capability}, server={serverSupports}: both peers must derive the same capability result");
                Ensure(server.Response.NegotiatedCapabilities == expected,
                    $"{capability}, server={serverSupports}: the wire response must match the immutable result");
                Ensure((server.Options.CompressionBinding is not null) ==
                       (expected == ProtocolV2Capabilities.Compression) &&
                       (client.CompressionBinding is not null) ==
                       (expected == ProtocolV2Capabilities.Compression),
                    $"{capability}, server={serverSupports}: compression binding must follow capability selection");
            }
        }

        var unknownOptional = (ProtocolV2Capabilities)(1UL << 63);
        var unknownOffer = CreateOffer(unknownOptional, ProtocolV2Capabilities.None, []);
        var unknownServer = ProtocolV2Negotiator.NegotiateServer(
            unknownOffer,
            CreatePolicy(ProtocolV2Capabilities.None));
        var unknownClient = ProtocolV2Negotiator.ValidateServerResponse(
            unknownOffer,
            unknownServer.Response,
            CreatePolicy(ProtocolV2Capabilities.None));
        Ensure(unknownServer.Options.Capabilities == ProtocolV2Capabilities.None &&
               unknownClient.Capabilities == ProtocolV2Capabilities.None,
            "unknown optional capabilities must be ignored for forward compatibility");
    }

    [Test]
    public void UnsupportedRequiredCapabilityShouldReturnUnimplemented()
    {
        var capabilities = new[]
        {
            ProtocolV2Capabilities.Metadata,
            ProtocolV2Capabilities.Compression,
            ProtocolV2Capabilities.FlowControl,
            ProtocolV2Capabilities.HealthCheck,
            ProtocolV2Capabilities.CancellationReason,
            (ProtocolV2Capabilities)(1UL << 63)
        };

        foreach (var capability in capabilities)
        {
            var providers = capability == ProtocolV2Capabilities.Compression
                ? Bindings("required")
                : Array.Empty<SharpLinkCompressionProviderBinding>();
            var failure = CaptureSharpLinkException(() => ProtocolV2Negotiator.NegotiateServer(
                CreateOffer(capability, capability, providers),
                CreatePolicy(ProtocolV2Capabilities.None)));

            Ensure(failure.Code == SharpLinkErrorCode.Unimplemented,
                $"unsupported required capability {capability} must have the stable Unimplemented classification");
        }
    }

    [Test]
    public void ServerNegotiationShouldIntersectLimitsAtCurrentMinorBoundaries()
    {
        var cases = new[]
        {
            new
            {
                Offer = CreatePolicy(
                    ProtocolV2Capabilities.None,
                    minorVersion: ProtocolV2Constants.MinorVersion,
                    maxFramePayloadBytes: 8192,
                    streamReceiveWindowBytes: 4096,
                    connectionReceiveWindowBytes: 8192),
                Server = CreatePolicy(
                    ProtocolV2Capabilities.None,
                    minorVersion: ProtocolV2Constants.MinorVersion,
                    maxFramePayloadBytes: 4096,
                    streamReceiveWindowBytes: 2048,
                    connectionReceiveWindowBytes: 4096),
                ExpectedMinor = ProtocolV2Constants.MinorVersion,
                ExpectedFrame = 4096,
                ExpectedStream = 2048,
                ExpectedConnection = 4096
            },
            new
            {
                Offer = CreatePolicy(
                    ProtocolV2Capabilities.None,
                    minorVersion: ProtocolV2Constants.MinorVersion,
                    maxFramePayloadBytes: SharpLinkProtocolOptions.MinMaxFramePayloadBytes,
                    streamReceiveWindowBytes: 1,
                    connectionReceiveWindowBytes: 1),
                Server = CreatePolicy(
                    ProtocolV2Capabilities.None,
                    minorVersion: ProtocolV2Constants.MinorVersion,
                    maxFramePayloadBytes: SharpLinkProtocolOptions.MaxMaxFramePayloadBytes,
                    streamReceiveWindowBytes: int.MaxValue,
                    connectionReceiveWindowBytes: int.MaxValue),
                ExpectedMinor = ProtocolV2Constants.MinorVersion,
                ExpectedFrame = SharpLinkProtocolOptions.MinMaxFramePayloadBytes,
                ExpectedStream = 1,
                ExpectedConnection = 1
            }
        };

        foreach (var item in cases)
        {
            var offer = ProtocolV2Negotiator.CreateClientOffer(
                item.Offer,
                ProtocolV2Capabilities.None,
                ReadOnlyMemory<byte>.Empty);
            var result = ProtocolV2Negotiator.NegotiateServer(offer, item.Server);

            Ensure(result.Response.MinorVersion == item.ExpectedMinor &&
                   result.Response.MaxFramePayloadBytes == item.ExpectedFrame &&
                   result.Response.StreamReceiveWindowBytes == item.ExpectedStream &&
                   result.Response.ConnectionReceiveWindowBytes == item.ExpectedConnection,
                "server negotiation must select the lower minor and every lower receive limit together");
            Ensure(result.Options.ProtocolMinorVersion == item.ExpectedMinor &&
                   result.Options.MaxFramePayloadBytes == item.ExpectedFrame &&
                   result.Options.StreamReceiveWindowBytes == item.ExpectedStream &&
                   result.Options.ConnectionReceiveWindowBytes == item.ExpectedConnection,
                "the immutable result must exactly match the negotiated wire response");
        }
    }

    [Test]
    public void ClientValidationShouldRejectOutOfOfferVersionCapabilitiesAndLimits()
    {
        var offer = CreateOffer(
            ProtocolV2Capabilities.Metadata | ProtocolV2Capabilities.FlowControl,
            ProtocolV2Capabilities.Metadata,
            []);
        var valid = new ProtocolV2HandshakeResponse(
            offer.MinorVersion,
            offer.RequiredCapabilities,
            offer.MaxFramePayloadBytes,
            offer.StreamReceiveWindowBytes,
            offer.ConnectionReceiveWindowBytes);
        var cases = new[]
        {
            (Response: valid with { MinorVersion = checked((ushort)(offer.MinorVersion + 1)) },
                Code: SharpLinkErrorCode.Unimplemented, Name: "future minor"),
            (Response: valid with { NegotiatedCapabilities = ProtocolV2Capabilities.HealthCheck },
                Code: SharpLinkErrorCode.ProtocolViolation, Name: "unoffered capability"),
            (Response: valid with { NegotiatedCapabilities = ProtocolV2Capabilities.FlowControl },
                Code: SharpLinkErrorCode.ProtocolViolation, Name: "missing required capability"),
            (Response: valid with { MaxFramePayloadBytes = offer.MaxFramePayloadBytes + 1 },
                Code: SharpLinkErrorCode.ProtocolViolation, Name: "frame above offer"),
            (Response: valid with { StreamReceiveWindowBytes = offer.StreamReceiveWindowBytes + 1 },
                Code: SharpLinkErrorCode.ProtocolViolation, Name: "stream window above offer"),
            (Response: valid with { ConnectionReceiveWindowBytes = offer.ConnectionReceiveWindowBytes + 1 },
                Code: SharpLinkErrorCode.ProtocolViolation, Name: "connection window above offer"),
            (Response: valid with { StreamReceiveWindowBytes = 2, ConnectionReceiveWindowBytes = 1 },
                Code: SharpLinkErrorCode.ProtocolViolation, Name: "connection below stream")
        };

        foreach (var item in cases)
        {
            var failure = CaptureSharpLinkException(() =>
                ProtocolV2Negotiator.ValidateServerResponse(
                    offer,
                    item.Response,
                    CreatePolicy(
                        ProtocolV2Capabilities.Metadata | ProtocolV2Capabilities.FlowControl)));
            Ensure(failure.Code == item.Code,
                $"{item.Name} must use the expected structured error classification");
        }
    }

    [Test]
    public void CompressionMatrixShouldHonorOptionalRequiredAndServerPreference()
    {
        var clientProviders = Bindings("client-first", "shared-second", "server-first");
        var serverProviders = Bindings("server-first", "shared-second");
        var compressionPolicy = CreatePolicy(ProtocolV2Capabilities.Compression, serverProviders);
        var clientPolicy = CreatePolicy(ProtocolV2Capabilities.Compression, clientProviders);
        var optionalOffer = CreateOffer(
            ProtocolV2Capabilities.Compression,
            ProtocolV2Capabilities.None,
            clientProviders);

        var preferred = ProtocolV2Negotiator.NegotiateServer(
            optionalOffer,
            compressionPolicy);
        var preferredClient = ProtocolV2Negotiator.ValidateServerResponse(
            optionalOffer,
            preferred.Response,
            clientPolicy);
        Ensure(preferred.Response.CompressionProfile == "server-first" &&
               ReferenceEquals(preferred.Options.CompressionBinding?.Provider, serverProviders[0].Provider) &&
               ReferenceEquals(preferredClient.CompressionBinding?.Provider, clientProviders[2].Provider),
            "the server's provider order must select the wire profile and each peer must bind its own exact provider");

        var noIntersectionServer = Bindings("server-only");
        var noIntersectionPolicy = CreatePolicy(
            ProtocolV2Capabilities.Compression,
            noIntersectionServer);
        var optional = ProtocolV2Negotiator.NegotiateServer(
            optionalOffer,
            noIntersectionPolicy);
        var optionalClient = ProtocolV2Negotiator.ValidateServerResponse(
            optionalOffer,
            optional.Response,
            clientPolicy);
        Ensure(optional.Response.CompressionProfile is null &&
               optional.Options.CompressionBinding is null &&
               optionalClient.CompressionBinding is null &&
               optionalClient.Capabilities == ProtocolV2Capabilities.None,
            "optional compression with no profile intersection must be disabled symmetrically");

        var requiredOffer = optionalOffer with
        {
            RequiredCapabilities = ProtocolV2Capabilities.Compression
        };
        var requiredFailure = CaptureSharpLinkException(() => ProtocolV2Negotiator.NegotiateServer(
            requiredOffer,
            noIntersectionPolicy));
        Ensure(requiredFailure.Code == SharpLinkErrorCode.Unimplemented,
            "required compression with no profile intersection must fail as unsupported");
    }

    [Test]
    public void MalformedOfferMatrixShouldReturnProtocolViolation()
    {
        var valid = CreateOffer(ProtocolV2Capabilities.Metadata, ProtocolV2Capabilities.None, []);
        var cases = new[]
        {
            valid with
            {
                RequiredCapabilities = ProtocolV2Capabilities.FlowControl
            },
            valid with
            {
                MaxFramePayloadBytes = SharpLinkProtocolOptions.MinMaxFramePayloadBytes - 1
            },
            valid with { StreamReceiveWindowBytes = 0 },
            valid with { StreamReceiveWindowBytes = 2, ConnectionReceiveWindowBytes = 1 },
            valid with
            {
                CompressionProfiles = new[] { "profile-without-capability" }
            },
            valid with
            {
                SupportedCapabilities = ProtocolV2Capabilities.Compression,
                CompressionProfiles = ReadOnlyMemory<string>.Empty
            },
            valid with
            {
                SupportedCapabilities = ProtocolV2Capabilities.Compression,
                CompressionProfiles = new[] { "same", "same" }
            },
            valid with
            {
                SupportedCapabilities = ProtocolV2Capabilities.Compression,
                CompressionProfiles = new[] { new string('a', SharpLinkCompressionProfile.MaxAsciiBytes + 1) }
            },
            valid with
            {
                SupportedCapabilities = ProtocolV2Capabilities.Compression,
                CompressionProfiles = Enumerable.Range(0, SharpLinkCompressionOptions.MaxProviders + 1)
                    .Select(static index => $"profile-{index}")
                    .ToArray()
            }
        };

        foreach (var item in cases)
        {
            var failure = CaptureSharpLinkException(() => ProtocolV2Negotiator.NegotiateServer(
                item,
                CreatePolicy(ProtocolV2Capabilities.None)));
            Ensure(failure.Code == SharpLinkErrorCode.ProtocolViolation,
                "every malformed offer must fail before authentication with ProtocolViolation");
        }
    }

    [Test]
    public void MalformedResponseMatrixShouldReturnStructuredFailure()
    {
        var clientProviders = Bindings("offered");
        var offer = CreateOffer(
            ProtocolV2Capabilities.Compression,
            ProtocolV2Capabilities.None,
            clientProviders);
        var valid = new ProtocolV2HandshakeResponse(
            offer.MinorVersion,
            ProtocolV2Capabilities.Compression,
            offer.MaxFramePayloadBytes,
            offer.StreamReceiveWindowBytes,
            offer.ConnectionReceiveWindowBytes,
            "offered");
        var cases = new[]
        {
            valid with { NegotiatedCapabilities = (ProtocolV2Capabilities)(1UL << 63) },
            valid with { NegotiatedCapabilities = ProtocolV2Capabilities.None },
            valid with { CompressionProfile = null },
            valid with { CompressionProfile = "not-offered" }
        };

        foreach (var item in cases)
        {
            var failure = CaptureSharpLinkException(() =>
                ProtocolV2Negotiator.ValidateServerResponse(
                    offer,
                    item,
                    CreatePolicy(ProtocolV2Capabilities.Compression, clientProviders)));
            Ensure(failure.Code == SharpLinkErrorCode.ProtocolViolation,
                "malformed or unoffered response selections must be ProtocolViolation");
        }

        var unbindable = CaptureSharpLinkException(() =>
            ProtocolV2Negotiator.ValidateServerResponse(
                offer,
                valid,
                CreatePolicy(ProtocolV2Capabilities.Compression, Bindings("different"))));
        Ensure(unbindable.Code == SharpLinkErrorCode.ProtocolViolation,
            "an offered profile that cannot bind to the client context must be ProtocolViolation");
    }

    [Test]
    public void ServerResultShouldValidateToEquivalentClientSnapshot()
    {
        var clientProviders = Bindings("client-only", "shared");
        var serverProviders = Bindings("server-only", "shared");
        var offeredCapabilities = RpcSessionProtocolRules.KnownCapabilities;
        var offer = new ProtocolV2HandshakeRequest(
            ProtocolV2Constants.MinorVersion,
            offeredCapabilities,
            ProtocolV2Capabilities.Metadata | ProtocolV2Capabilities.FlowControl,
            16384,
            8192,
            16384,
            new byte[] { 7, 8 },
            clientProviders.Select(static binding => binding.WireProfile).ToArray());
        var serverPolicy = CreatePolicy(
            offeredCapabilities,
            serverProviders,
            minorVersion: ProtocolV2Constants.MinorVersion,
            maxFramePayloadBytes: 8192,
            streamReceiveWindowBytes: 4096,
            connectionReceiveWindowBytes: 8192);
        var clientPolicy = CreatePolicy(offeredCapabilities, clientProviders);

        var server = ProtocolV2Negotiator.NegotiateServer(offer, serverPolicy);
        var client = ProtocolV2Negotiator.ValidateServerResponse(
            offer,
            server.Response,
            clientPolicy);

        Ensure(server.Options.ProtocolMinorVersion == client.ProtocolMinorVersion &&
               server.Options.Capabilities == client.Capabilities &&
               server.Options.MaxFramePayloadBytes == client.MaxFramePayloadBytes &&
               server.Options.StreamReceiveWindowBytes == client.StreamReceiveWindowBytes &&
               server.Options.ConnectionReceiveWindowBytes == client.ConnectionReceiveWindowBytes,
            "server construction and client validation must derive equivalent immutable scalars");
        Ensure(server.Response.MinorVersion == client.ProtocolMinorVersion &&
               server.Response.NegotiatedCapabilities == client.Capabilities &&
               server.Response.MaxFramePayloadBytes == client.MaxFramePayloadBytes &&
               server.Response.StreamReceiveWindowBytes == client.StreamReceiveWindowBytes &&
               server.Response.ConnectionReceiveWindowBytes == client.ConnectionReceiveWindowBytes,
            "the wire response must be an exact projection of the immutable result");
        Ensure(server.Options.CompressionBinding?.WireProfile == "shared" &&
               client.CompressionBinding?.WireProfile == "shared" &&
               ReferenceEquals(server.Options.CompressionBinding?.Provider, serverProviders[1].Provider) &&
               ReferenceEquals(client.CompressionBinding?.Provider, clientProviders[1].Provider),
            "both peers must bind the same wire profile to their own context-owned provider");
    }

    [Test]
    public void RepeatedInputsShouldProduceEquivalentResultsAndErrors()
    {
        var clientProviders = Bindings("client-only", "shared");
        var serverProviders = Bindings("server-only", "shared");
        var offer = CreateOffer(
            RpcSessionProtocolRules.KnownCapabilities,
            ProtocolV2Capabilities.Metadata,
            clientProviders);
        var policy = CreatePolicy(
            RpcSessionProtocolRules.KnownCapabilities,
            serverProviders,
            maxFramePayloadBytes: 4096,
            streamReceiveWindowBytes: 1024,
            connectionReceiveWindowBytes: 2048);

        var first = ProtocolV2Negotiator.NegotiateServer(offer, policy);
        var second = ProtocolV2Negotiator.NegotiateServer(offer, policy);

        Ensure(first.Response == second.Response,
            "the same offer and policy must produce the same wire response");
        Ensure(first.Options.ProtocolMinorVersion == second.Options.ProtocolMinorVersion &&
               first.Options.Capabilities == second.Options.Capabilities &&
               first.Options.MaxFramePayloadBytes == second.Options.MaxFramePayloadBytes &&
               first.Options.StreamReceiveWindowBytes == second.Options.StreamReceiveWindowBytes &&
               first.Options.ConnectionReceiveWindowBytes == second.Options.ConnectionReceiveWindowBytes &&
               first.Options.CompressionBinding == second.Options.CompressionBinding,
            "the same offer and policy must produce equivalent immutable local results");

        var unsupported = offer with
        {
            SupportedCapabilities = (ProtocolV2Capabilities)(1UL << 63),
            RequiredCapabilities = (ProtocolV2Capabilities)(1UL << 63),
            CompressionProfiles = ReadOnlyMemory<string>.Empty
        };
        var firstFailure = CaptureSharpLinkException(() =>
            ProtocolV2Negotiator.NegotiateServer(unsupported, policy));
        var secondFailure = CaptureSharpLinkException(() =>
            ProtocolV2Negotiator.NegotiateServer(unsupported, policy));

        Ensure(firstFailure.Code == secondFailure.Code && firstFailure.Message == secondFailure.Message,
            "the same invalid offer and policy must produce the same structured error");
    }

    private static ProtocolV2HandshakeRequest CreateOffer(
        ProtocolV2Capabilities supported,
        ProtocolV2Capabilities required,
        IReadOnlyList<SharpLinkCompressionProviderBinding> providers)
        => new(
            ProtocolV2Constants.MinorVersion,
            supported,
            required,
            8192,
            2048,
            4096,
            ReadOnlyMemory<byte>.Empty,
            providers.Select(static binding => binding.WireProfile).ToArray());

    private static ProtocolV2NegotiationPolicy CreatePolicy(
        ProtocolV2Capabilities supported,
        IReadOnlyList<SharpLinkCompressionProviderBinding>? providers = null,
        ushort minorVersion = ProtocolV2Constants.MinorVersion,
        int maxFramePayloadBytes = 8192,
        int streamReceiveWindowBytes = 2048,
        int connectionReceiveWindowBytes = 4096)
        => ProtocolV2NegotiationPolicy.Create(
            minorVersion,
            supported,
            maxFramePayloadBytes,
            streamReceiveWindowBytes,
            connectionReceiveWindowBytes,
            providers ?? Array.Empty<SharpLinkCompressionProviderBinding>());

    private static SharpLinkCompressionProviderBinding[] Bindings(params string[] profiles)
        => profiles.Select(static profile => new SharpLinkCompressionProviderBinding(
            profile,
            new TestCompressionProvider(profile))).ToArray();

    private static SharpLinkException CaptureSharpLinkException(Action action)
    {
        try
        {
            action();
            throw new Exception("the negotiation should throw a SharpLinkException");
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
    }

    private static ArgumentException CaptureArgumentException(Action action)
    {
        try
        {
            action();
            throw new Exception("the policy construction should throw an ArgumentException");
        }
        catch (ArgumentException exception)
        {
            return exception;
        }
    }

    private sealed class TestCompressionProvider(string wireProfile) : ISharpLinkCompressionProvider
    {
        public string WireProfile { get; } = wireProfile;

        public SharpLinkCompressionResult Compress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public SharpLinkCompressionResult Decompress(
            ReadOnlySequence<byte> input,
            IBufferWriter<byte> output,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
