namespace SharpLink.IntegrationTests;

public class TransportFactoryLifecycleTests
{
    [Test]
    public async Task SocketClientFactoryShouldCreateFreshSocketAfterFailedAttempt()
    {
        var port = GetFreePort();
        await using var factory = new SocketClientTransportFactory(new IPEndPoint(IPAddress.Loopback, port));
        await ExpectException<SocketException>(factory.ConnectAsync().AsTask());

        await using var listener = new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, port));
        var acceptTask = listener.AcceptAsync().AsTask();
        await using var client = await factory.ConnectAsync();
        await using var server = await acceptTask;

        await AssertDuplexByteAsync(client, server, 0x2A);
    }

    [Test]
    public async Task AcceptedSocketConnectionsShouldOwnIndependentResources()
    {
        await using var listener = new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)(listener.LocalEndPoint ?? throw new Exception("listener endpoint missing"));
        await using var factory = new SocketClientTransportFactory(endpoint);

        var firstAccept = listener.AcceptAsync().AsTask();
        var firstClient = await factory.ConnectAsync();
        var firstServer = await firstAccept;
        await firstClient.DisposeAsync();
        await firstServer.DisposeAsync();

        var secondAccept = listener.AcceptAsync().AsTask();
        await using var secondClient = await factory.ConnectAsync();
        await using var secondServer = await secondAccept;
        Ensure(firstClient.Id != secondClient.Id, "client connection ids must differ");
        Ensure(firstServer.Id != secondServer.Id, "server connection ids must differ");
        await AssertDuplexByteAsync(secondClient, secondServer, 0x5C);
    }

    [Test]
    public async Task NamedPipeListenerShouldCreateFreshServerStreamPerAccept()
    {
        var name = $"sharplink-factory-{Guid.NewGuid():N}";
        await using var listener = new NamedPipeServerTransportListener(name);
        await using var factory = new NamedPipeClientTransportFactory(name);

        var firstAccept = listener.AcceptAsync().AsTask();
        var firstClient = await factory.ConnectAsync();
        var firstServer = await firstAccept;
        var firstServerId = firstServer.Id;
        await firstClient.DisposeAsync();
        await firstServer.DisposeAsync();

        var secondAccept = listener.AcceptAsync().AsTask();
        await using var secondClient = await factory.ConnectAsync();
        await using var secondServer = await secondAccept;
        Ensure(firstServerId != secondServer.Id, "named-pipe accepts must produce independent connections");
        await AssertDuplexByteAsync(secondClient, secondServer, 0x7E);
    }

    [Test]
    public async Task UdsListenerShouldDeleteOnlyItsBoundPathOnDispose()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;

        var path = Path.Combine(Path.GetTempPath(), $"sl-own-{Guid.NewGuid():N}.sock");
        var unrelated = Path.Combine(Path.GetTempPath(), $"sl-other-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(unrelated, "keep");
        var listener = new SocketServerTransportListener(new UnixDomainSocketEndPoint(path));
        try
        {
            Ensure(File.Exists(path), "bound UDS path should exist");
        }
        finally
        {
            await listener.DisposeAsync();
        }

        try
        {
            Ensure(!File.Exists(path), "listener should delete its bound UDS path");
            Ensure(File.Exists(unrelated), "listener must not delete unrelated paths");
        }
        finally
        {
            File.Delete(unrelated);
        }
    }

    private static async Task AssertDuplexByteAsync(
        ITransportConnection sender,
        ITransportConnection receiver,
        byte value)
    {
        var memory = sender.Output.GetMemory(1);
        memory.Span[0] = value;
        sender.Output.Advance(1);
        var flush = await sender.Output.FlushAsync();
        Ensure(!flush.IsCanceled && !flush.IsCompleted, "sender flush");

        var result = await receiver.Input.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        var buffer = result.Buffer;
        try
        {
            Ensure(buffer.Length >= 1, "receiver byte available");
            Ensure(buffer.FirstSpan[0] == value, "receiver byte value");
        }
        finally
        {
            receiver.Input.AdvanceTo(buffer.GetPosition(1));
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task ExpectException<TException>(Task task) where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
