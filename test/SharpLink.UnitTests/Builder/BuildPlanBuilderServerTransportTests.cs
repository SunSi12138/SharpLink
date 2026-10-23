using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Threading;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.UnitTests.Builder;

public sealed partial class BuildPlanBuilderTests
{
    [Test]
    public async Task ServerBuilderShouldStayConsumedAfterSuccessAndFailure()
    {
        // Issue #805: public Fluent builders are not third-party subclassing SPIs.
        // A sealed CLR type also forbids compilation of derived classes (CS0509).
        if (!typeof(SharpClientBuilder).IsPublic || !typeof(SharpClientBuilder).IsSealed ||
            !typeof(SharpLinkServerBuilder).IsPublic || !typeof(SharpLinkServerBuilder).IsSealed ||
            !typeof(ISharpLinkServerBuilder).IsAssignableFrom(typeof(SharpLinkServerBuilder)))
        {
            throw new Exception(
                "Public Client/Server builders must remain configurable without allowing inheritance.");
        }

        var successfulTransport = new TrackingServerListener();
        var successfulBuilder = CreateServerBuilder().UseTransport(successfulTransport);
        await using var server = successfulBuilder.Build();

        EnsureConsumed(() => _ = successfulBuilder.Build());
        EnsureConsumed(() => successfulBuilder.UseHeartbeat(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));

        var failedTransport = new TrackingServerListener();
        var failedBuilder = CreateServerBuilder()
            .UseTransport(failedTransport)
            .RequireAuthentication();
        var failure = Capture(() => _ = failedBuilder.Build());

        Ensure(failure is InvalidOperationException &&
               failure.Message == "RequireAuthentication needs an ISharpLinkServerAuthenticator.",
            "server Compile failure must preserve the configuration error");
        Ensure(failedTransport.DisposeCount == 1,
            "server Compile failure must release its configured listener once");
        EnsureConsumed(() => _ = failedBuilder.Build());
        EnsureConsumed(() => failedBuilder.UseTransport(new TrackingServerListener()));
    }

    [Test]
    public async Task TcpDefaultsShouldBindLoopbackAndAllowSecureBuild()
    {
        var builder = CreateServerBuilder().UseTcp(0);

        var bound = builder.Transport!.LocalEndPoint as IPEndPoint;
        Ensure(bound is not null && bound.Address.Equals(IPAddress.Loopback),
            "UseTcp(port) must bind loopback by default.");

        await using var server = builder.Build();
        Ensure(server is not null, "loopback plaintext TCP must build by default.");

        // #805: the typed Server overloads remain viable without string-IP APIs.
        var typedBuilder = CreateServerBuilder().UseTcp(0, IPAddress.Loopback);
        var typedAddress = typedBuilder.Transport!.LocalEndPoint as IPEndPoint;
        Ensure(typedAddress is not null && typedAddress.Address.Equals(IPAddress.Loopback),
            "typed IPAddress TCP overload must preserve loopback binding");
        await using var typedServer = typedBuilder.Build();
        Ensure(typedServer is not null, "typed IPAddress TCP must still build");

        var tlsOptions = new SslServerAuthenticationOptions
        {
            ServerCertificateSelectionCallback = static (_, _) => null!
        };
        var typedTlsBuilder = CreateServerBuilder()
            .UseTcp(0, tlsOptions, IPAddress.Loopback, tlsHandshakeTimeout: TimeSpan.FromSeconds(2));
        var tlsAddress = typedTlsBuilder.Transport!.LocalEndPoint as IPEndPoint;
        Ensure(tlsAddress is not null && tlsAddress.Address.Equals(IPAddress.Loopback),
            "typed TLS Server TCP overload must preserve loopback binding");
        await using var typedTlsServer = typedTlsBuilder.Build();
        Ensure(typedTlsServer is not null, "typed IPAddress TLS must still build");
    }

    [Test]
    public void Ipv4MappedLoopbackShouldBeTreatedAsLoopback()
    {
        var isLoopback = typeof(SharpLinkServerBuilder).GetMethod(
            "IsLoopback",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var mappedLoopback = IPAddress.Parse("::ffff:127.0.0.1");

        Ensure((bool)isLoopback.Invoke(null, [mappedLoopback])!,
            "IPv4-mapped loopback addresses must not require network-exposure opt-ins.");
    }

    [Test]
    public async Task NonLoopbackPlaintextShouldRequireExplicitOptIn()
    {
        var failure = Capture(() => CreateServerBuilder()
            .UseTcp(0)
            .ListenOnAnyAddress()
            .Build());

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("AllowUnencrypted()", StringComparison.Ordinal),
            "non-loopback plaintext TCP must require AllowUnencrypted.");
    }

    [Test]
    public async Task NonLoopbackPlaintextShouldBuildAfterExplicitOptIn()
    {
        var unencryptedBuilder = CreateServerBuilder()
            .UseTcp(0)
            .ListenOnAnyAddress()
            .AllowUnencrypted()
            .AllowUnauthenticated();
        await using var unencryptedServer = unencryptedBuilder.Build();

        Ensure(unencryptedServer is not null,
            "AllowUnencrypted plus AllowUnauthenticated must be accepted for non-loopback plaintext TCP.");
    }

    [Test]
    public async Task NonLoopbackTlsShouldBuildWithoutLoweringEncryption()
    {
        var tlsOptions = new SslServerAuthenticationOptions
        {
            ServerCertificateSelectionCallback = static (_, _) => null!
        };

        var builder = CreateServerBuilder()
            .UseTcp(0)
            .ListenOnAnyAddress()
            .UseTls(tlsOptions)
            .AllowUnauthenticated();

        await using var server = builder.Build();
        Ensure(server is not null, "non-loopback TLS must only require authentication opt-in.");
    }

    [Test]
#pragma warning disable SYSLIB0040
    public async Task NonLoopbackTlsWithNoEncryptionShouldRequireUnencryptedOptIn()
    {
        var tlsOptions = new SslServerAuthenticationOptions
        {
            ServerCertificateSelectionCallback = static (_, _) => null!,
            EncryptionPolicy = EncryptionPolicy.NoEncryption
        };

        var failure = Capture(() => CreateServerBuilder()
            .UseTcp(0)
            .ListenOnAnyAddress()
            .UseTls(tlsOptions)
            .AllowUnauthenticated()
            .Build());

        Ensure(failure is InvalidOperationException &&
               failure.Message.Contains("AllowUnencrypted()", StringComparison.Ordinal),
            "NULL-cipher TLS must be treated as plaintext and require AllowUnencrypted.");
    }
#pragma warning restore SYSLIB0040

    [Test]
    public async Task EphemeralTcpShouldSupportChangingToAnyAddress()
    {
        var builder = CreateServerBuilder()
            .UseTcp(0)
            .ListenOnAnyAddress()
            .AllowUnencrypted()
            .AllowUnauthenticated();

        var bound = builder.Transport!.LocalEndPoint as IPEndPoint;
        Ensure(bound is not null && bound.Port != 0 && !bound.Address.Equals(IPAddress.Loopback),
            "ephemeral TCP must rebind to Any without overlapping the original loopback listener.");

        await using var server = builder.Build();
        Ensure(server is not null, "ephemeral Any-address TCP must build after explicit opt-ins.");
    }
}
