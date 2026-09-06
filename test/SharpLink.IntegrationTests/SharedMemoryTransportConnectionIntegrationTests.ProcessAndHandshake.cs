using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using PipeStreamOptions = System.IO.Pipes.PipeOptions;

namespace SharpLink.IntegrationTests;

public partial class SharedMemoryTransportConnectionIntegrationTests
{
    [Test]
    public async Task SharedMemoryShouldWorkAcrossIndependentProcesses()
    {
        var name = $"sp{Guid.NewGuid():N}";
        var executable = FindAotSmokeAssembly();
        using var server = StartAotSmokeProcess(executable, name, "server");
        try
        {
            var ready = await server.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(ready == "AOT_SMOKE_SERVER_READY", $"child server ready: {ready}");

            using var client = StartAotSmokeProcess(executable, name, "client");
            var clientOutput = await client.StandardOutput.ReadToEndAsync();
            var clientError = await client.StandardError.ReadToEndAsync();
            await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Ensure(client.ExitCode == 0, $"child client exit: {clientOutput} {clientError}");
            Ensure(clientOutput.Contains("REFERENCED_SERVICE_PASS", StringComparison.Ordinal),
                "referenced internal service result");
            Ensure(clientOutput.Contains("AOT_SMOKE_CLIENT_PASS", StringComparison.Ordinal), "child client result");

            var remainingServerOutput = await server.StandardOutput.ReadToEndAsync();
            var serverError = await server.StandardError.ReadToEndAsync();
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Ensure(server.ExitCode == 0, $"child server exit: {remainingServerOutput} {serverError}");
            Ensure(remainingServerOutput.Contains("AOT_SMOKE_SERVER_PASS", StringComparison.Ordinal), "child server result");
        }
        finally
        {
            if (!server.HasExited)
                server.Kill(entireProcessTree: true);
        }
    }

