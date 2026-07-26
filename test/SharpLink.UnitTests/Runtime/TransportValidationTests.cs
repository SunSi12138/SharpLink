using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Text;
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
}
