using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using PipeStreamOptions = System.IO.Pipes.PipeOptions;

namespace SharpLink.IntegrationTests;

public class SharedMemoryTransportConnectionIntegrationTests
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
    public async Task SharedMemoryUnknownHandshakeVersionShouldFailBeforeMapping()
    {
        var name = $"sv{Guid.NewGuid():N}"[..10];
        await using var listener = new SharedMemoryServerTransportListener(name);
        var accept = listener.AcceptAsync().AsTask();
        await using var pipe = new NamedPipeClientStream(
            ".",
            $"shm-{name}",
            PipeDirection.InOut,
            PipeStreamOptions.Asynchronous | PipeStreamOptions.CurrentUserOnly);
        await pipe.ConnectAsync();
        var invalidHello = new byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(invalidHello, 0x53484D31);
        BinaryPrimitives.WriteInt32LittleEndian(invalidHello.AsSpan(4), 999);
        BinaryPrimitives.WriteInt32LittleEndian(invalidHello.AsSpan(8), 64 * 1024);
        await pipe.WriteAsync(invalidHello);
        await pipe.FlushAsync();
        try
        {
            await accept;
            throw new Exception("expected unsupported shared-memory handshake version");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.FailedPrecondition, "unsupported handshake error code");
        }
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
    public async Task SharedMemoryRawDuplexShouldPreserveOneMillionRecordsAcrossWraps()
    {
        const int recordCount = 1_000_000;
        var name = $"sharplink-shm-raw-{Guid.NewGuid():N}";
        var options = new SharedMemoryTransportOptions
        {
            CapacityPerDirectionBytes = 64 * 1024,
            SpinCount = 0,
            HandshakeTimeout = TimeSpan.FromSeconds(5)
        };
        await using var listener = new SharedMemoryServerTransportListener(name, options);
        await using var factory = new SharedMemoryClientTransportFactory(name, options);

        var accept = listener.AcceptAsync().AsTask();
        await using var client = await factory.ConnectAsync();
        await using var server = await accept;

        var clientToServer = TransferRecordsAsync(client.Output, server.Input, recordCount, 0x13579BDF);
        var serverToClient = TransferRecordsAsync(server.Output, client.Input, recordCount, 0x2468ACE0);
        await Task.WhenAll(clientToServer, serverToClient).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task SharedMemoryCancelPendingReadShouldNotPoisonTheNextRead()
    {
        var (listener, factory, client, server) = await CreateRawPairAsync();
        await using var listenerScope = listener;
        await using var factoryScope = factory;
        await using var clientScope = client;
        await using var serverScope = server;

        client.Input.CancelPendingRead();
        var canceled = await client.Input.ReadAsync();
        Ensure(canceled.IsCanceled, "shared-memory pending read cancellation");
        client.Input.AdvanceTo(canceled.Buffer.Start);

        var memory = server.Output.GetMemory(1);
        memory.Span[0] = 0x5A;
        server.Output.Advance(1);
        var flush = await server.Output.FlushAsync();
        Ensure(!flush.IsCanceled && !flush.IsCompleted, "shared-memory writer after read cancellation");

        var resumed = await client.Input.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(resumed.Buffer.Length == 1 && resumed.Buffer.FirstSpan[0] == 0x5A,
            "shared-memory read resumed after cancellation");
        client.Input.AdvanceTo(resumed.Buffer.End);
    }

    [Test]
    public async Task SharedMemoryFullRingFlushCancellationShouldResumeWithoutLostWakeup()
    {
        const int capacity = 64 * 1024;
        var (listener, factory, client, server) = await CreateRawPairAsync();
        await using var listenerScope = listener;
        await using var factoryScope = factory;
        await using var clientScope = client;
        await using var serverScope = server;

        var ring = client.Output.GetMemory(capacity);
        ring.Span[..capacity].Fill(0x41);
        client.Output.Advance(capacity);
        var initialFlush = await client.Output.FlushAsync();
        Ensure(!initialFlush.IsCanceled && !initialFlush.IsCompleted, "shared-memory full-ring initial flush");

        var spill = client.Output.GetMemory(1);
        spill.Span[0] = 0x7E;
        client.Output.Advance(1);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try
            {
                await client.Output.FlushAsync(cancellation.Token);
                throw new Exception("expected full shared-memory ring flush cancellation");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        var fullRead = await server.Input.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(fullRead.Buffer.Length == capacity, "shared-memory full ring backpressure payload");
        server.Input.AdvanceTo(fullRead.Buffer.End);

        var resumedFlush = await client.Output.FlushAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!resumedFlush.IsCanceled && !resumedFlush.IsCompleted,
            "shared-memory flush resumed after cancellation");
        var spillRead = await server.Input.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(spillRead.Buffer.Length == 1 && spillRead.Buffer.FirstSpan[0] == 0x7E,
            "shared-memory spill preserved after cancellation");
        server.Input.AdvanceTo(spillRead.Buffer.End);
    }

    [Test]
    public async Task SharedMemoryOversizedSpillRequestShouldFailWithoutAllocation()
    {
        var (listener, factory, client, server) = await CreateRawPairAsync();
        await using var listenerScope = listener;
        await using var factoryScope = factory;
        await using var clientScope = client;
        await using var serverScope = server;

        try
        {
            _ = client.Output.GetMemory((256 * 1024 * 1024) + 1);
            throw new Exception("expected oversized shared-memory spill rejection");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.ResourceExhausted,
                "shared-memory oversized spill error code");
        }
    }

    [Test]
    public async Task SharedMemoryConnectionDisposeShouldBeIdempotent()
    {
        var (listener, factory, client, server) = await CreateRawPairAsync();
        await using var listenerScope = listener;
        await using var factoryScope = factory;
        await using var serverScope = server;

        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Test]
    public async Task SharedMemoryConcurrentCloseShouldBeIdempotentOnBothSides()
    {
        var (listener, factory, client, server) = await CreateRawPairAsync();
        await using var listenerScope = listener;
        await using var factoryScope = factory;

        var closes = new List<Task>(32);
        for (var index = 0; index < 16; index++)
        {
            closes.Add(client.DisposeAsync().AsTask());
            closes.Add(server.DisposeAsync().AsTask());
        }
        await Task.WhenAll(closes).WaitAsync(TimeSpan.FromSeconds(5));
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
        BinaryPrimitives.WriteInt32LittleEndian(hello.AsSpan(4), 1);
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
        BinaryPrimitives.WriteInt32LittleEndian(invalidAck.AsSpan(4), 1);
        RandomNumberGenerator.Fill(invalidAck.AsSpan(8));
        await pipe.WriteAsync(invalidAck);
        await pipe.FlushAsync();

        try
        {
            await accept;
            throw new Exception("expected shared-memory acknowledgement nonce rejection");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.FailedPrecondition,
                "shared-memory acknowledgement nonce error code");
        }

        await WaitUntilAsync(() => !File.Exists(mappingPath), TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task SharedMemoryAuthenticationShouldStayIsolatedAcrossMultipleClients()
    {
        var name = $"sharplink-shm-auth-{Guid.NewGuid():N}";
        using var serverCts = new CancellationTokenSource();
        var server = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseSharedMemory(name)
            .UseSerializer(MemoryPackCodec.Resolver)
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

    [Test]
    public async Task SharedMemoryHeartbeatShouldKeepAnIdleConnectionReady()
    {
        await using var harness = await SharedMemoryHarness.CreateAsync();
        await Task.Delay(TimeSpan.FromSeconds(2));

        Ensure(harness.Client.State == SharpLinkConnectionState.Ready,
            "shared-memory idle heartbeat connection state");
        Ensure(await harness.Client.Get<IConnectionBehaviorService>().PingAsync(50) == 51,
            "shared-memory ping after idle heartbeat interval");
    }

    [Test]
    public async Task SharedMemoryConnectAndBasicRpcShouldWork()
    {
        await using var harness = await SharedMemoryHarness.CreateAsync();
        var service = harness.Client.Get<IConnectionBehaviorService>();

        Ensure(await service.PingAsync(41) == 42, "shared-memory ping");
    }

    [Test]
    public async Task SharedMemoryPayloadLargerThanRingShouldRoundTrip()
    {
        await using var harness = await SharedMemoryHarness.CreateAsync();
        var service = harness.Client.Get<IConnectionBehaviorService>();
        var payload = new string('x', 256 * 1024);

        var response = await service.EchoAsync(payload);

        Ensure(response == payload, "shared-memory wrapped payload");
    }

    [Test]
    public async Task SharedMemoryServerUnexpectedDisconnectShouldFailFastPendingCall()
    {
        await using var harness = await SharedMemoryHarness.CreateAsync();
        var service = harness.Client.Get<IConnectionBehaviorService>();
        Ensure(await service.PingAsync(1) == 2, "shared-memory warmup ping");

        var pending = service.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeServerOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "shared-memory pending after server dispose");
    }

    [Test]
    public async Task SharedMemoryClientShouldReconnectAfterServerRestart()
    {
        var name = $"sharplink-shm-int-{Guid.NewGuid():N}";
        await using (var first = await SharedMemoryHarness.CreateAsync(name))
        {
            var service = first.Client.Get<IConnectionBehaviorService>();
            Ensure(await service.PingAsync(2) == 3, "first shared-memory ping");
        }

        await using var second = await SharedMemoryHarness.CreateAsync(name);
        var secondService = second.Client.Get<IConnectionBehaviorService>();
        Ensure(await secondService.PingAsync(3) == 4, "second shared-memory ping");
    }

    private static async Task EnsureThrowsSharpLinkFast(Task task, string name)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception($"assert failed: {name} should throw");
        }
        catch (TimeoutException)
        {
            throw new Exception($"assert failed: {name} did not fail fast");
        }
        catch (SharpLinkException ex)
        {
            Ensure(ex.Code == SharpLinkErrorCode.ConnectionClosed, $"{name} error code");
        }
    }

    private static async Task TransferRecordsAsync(
        PipeWriter writer,
        PipeReader reader,
        int recordCount,
        int salt)
    {
        var writeTask = Task.Run(async () =>
        {
            for (var index = 0; index < recordCount; index++)
            {
                var span = writer.GetSpan(16);
                BinaryPrimitives.WriteInt64LittleEndian(span, index);
                BinaryPrimitives.WriteInt64LittleEndian(span[8..], Checksum(index, salt));
                writer.Advance(16);
                if ((index & 0xFF) == 0xFF)
                {
                    var flush = await writer.FlushAsync();
                    Ensure(!flush.IsCanceled && !flush.IsCompleted, "raw shared-memory writer remained connected");
                }
            }
            await writer.CompleteAsync();
        });

        long expected = 0;
        while (true)
        {
            var read = await reader.ReadAsync();
            var buffer = read.Buffer;
            var sequenceReader = new SequenceReader<byte>(buffer);
            while (sequenceReader.Remaining >= 16)
            {
                Ensure(sequenceReader.TryReadLittleEndian(out long sequence), "raw sequence available");
                Ensure(sequenceReader.TryReadLittleEndian(out long checksum), "raw checksum available");
                Ensure(sequence == expected, $"raw shared-memory sequence {expected}");
                Ensure(checksum == Checksum(expected, salt), $"raw shared-memory checksum {expected}");
                expected++;
            }
            reader.AdvanceTo(sequenceReader.Position, buffer.End);
            if (read.IsCompleted)
                break;
        }

        await writeTask;
        Ensure(expected == recordCount, "raw shared-memory record count");
        await reader.CompleteAsync();
    }

    private static async Task DrainUntilCompletedAsync(PipeReader reader, string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var read = await reader.ReadAsync(timeout.Token);
            reader.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                return;
            Ensure(!read.IsCanceled, $"shared-memory {scenario} read was not canceled");
        }
    }

    private static async Task<(
        SharedMemoryServerTransportListener Listener,
        SharedMemoryClientTransportFactory Factory,
        ITransportConnection Client,
        ITransportConnection Server)> CreateRawPairAsync()
    {
        var name = $"sharplink-shm-raw-{Guid.NewGuid():N}";
        var options = new SharedMemoryTransportOptions
        {
            CapacityPerDirectionBytes = 64 * 1024,
            SpinCount = 0,
            HandshakeTimeout = TimeSpan.FromSeconds(5)
        };
        var listener = new SharedMemoryServerTransportListener(name, options);
        var factory = new SharedMemoryClientTransportFactory(name, options);
        try
        {
            var accept = listener.AcceptAsync().AsTask();
            var client = await factory.ConnectAsync();
            var server = await accept;
            return (listener, factory, client, server);
        }
        catch
        {
            await factory.DisposeAsync();
            await listener.DisposeAsync();
            throw;
        }
    }

    private static ISharpLinkClient CreateAuthenticatedSharedMemoryClient(string name, string token)
    {
        var payload = Encoding.UTF8.GetBytes(token);
        return SharpClientBuilder.Create()
            .UseSharedMemory(name)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(SharpLinkAuthenticator.CreateClient(
                _ => ValueTask.FromResult<ReadOnlyMemory<byte>>(payload)))
            .Build();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    private static long Checksum(long sequence, int salt)
        => unchecked((sequence * -7046029254386353131L) ^ salt);

    private static Process StartAotSmokeProcess(string assemblyPath, string name, string role)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add("sharedmemory");
        start.ArgumentList.Add("--role");
        start.ArgumentList.Add(role);
        start.ArgumentList.Add("--shm-name");
        start.ArgumentList.Add(name);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the AOT smoke child process.");
    }

    private static string FindAotSmokeAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("Could not locate the SharpLink repository root.");
        var path = Path.Combine(
            directory.FullName,
            "test",
            "SharpLink.AotSmoke",
            "bin",
            "Release",
            "net10.0",
            "SharpLink.AotSmoke.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException("The AOT smoke child assembly was not built.", path);
        return path;
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private sealed class SharedMemoryHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private bool _serverDisposed;
        private bool _clientDisposed;

        public ISharpLinkClient Client { get; }

        private SharedMemoryHarness(
            ISharpLinkServer server,
            Task serverTask,
            CancellationTokenSource serverCts,
            ISharpLinkClient client)
        {
            _server = server;
            _serverTask = serverTask;
            _serverCts = serverCts;
            Client = client;
        }

        public static Task<SharedMemoryHarness> CreateAsync()
            => CreateAsync($"sharplink-shm-int-{Guid.NewGuid():N}");

        public static async Task<SharedMemoryHarness> CreateAsync(string name)
        {
            var cts = new CancellationTokenSource();
            var server = SharpLinkServerBuilder.Create()
                .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
                .UseSharedMemory(name, options =>
                {
                    options.CapacityPerDirectionBytes = 64 * 1024;
                    options.HandshakeTimeout = TimeSpan.FromSeconds(5);
                })
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
                .Build();
            var client = SharpClientBuilder.Create()
                .UseSharedMemory(name, options =>
                {
                    options.CapacityPerDirectionBytes = 64 * 1024;
                    options.HandshakeTimeout = TimeSpan.FromSeconds(5);
                })
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
                .Build();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunAsync(cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
                {
                    _ = ex.HResult;
                }
            }, CancellationToken.None);

            await client.ConnectAsync(cts.Token);
            return new SharedMemoryHarness(server, serverTask, cts, client);
        }

        public async ValueTask DisposeServerOnlyAsync()
        {
            if (_serverDisposed)
                return;
            _serverDisposed = true;
            try
            {
                await _server.StopAsync(TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                _ = ex.HResult;
            }
        }

        public async ValueTask DisposeClientOnlyAsync()
        {
            if (_clientDisposed)
                return;
            _clientDisposed = true;
            try
            {
                await Client.StopAsync();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                _ = ex.HResult;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeClientOnlyAsync();
            try
            {
                await _serverCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
            await DisposeServerOnlyAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }
}