    [Test]
    public async Task SharedMemoryServerProcessKillShouldCloseControlChannelAndAllowRestart()
    {
        var name = $"sp{Guid.NewGuid():N}";
        var executable = FindAotSmokeAssembly();
        using var serverProcess = StartAotSmokeProcess(executable, name, "server");
        var ready = await serverProcess.StandardOutput.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        Ensure(ready == "AOT_SMOKE_SERVER_READY", $"killed child server ready: {ready}");

        await using var factory = new SharedMemoryClientTransportFactory(name);
        await using var connection = await factory.ConnectAsync();
        serverProcess.Kill(entireProcessTree: true);
        await serverProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await DrainUntilCompletedAsync(connection.Input, "server process kill");

        var restartName = name;
        var options = new SharedMemoryTransportOptions { CapacityPerDirectionBytes = 64 * 1024 };
        await using var listener = new SharedMemoryServerTransportListener(restartName, options);
        await using var restartFactory = new SharedMemoryClientTransportFactory(restartName, options);
        var accept = listener.AcceptAsync().AsTask();
        await using var restartedClient = await restartFactory.ConnectAsync();
        await using var restartedServer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task SharedMemoryClientProcessKillShouldCloseControlChannel()
    {
        var name = $"cp{Guid.NewGuid():N}";
        var executable = FindAotSmokeAssembly();
        await using var listener = new SharedMemoryServerTransportListener(name);
        var accept = listener.AcceptAsync().AsTask();
        using var clientProcess = StartAotSmokeProcess(executable, name, "client");
        await using var connection = await accept.WaitAsync(TimeSpan.FromSeconds(10));

        clientProcess.Kill(entireProcessTree: true);
        await clientProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await DrainUntilCompletedAsync(connection.Input, "client process kill");
    }

    [Test]
    public async Task SharedMemoryCapacityMismatchShouldNegotiateSmallerRing()
    {
        var name = $"sharplink-shm-capacity-{Guid.NewGuid():N}";
        await using var listener = new SharedMemoryServerTransportListener(name, new SharedMemoryTransportOptions
        {
            CapacityPerDirectionBytes = 4 * 1024 * 1024
        });
        await using var factory = new SharedMemoryClientTransportFactory(name, new SharedMemoryTransportOptions
        {
            CapacityPerDirectionBytes = 1024 * 1024
        });

        var accept = listener.AcceptAsync().AsTask();
        await using var client = await factory.ConnectAsync();
        await using var server = await accept;
        var clientMemory = client.Output.GetMemory(1);
        var serverMemory = server.Output.GetMemory(1);
        Ensure(clientMemory.Length == 1024 * 1024, "client negotiated smaller shared-memory capacity");
        Ensure(serverMemory.Length == 1024 * 1024, "server negotiated smaller shared-memory capacity");
        client.Output.Advance(0);
        server.Output.Advance(0);
    }

    [Test]
    public async Task SharedMemoryNoListenerShouldMapTimeoutToUnavailable()
    {
        await using var factory = new SharedMemoryClientTransportFactory(
            $"sharplink-shm-missing-{Guid.NewGuid():N}",
            new SharedMemoryTransportOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(100) });
        try
        {
            await factory.ConnectAsync();
            throw new Exception("expected missing shared-memory listener failure");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "missing listener error code");
        }
    }

    [Test]
    public async Task SharedMemoryTruncatedServerResponseShouldMapToUnavailable()
    {
        var name = $"tr{Guid.NewGuid():N}"[..20];
        await using var server = new NamedPipeServerStream(
            $"shm-{name}",
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeStreamOptions.Asynchronous | PipeStreamOptions.CurrentUserOnly);
        var peer = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var hello = new byte[48];
            await server.ReadExactlyAsync(hello);
            await server.WriteAsync(new byte[] { 0x31, 0x4D, 0x48, 0x53 });
            await server.FlushAsync();
            await server.DisposeAsync();
        });

        await using var factory = new SharedMemoryClientTransportFactory(name);
        Exception? failure = null;
        try
        {
            await factory.ConnectAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        await peer.WaitAsync(TimeSpan.FromSeconds(2));

        Ensure(failure is SharpLinkException
        {
            Code: SharpLinkErrorCode.Unavailable,
            InnerException: EndOfStreamException
        }, "truncated shared-memory response error normalization");
    }

    [Test]
    public async Task SharedMemoryCallerCancellationShouldRemainOperationCanceledException()
    {
        await using var factory = new SharedMemoryClientTransportFactory(
            $"sharplink-shm-cancel-{Guid.NewGuid():N}",
            new SharedMemoryTransportOptions { HandshakeTimeout = TimeSpan.FromSeconds(5) });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await factory.ConnectAsync(cancellation.Token);
            throw new Exception("expected shared-memory connect cancellation");
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Test]
    public async Task SharedMemoryListenerShouldRejectBadHandshakesAndAcceptNextClient()
    {
        var name = $"sv{Guid.NewGuid():N}"[..10];
        var options = new SharedMemoryTransportOptions
        {
            CapacityPerDirectionBytes = 64 * 1024,
            HandshakeTimeout = TimeSpan.FromMilliseconds(100)
        };
        await using var listener = new SharedMemoryServerTransportListener(name, options);
        var accept = listener.AcceptAsync().AsTask();

        await using (var unknownVersion = CreateRawSharedMemoryPipe(name))
        {
            await unknownVersion.ConnectAsync();
            var invalidHello = new byte[48];
            BinaryPrimitives.WriteInt32LittleEndian(invalidHello, 0x53484D31);
            BinaryPrimitives.WriteInt32LittleEndian(invalidHello.AsSpan(4), 999);
            BinaryPrimitives.WriteInt32LittleEndian(invalidHello.AsSpan(8), 64 * 1024);
            await unknownVersion.WriteAsync(invalidHello);
            await unknownVersion.FlushAsync();
        }

        await using (var truncated = CreateRawSharedMemoryPipe(name))
        {
            await truncated.ConnectAsync();
            await truncated.WriteAsync(new byte[] { 0x31, 0x4D, 0x48, 0x53 });
            await truncated.FlushAsync();
        }

        await using (var idle = CreateRawSharedMemoryPipe(name))
        {
            await idle.ConnectAsync();
            await Task.Delay(200);
        }

        await using var factory = new SharedMemoryClientTransportFactory(name, options);
        await using var client = await factory.ConnectAsync();
        await using var server = await accept.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task SharedMemoryListenerIdleTimeShouldNotConsumeHandshakeTimeout()
    {
        var name = $"sharplink-shm-idle-{Guid.NewGuid():N}";
        var options = new SharedMemoryTransportOptions
        {
            CapacityPerDirectionBytes = 64 * 1024,
            HandshakeTimeout = TimeSpan.FromMilliseconds(100)
        };
        await using var listener = new SharedMemoryServerTransportListener(name, options);
        await using var factory = new SharedMemoryClientTransportFactory(name, options);

        var accept = listener.AcceptAsync().AsTask();
        await Task.Delay(300);
        await using var client = await factory.ConnectAsync();
        await using var server = await accept.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task SharedMemoryHandshakeShouldRejectMismatchedAcknowledgementNonceAndCleanMapping()
    {
        var name = $"ack{Guid.NewGuid():N}"[..20];
        await using var listener = new SharedMemoryServerTransportListener(name);
        var accept = listener.AcceptAsync().AsTask();
        await using var pipe = new NamedPipeClientStream(
            ".",
            $"shm-{name}",
            PipeDirection.InOut,
            PipeStreamOptions.Asynchronous | PipeStreamOptions.CurrentUserOnly);
        await pipe.ConnectAsync();

        var nonce = RandomNumberGenerator.GetBytes(32);
        var hello = new byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(hello, 0x53484D31);
        BinaryPrimitives.WriteInt32LittleEndian(hello.AsSpan(4), 3);
        BinaryPrimitives.WriteInt32LittleEndian(hello.AsSpan(8), 64 * 1024);
        BinaryPrimitives.WriteInt32LittleEndian(hello.AsSpan(12), Environment.ProcessId);
        nonce.CopyTo(hello, 16);
        await pipe.WriteAsync(hello);
        await pipe.FlushAsync();

        var responseHeader = new byte[52];
        await pipe.ReadExactlyAsync(responseHeader);
        var pathLength = BinaryPrimitives.ReadInt32LittleEndian(responseHeader.AsSpan(16));
        Ensure(pathLength is > 0 and <= 3072, "shared-memory handshake response path length");
        var pathBytes = new byte[pathLength];
        await pipe.ReadExactlyAsync(pathBytes);
        var mappingPath = Encoding.UTF8.GetString(pathBytes);

        var invalidAck = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(invalidAck, 0x53484D31);
        BinaryPrimitives.WriteInt32LittleEndian(invalidAck.AsSpan(4), 3);
        RandomNumberGenerator.Fill(invalidAck.AsSpan(8));
        await pipe.WriteAsync(invalidAck);
        await pipe.FlushAsync();
        await pipe.DisposeAsync();

        await WaitUntilAsync(() => !File.Exists(mappingPath), TimeSpan.FromSeconds(2));

        await using var factory = new SharedMemoryClientTransportFactory(name);
        await using var client = await factory.ConnectAsync();
        await using var server = await accept.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task SharedMemoryAuthenticationShouldStayIsolatedAcrossMultipleClients()
    {
        var name = $"sharplink-shm-auth-{Guid.NewGuid():N}";
        using var serverCts = new CancellationTokenSource();
        var server = SharpLinkServerBuilder.Create()
            .UseSharedMemory(name)

            .UseAuthenticator(SharpLinkAuthenticator.CreateServer((request, _) =>
            {
                var token = Encoding.UTF8.GetString(request.Payload.Span);
                return ValueTask.FromResult(token is "connection-a" or "connection-b"
                    ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext(
                        subject: token,
                        claims: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["role"] = token
                        }))
                    : SharpLinkAuthenticationResult.Reject());
            }))
            .RequireAuthentication()
            .Build();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(serverCts.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or
                                              ObjectDisposedException or IOException or SocketException)
            {
                _ = exception.HResult;
            }
        }, CancellationToken.None);

        var firstClient = CreateAuthenticatedSharedMemoryClient(name, "connection-a");
        var secondClient = CreateAuthenticatedSharedMemoryClient(name, "connection-b");
        var rejectedClient = CreateAuthenticatedSharedMemoryClient(name, "rejected");
        try
        {
            await Task.WhenAll(
                firstClient.ConnectAsync(serverCts.Token).AsTask(),
                secondClient.ConnectAsync(serverCts.Token).AsTask());
            var firstService = firstClient.Get<IConnectionBehaviorService>();
            var secondService = secondClient.Get<IConnectionBehaviorService>();
            var calls = new Task<string>[200];
            for (var index = 0; index < calls.Length; index += 2)
            {
                calls[index] = firstService.GetAuthenticationSummaryAsync().AsTask();
                calls[index + 1] = secondService.GetAuthenticationSummaryAsync().AsTask();
            }
            await Task.WhenAll(calls);
            for (var index = 0; index < calls.Length; index += 2)
            {
                Ensure(calls[index].Result == "connection-a|connection-a",
                    "shared-memory first authentication context isolation");
                Ensure(calls[index + 1].Result == "connection-b|connection-b",
                    "shared-memory second authentication context isolation");
            }

            try
            {
                await rejectedClient.ConnectAsync(serverCts.Token);
                throw new Exception("expected shared-memory authentication rejection");
            }
            catch (SharpLinkException exception)
            {
                Ensure(exception.Code == SharpLinkErrorCode.AuthenticationRejected,
                    "shared-memory authentication rejection code");
            }
        }
        finally
        {
            await firstClient.DisposeAsync();
            await secondClient.DisposeAsync();
            await rejectedClient.DisposeAsync();
            await serverCts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }
}
