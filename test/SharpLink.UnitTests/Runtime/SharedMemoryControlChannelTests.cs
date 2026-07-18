using System.IO.Pipes;

namespace SharpLink.UnitTests.Runtime;

public class SharedMemoryControlChannelTests
{
    [Test]
    public async Task UnknownControlSignalShouldSurfaceProtocolViolation()
    {
        var pipeName = $"sc{Guid.NewGuid():N}"[..20];
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await accept;
        await using var control = new SharedMemoryControlChannel(server);

        await client.WriteAsync(new byte[] { 0x7F });
        await client.FlushAsync();

        try
        {
            await control.WaitForDataAsync(default);
            throw new Exception("expected unknown shared-memory control signal rejection");
        }
        catch (SharpLinkException exception)
        {
            await Assert.That(exception.Code).IsEqualTo(SharpLinkErrorCode.ProtocolViolation);
        }
    }
}
