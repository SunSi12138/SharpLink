using System.Buffers.Binary;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public class TransportValidationTests
{
    [Test]
    public async Task UnixNamedPipeNormalizationShouldRespectUtf8PathBytes()
    {
        var logicalName = new string('界', 30);
        var normalized = NamedPipeName.Normalize(logicalName);

        if (OperatingSystem.IsWindows())
        {
            await Assert.That(normalized).IsEqualTo(logicalName);
            return;
        }

        var tempPath = Path.GetTempPath().TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var nativePath = Path.Combine(tempPath, $"CoreFxPipe_{normalized}");

        await Assert.That(Encoding.UTF8.GetByteCount(nativePath)).IsLessThanOrEqualTo(103);

        var emojiName = string.Concat(Enumerable.Repeat("\U0001F680", 30));
        var normalizedEmoji = NamedPipeName.Normalize(emojiName);
        await Assert.That(HasUnpairedSurrogate(normalizedEmoji)).IsFalse();
        await Assert.That(NamedPipeName.Normalize(emojiName)).IsEqualTo(normalizedEmoji);
    }

    [Test]
    [Arguments(-2)]
    [Arguments(0)]
    [Arguments(255)]
    public async Task NamedPipeListenerShouldRejectInvalidServerInstanceLimits(int maxServerInstances)
    {
        await Assert.That(() => new NamedPipeServerTransportListener(
                $"np{Guid.NewGuid():N}",
                maxServerInstances))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task NamedPipeListenerShouldAcceptDocumentedServerInstanceLimits()
    {
        await using var unlimited = new NamedPipeServerTransportListener(
            $"np{Guid.NewGuid():N}",
            NamedPipeServerStream.MaxAllowedServerInstances);
        await using var maximum = new NamedPipeServerTransportListener(
            $"np{Guid.NewGuid():N}",
            254);

        await Assert.That(unlimited).IsNotNull();
        await Assert.That(maximum).IsNotNull();
    }

    [Test]
    public async Task NamedPipeTransportsShouldRejectUndefinedEnumsDuringConfiguration()
    {
        var allDefinedOptions = System.IO.Pipes.PipeOptions.None;
        foreach (var option in Enum.GetValues<System.IO.Pipes.PipeOptions>())
        {
            NamedPipeTransportValidation.ValidateServerOptions(option);
            if (option != System.IO.Pipes.PipeOptions.FirstPipeInstance)
                NamedPipeTransportValidation.ValidateClientOptions(option);
            allDefinedOptions |= option;
        }
        NamedPipeTransportValidation.ValidateServerOptions(allDefinedOptions);
        foreach (var mode in Enum.GetValues<PipeTransmissionMode>())
            NamedPipeTransportValidation.ValidateTransmissionMode(mode);

        const System.IO.Pipes.PipeOptions undefinedOptions =
            (System.IO.Pipes.PipeOptions)0x1000_0000;
        const PipeTransmissionMode undefinedMode = (PipeTransmissionMode)42;
        var clientFailure = CaptureFailure(() =>
            _ = new NamedPipeClientTransportFactory("pipe", ".", undefinedOptions));
        var clientServerOnlyFailure = CaptureFailure(() =>
            _ = new NamedPipeClientTransportFactory(
                "pipe",
                ".",
                System.IO.Pipes.PipeOptions.FirstPipeInstance));
        var serverOptionsFailure = CaptureFailure(() =>
            _ = new NamedPipeServerTransportListener(
                "pipe",
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                undefinedOptions));
        var serverModeFailure = CaptureFailure(() =>
            _ = new NamedPipeServerTransportListener(
                "pipe",
                NamedPipeServerStream.MaxAllowedServerInstances,
                undefinedMode));

        await Assert.That(clientFailure).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(clientServerOnlyFailure).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(serverOptionsFailure).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(serverModeFailure).IsTypeOf<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SocketClientShouldRejectTheServerOnlyEphemeralPort()
    {
        await Assert.That(() => new SocketClientTransportFactory(
                new IPEndPoint(IPAddress.Loopback, 0)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SocketClientTransportFactory(
                new DnsEndPoint("localhost", 0)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SharpClientBuilder.Create().UseTcp("127.0.0.1", 0))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => SharpClientBuilder.Create().UseTcp(
                "127.0.0.1",
                0,
                new SslClientAuthenticationOptions()))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task HandshakeTimeoutsBeyondThePortableTimerRangeShouldFailDuringConfiguration()
    {
        var protocolFailure = CaptureFailure(() => new SharpLinkProtocolOptions
        {
            HandshakeTimeout = TimeSpan.MaxValue
        }.Validate());
        var tlsFailure = CaptureFailure(() =>
            _ = TlsAuthenticationOptionsSnapshot.ValidateTimeout(TimeSpan.MaxValue));
        var sharedMemoryFailure = CaptureFailure(() => new SharedMemoryTransportOptions
        {
            HandshakeTimeout = TimeSpan.MaxValue
        }.Validate());

        await Assert.That(protocolFailure).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(tlsFailure).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(sharedMemoryFailure).IsTypeOf<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SocketKeepAliveDurationsBeyondNativeRangeShouldFailDuringConfiguration()
    {
        var maximum = TimeSpan.FromSeconds(int.MaxValue);
        var accepted = new SocketTransportOptions
        {
            KeepAliveTime = maximum,
            KeepAliveInterval = maximum
        }.CloneValidated();
        var timeFailure = CaptureFailure(() => new SocketTransportOptions
        {
            KeepAliveTime = maximum.Add(TimeSpan.FromTicks(1))
        }.CloneValidated());
        var intervalFailure = CaptureFailure(() => new SocketTransportOptions
        {
            KeepAliveInterval = maximum.Add(TimeSpan.FromTicks(1))
        }.CloneValidated());

        await Assert.That(accepted.KeepAliveTime).IsEqualTo(maximum);
        await Assert.That(accepted.KeepAliveInterval).IsEqualTo(maximum);
        await Assert.That(timeFailure).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(intervalFailure).IsTypeOf<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SharedMemoryServerResponseShouldRejectMalformedPathUtf8()
    {
        var name = $"su{Guid.NewGuid():N}"[..10];
        await using var server = new NamedPipeServerStream(
            name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".",
            name,
            PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());

        var nonce = new byte[SharedMemoryLayout.NonceBytes];
        Random.Shared.NextBytes(nonce);
        var response = new byte[20 + nonce.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(response, 0x53484D31);
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(4), 3);
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(8), 64 * 1024);
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(12), Environment.ProcessId);
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(16), 2);
        nonce.CopyTo(response, 20);
        response[^2] = 0xC3;
        response[^1] = 0x28;

        var read = SharedMemoryHandshake.ReadServerResponseAsync(client, nonce, CancellationToken.None).AsTask();
        await server.WriteAsync(response);
        var failure = await CaptureFailureAsync(read);

        await Assert.That(failure).IsTypeOf<SharpLinkException>();
        var sharpLinkFailure = (SharpLinkException)failure!;
        await Assert.That(sharpLinkFailure.Code).IsEqualTo(SharpLinkErrorCode.FailedPrecondition);
        await Assert.That(sharpLinkFailure.Message).Contains("UTF-8");
    }

    [Test]
    public async Task UnixSocketListenerShouldNotDeleteAPreExistingFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = $"/tmp/sl-{Guid.NewGuid():N}.sock";
        await File.WriteAllTextAsync(path, "caller-owned");
        SocketServerTransportListener? listener = null;
        Exception? failure = null;
        try
        {
            try
            {
                listener = new SocketServerTransportListener(new UnixDomainSocketEndPoint(path));
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            await Assert.That(failure).IsTypeOf<IOException>();
            await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("caller-owned");
        }
        finally
        {
            if (listener is not null)
                await listener.DisposeAsync();
            File.Delete(path);
        }
    }

    [Test]
    public async Task SocketClientFactoryShouldSnapshotAMutableIpEndPoint()
    {
        await using var listener = new SocketServerTransportListener(
            new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        var supplied = new IPEndPoint(IPAddress.Loopback, port);
        await using var factory = new SocketClientTransportFactory(supplied);
        supplied.Port = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepting = listener.AcceptAsync(timeout.Token);

        await using var client = await factory.ConnectAsync(timeout.Token);
        await using var server = await accepting;

        await Assert.That(((IPEndPoint)client.RemoteEndPoint!).Port).IsEqualTo(port);
    }

    [Test]
    public async Task TlsSnapshotsShouldPreserveAndIsolateChainPolicy()
    {
        var clientPolicy = new X509ChainPolicy
        {
            RevocationMode = X509RevocationMode.NoCheck,
            VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority
        };
        var clientSource = new SslClientAuthenticationOptions
        {
            TargetHost = "client.example",
            CertificateChainPolicy = clientPolicy
        };
        var clientSnapshot = TlsAuthenticationOptionsSnapshot.Clone(clientSource)!;

        var serverPolicy = new X509ChainPolicy
        {
            RevocationMode = X509RevocationMode.Offline,
            VerificationFlags = X509VerificationFlags.IgnoreWrongUsage
        };
        var serverSource = new SslServerAuthenticationOptions
        {
            ServerCertificateSelectionCallback = static (_, _) => null!,
            CertificateChainPolicy = serverPolicy
        };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            serverSource.AllowRsaPkcs1Padding = false;
            serverSource.AllowRsaPssPadding = false;
        }
        var serverSnapshot = TlsAuthenticationOptionsSnapshot.Clone(serverSource)!;

        clientPolicy.RevocationMode = X509RevocationMode.Online;
        serverPolicy.RevocationMode = X509RevocationMode.Online;
        await Assert.That(clientSnapshot.CertificateChainPolicy).IsNotSameReferenceAs(clientPolicy);
        await Assert.That(clientSnapshot.CertificateChainPolicy!.RevocationMode)
            .IsEqualTo(X509RevocationMode.NoCheck);
        await Assert.That(serverSnapshot.CertificateChainPolicy).IsNotSameReferenceAs(serverPolicy);
        await Assert.That(serverSnapshot.CertificateChainPolicy!.RevocationMode)
            .IsEqualTo(X509RevocationMode.Offline);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            await Assert.That(serverSnapshot.AllowRsaPkcs1Padding).IsFalse();
            await Assert.That(serverSnapshot.AllowRsaPssPadding).IsFalse();
        }
    }

    private static bool HasUnpairedSurrogate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
                continue;
            if (!char.IsHighSurrogate(value[index]) ||
                index + 1 == value.Length ||
                !char.IsLowSurrogate(value[++index]))
            {
                return true;
            }
        }
        return false;
    }

    private static Exception? CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
