using SharpLink.Sdk;
using System.Threading;

namespace SharpLink.UnitTests.Client;

public class SharpLinkClientExtensionsTests
{
    [Test]
    public async Task ConnectOrThrowAsyncShouldReturnWhenConnectSucceeds()
    {
        var client = new FakeSharpLinkClient(connected: true);
        await client.ConnectOrThrowAsync();
    }

    [Test]
    public async Task ConnectOrThrowAsyncShouldRethrowLastConnectionException()
    {
        var expected = new SharpLinkException(SharpLinkErrorCode.AuthenticationRejected, "token rejected");
        var client = new FakeSharpLinkClient(connected: false, lastConnectionException: expected);

        try
        {
            await client.ConnectOrThrowAsync();
            throw new Exception("expected ConnectOrThrowAsync to throw");
        }
        catch (SharpLinkException ex)
        {
            Ensure(ReferenceEquals(expected, ex), "should rethrow the captured exception");
        }
    }

    [Test]
    public async Task ConnectOrThrowAsyncShouldThrowDefaultAuthenticationRejectedException()
    {
        var client = new FakeSharpLinkClient(connected: false);

        try
        {
            await client.ConnectOrThrowAsync();
            throw new Exception("expected ConnectOrThrowAsync to throw");
        }
        catch (SharpLinkException ex)
        {
            Ensure(ex.Code == SharpLinkErrorCode.AuthenticationRejected, "error code");
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FakeSharpLinkClient(bool connected, Exception? lastConnectionException = null)
        : ISharpLinkClient, ISharpLinkClientDiagnostics
    {
        public Exception? LastConnectionException { get; } = lastConnectionException;

        public Task<bool> ConnectAsync(CancellationToken ct = default) => Task.FromResult(connected);

        public T Get<T>() where T : IService
            => throw new NotSupportedException();
    }
}
