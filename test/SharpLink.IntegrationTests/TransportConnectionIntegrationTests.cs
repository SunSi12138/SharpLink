namespace SharpLink.IntegrationTests;

public class TransportConnectionIntegrationTests
{
    [Test]
    public async Task TcpConnectAndBasicRpcShouldWork()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.Tcp);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var value = await svc.PingAsync(7);
        Ensure(value == 8, "tcp ping");
    }

    [Test]
    public async Task NamedPipeConnectAndBasicRpcShouldWork()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.NamedPipe);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var value = await svc.PingAsync(9);
        Ensure(value == 10, "namedpipe ping");
    }

    [Test]
    public async Task UdsConnectAndBasicRpcShouldWork()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;

        await using var harness = await TransportHarness.CreateAsync(TransportKind.Uds);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var value = await svc.PingAsync(11);
        Ensure(value == 12, "uds ping");
    }

    [Test]
    public async Task TcpServerUnexpectedDisconnectShouldFailFastPendingCall()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.Tcp);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        harness.DisposeServerOnly();

        await EnsureThrowsSharpLinkFast(pending, "tcp pending should fail fast after server dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task NamedPipeServerUnexpectedDisconnectShouldFailFastPendingCall()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.NamedPipe);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        harness.DisposeServerOnly();

        await EnsureThrowsSharpLinkFast(pending, "namedpipe pending should fail fast after server dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task UdsServerUnexpectedDisconnectShouldFailFastPendingCall()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;

        await using var harness = await TransportHarness.CreateAsync(TransportKind.Uds);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        harness.DisposeServerOnly();

        await EnsureThrowsSharpLinkFast(pending, "uds pending should fail fast after server dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task TcpClientDisposeShouldFailFastPendingCall()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.Tcp);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        harness.DisposeClientOnly();

        await EnsureThrowsSharpLinkFast(pending, "tcp pending should fail fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task NamedPipeClientDisposeShouldFailFastPendingCall()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.NamedPipe);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        harness.DisposeClientOnly();

        await EnsureThrowsSharpLinkFast(pending, "namedpipe pending should fail fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task UdsClientDisposeShouldFailFastPendingCall()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;

        await using var harness = await TransportHarness.CreateAsync(TransportKind.Uds);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        harness.DisposeClientOnly();

        await EnsureThrowsSharpLinkFast(pending, "uds pending should fail fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task TcpConnectWithoutServerShouldThrowSocketException()
    {
        var port = GetFreePort();
        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await EnsureThrows<SocketException>(client.ConnectAsync(), "tcp connect without server");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    [Test]
    public async Task NamedPipeConnectWithoutServerShouldHonorCancellation()
    {
        var pipeName = $"sharplink-int-no-server-{Guid.NewGuid():N}";
        var client = SharpClientBuilder.Create()
            .UseNamedPipe(pipeName)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(180));
            await EnsureThrows<OperationCanceledException>(
                client.ConnectAsync(cts.Token),
                "namedpipe connect without server");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    [Test]
    public async Task UdsConnectWithoutServerShouldThrowSocketException()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;

        var socketPath = GetUniqueUdsPath();
        var client = SharpClientBuilder.Create()
            .UseUds(socketPath)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await EnsureThrows<SocketException>(client.ConnectAsync(), "uds connect without server");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
            TryDeleteFile(socketPath);
        }
    }

    [Test]
    public async Task TcpConnectWithCanceledTokenShouldThrowOperationCanceledException()
    {
        var port = GetFreePort();
        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await EnsureThrows<OperationCanceledException>(
                client.ConnectAsync(cts.Token),
                "tcp connect with canceled token");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    [Test]
    public async Task TcpHandshakeFailureShouldReturnFalse()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var fakeServerTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            var writer = BufferWriterPool.Get();
            try
            {
                using (writer.BeginPacketScope(PacketType.Handshake, PacketFlags.IsError, 0))
                {
                    writer.WriteUtf8String("token rejected");
                }

                await stream.WriteAsync(writer.WrittenMemory);
                await stream.FlushAsync();
            }
            finally
            {
                BufferWriterPool.Return(writer);
            }
        });

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            var connected = await client.ConnectAsync();
            Ensure(!connected, "tcp handshake failure should return false");
            Ensure(
                client is ISharpLinkClientDiagnostics
                {
                    LastConnectionException: SharpLinkException
                    {
                        Code: SharpLinkErrorCode.AuthenticationRejected,
                        Message: "token rejected"
                    }
                },
                "handshake diagnostics should expose authentication rejection");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
            listener.Stop();
            await Task.WhenAny(fakeServerTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpCustomAuthenticatorShouldAcceptMatchingHandshakeMessage()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(static message => message == "expected-token")
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)((ILocalEndPointTransport)serverBuilder.Transport!).LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.Start(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator("expected-token")
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            Ensure(await client.ConnectAsync(cts.Token), "custom authenticator should connect");
            var svc = client.Get<IConnectionBehaviorService>();
            Ensure(await svc.PingAsync(12) == 13, "custom authenticator ping");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
            await cts.CancelAsync();
            (server as IDisposable)?.Dispose();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpCustomAuthenticatorShouldRejectMismatchedHandshakeMessage()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(static message => message == "expected-token")
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)((ILocalEndPointTransport)serverBuilder.Transport!).LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.Start(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator("unexpected-token")
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            Ensure(!await client.ConnectAsync(cts.Token), "custom authenticator should reject mismatched token");
            var exception = EnsureConnectionException(client, "custom authenticator diagnostics");
            Ensure(exception.Code == SharpLinkErrorCode.AuthenticationRejected, "custom authenticator diagnostics should expose authentication rejection");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
            await cts.CancelAsync();
            (server as IDisposable)?.Dispose();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpStructuredAuthenticatorShouldExposeCustomAuthenticationError()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Success
                : SharpLinkAuthenticationResult.Reject(
                    SharpLinkErrorCode.AuthenticationExpired,
                    "token expired"))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)((ILocalEndPointTransport)serverBuilder.Transport!).LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.Start(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator("expired-token")
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            Ensure(!await client.ConnectAsync(cts.Token), "structured authenticator should reject token");
            var exception = EnsureConnectionException(client, "structured authenticator diagnostics");
            Ensure(exception.Code == SharpLinkErrorCode.AuthenticationExpired, "structured authenticator code");
            Ensure(exception.Message == "token expired", "structured authenticator message");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
            await cts.CancelAsync();
            (server as IDisposable)?.Dispose();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpStructuredAuthenticatorShouldExposeAuthenticationContextToService()
    {
        var expiresAt = new DateTimeOffset(2026, 4, 19, 12, 34, 56, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Authenticate(
                    new SharpLinkAuthenticationContext(
                        subject: "user-42",
                        tenantId: "tenant-a",
                        scopes: ["rpc.read", "rpc.write"],
                        expiresAt: new DateTimeOffset(2026, 4, 19, 12, 34, 56, TimeSpan.Zero),
                        claims: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["role"] = "admin"
                        }))
                : SharpLinkAuthenticationResult.Reject())
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)((ILocalEndPointTransport)serverBuilder.Transport!).LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.Start(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator("expected-token")
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            Ensure(await client.ConnectAsync(cts.Token), "structured authenticator should connect");
            var svc = client.Get<IConnectionBehaviorService>();
            Ensure(await svc.GetAuthenticationSummaryAsync() == "user-42|admin", "authentication context should flow into service");
            Ensure(
                await svc.GetAuthenticationDetailsAsync() == $"tenant-a|True|True|{expiresAt:O}",
                "tenant/scopes/expiresAt should flow into service");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
            await cts.CancelAsync();
            (server as IDisposable)?.Dispose();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpAuthorizationGuardsShouldReturnStructuredRemoteErrors()
    {
        var expiresAt = new DateTimeOffset(2026, 4, 19, 12, 34, 56, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Authenticate(
                    new SharpLinkAuthenticationContext(
                        subject: "user-42",
                        tenantId: "tenant-a",
                        scopes: ["rpc.read"],
                        expiresAt: new DateTimeOffset(2026, 4, 19, 12, 34, 56, TimeSpan.Zero),
                        claims: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["role"] = "admin"
                        }))
                : SharpLinkAuthenticationResult.Reject())
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)((ILocalEndPointTransport)serverBuilder.Transport!).LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.Start(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseAuthenticator("expected-token")
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            Ensure(await client.ConnectAsync(cts.Token), "authorization guard client should connect");
            var svc = client.Get<IConnectionBehaviorService>();

            await EnsureThrowsSharpLink(
                svc.RequireScopeAsync("rpc.write").AsTask(),
                "scope guard",
                SharpLinkErrorCode.AuthorizationDenied,
                "rpc.write");

            await EnsureThrowsSharpLink(
                svc.RequireTenantAsync("tenant-b").AsTask(),
                "tenant guard",
                SharpLinkErrorCode.AuthorizationDenied,
                "tenant-b");

            await EnsureThrowsSharpLink(
                svc.RequireActiveTokenAsync(expiresAt.ToUnixTimeSeconds() + 1).AsTask(),
                "expiry guard",
                SharpLinkErrorCode.AuthenticationExpired,
                "expired");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
            await cts.CancelAsync();
            (server as IDisposable)?.Dispose();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpServerStartShouldCancelWhileWaitingForConnection()
    {
        var server = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await EnsureThrows<OperationCanceledException>(server.Start(cts.Token), "tcp server start cancel");
        }
        finally
        {
            (server as IDisposable)?.Dispose();
        }
    }

    [Test]
    public async Task NamedPipeServerStartShouldCancelWhileWaitingForConnection()
    {
        var pipeName = $"sharplink-start-cancel-{Guid.NewGuid():N}";
        var server = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseNamedPipe(pipeName)
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await EnsureThrows<OperationCanceledException>(server.Start(cts.Token), "namedpipe server start cancel");
        }
        finally
        {
            (server as IDisposable)?.Dispose();
        }
    }

    [Test]
    public async Task ServerStartShouldSurfaceTransportAcceptException()
    {
        var server = SharpLinkServerBuilder.Create()
            .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
            .UseTransport(new ThrowingConnectTransport(new IOException("accept failed")))
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await EnsureThrows<IOException>(server.Start(CancellationToken.None), "server start transport accept exception");
        }
        finally
        {
            (server as IDisposable)?.Dispose();
        }
    }

    [Test]
    public async Task TcpShouldReconnectWithNewClientInstanceAfterDisconnect()
    {
        await VerifyReconnectWithNewClientInstanceAsync(TransportKind.Tcp);
    }

    [Test]
    public async Task NamedPipeShouldReconnectWithNewClientInstanceAfterDisconnect()
    {
        await VerifyReconnectWithNewClientInstanceAsync(TransportKind.NamedPipe);
    }

    [Test]
    public async Task UdsShouldReconnectWithNewClientInstanceAfterDisconnect()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;

        await VerifyReconnectWithNewClientInstanceAsync(TransportKind.Uds);
    }


    private static async Task EnsureThrows<TException>(Task task, string name) where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"assert failed: {name} should throw {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static async Task EnsureThrowsSharpLinkFast(Task task, string name, SharpLinkErrorCode errorCode)
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
            Ensure(ex.Code == errorCode, $"{name} error code");
        }
    }

    private static async Task EnsureThrowsSharpLink(Task task, string name, SharpLinkErrorCode errorCode, string? messageContains = null)
    {
        try
        {
            await task;
            throw new Exception($"assert failed: {name} should throw SharpLinkException");
        }
        catch (SharpLinkException ex)
        {
            Ensure(ex.Code == errorCode, $"{name} error code");
            if (!string.IsNullOrWhiteSpace(messageContains))
                Ensure(ex.Message.Contains(messageContains, StringComparison.Ordinal), $"{name} error message");
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private static SharpLinkException EnsureConnectionException(ISharpLinkClient client, string name)
    {
        if (client is not ISharpLinkClientDiagnostics diagnostics)
            throw new Exception($"assert failed: {name} missing diagnostics interface");

        if (diagnostics.LastConnectionException is SharpLinkException exception)
            return exception;

        var actualType = diagnostics.LastConnectionException?.GetType().FullName ?? "<null>";
        var actualMessage = diagnostics.LastConnectionException?.Message ?? "<null>";
        throw new Exception($"assert failed: {name} actual={actualType} message={actualMessage}");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GetUniqueUdsPath()
    {
        return Path.Combine(Path.GetTempPath(), $"sharplink-{Guid.NewGuid():N}.sock");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
        {
            IgnoreExpectedException(ex);
        }
    }

    private static async Task VerifyReconnectWithNewClientInstanceAsync(TransportKind kind)
    {
        await using var harness1 = await TransportHarness.CreateAsync(kind);
        var svc1 = harness1.Client.Get<IConnectionBehaviorService>();
        Ensure(await svc1.PingAsync(20) == 21, $"{kind} first connect");
        harness1.DisposeClientOnly();
        await Task.Delay(120);

        var client2 = BuildClientForEndpoint(harness1.Endpoint);
        try
        {
            var connected = await client2.ConnectAsync();
            Ensure(connected, $"{kind} reconnect connect");
            var svc2 = client2.Get<IConnectionBehaviorService>();
            Ensure(await svc2.PingAsync(30) == 31, $"{kind} reconnect ping");
        }
        finally
        {
            (client2 as IDisposable)?.Dispose();
        }
    }

    private static ISharpLinkClient BuildClientForEndpoint(TransportEndpoint endpoint)
    {
        var builder = SharpClientBuilder.Create()
            .UseSerializer(MemoryPackCodec.Resolver)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        switch (endpoint.Kind)
        {
            case TransportKind.Tcp:
                builder.UseTcp(IPAddress.Loopback.ToString(), endpoint.Port);
                break;
            case TransportKind.NamedPipe:
                builder.UseNamedPipe(endpoint.PipeName);
                break;
            case TransportKind.Uds:
                builder.UseUds(endpoint.UdsPath);
                break;
            default:
                throw new InvalidOperationException($"Unsupported endpoint kind: {endpoint.Kind}");
        }

        return builder.Build();
    }

    private enum TransportKind
    {
        Tcp,
        NamedPipe,
        Uds
    }

    private readonly record struct TransportEndpoint(TransportKind Kind, int Port, string PipeName, string UdsPath);

    private sealed class ThrowingConnectTransport(Exception exception) : ITransport
    {
        public Task<IRpcSession> ConnectAsync(CancellationToken ct = default)
            => Task.FromException<IRpcSession>(exception);

        public void Dispose()
        {
        }
    }

    private sealed class TransportHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        private readonly ISharpLinkServer _server;
        private readonly Action _cleanup;
        private bool _serverDisposed;
        private bool _clientDisposed;

        public ISharpLinkClient Client { get; }
        public TransportEndpoint Endpoint { get; }

        private TransportHarness(
            ISharpLinkServer server,
            Task serverTask,
            CancellationTokenSource serverCts,
            ISharpLinkClient client,
            TransportEndpoint endpoint,
            Action cleanup)
        {
            _server = server;
            _serverTask = serverTask;
            _serverCts = serverCts;
            Client = client;
            Endpoint = endpoint;
            _cleanup = cleanup;
        }

        public static Task<TransportHarness> CreateAsync(TransportKind kind) => CreateAsync(kind, default);

        private static async Task<TransportHarness> CreateAsync(TransportKind kind, TransportEndpoint endpoint)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .AddService<IConnectionBehaviorService, ConnectionBehaviorService>()
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

            var clientBuilder = SharpClientBuilder.Create()
                .UseSerializer(MemoryPackCodec.Resolver)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

            Action cleanup = static () => { };
            TransportEndpoint resolvedEndpoint = endpoint;

            switch (kind)
            {
                case TransportKind.Tcp:
                {
                    serverBuilder.UseTcp(endpoint.Port, IPAddress.Loopback.ToString());
                    var port = ((IPEndPoint)((ILocalEndPointTransport)serverBuilder.Transport!).LocalEndPoint!).Port;
                    clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
                    resolvedEndpoint = new TransportEndpoint(kind, port, string.Empty, string.Empty);
                    break;
                }
                case TransportKind.NamedPipe:
                {
                    var pipeName = string.IsNullOrWhiteSpace(endpoint.PipeName)
                        ? $"sharplink-int-{Guid.NewGuid():N}"
                        : endpoint.PipeName;
                    serverBuilder.UseNamedPipe(pipeName);
                    clientBuilder.UseNamedPipe(pipeName);
                    resolvedEndpoint = new TransportEndpoint(kind, 0, pipeName, string.Empty);
                    break;
                }
                case TransportKind.Uds:
                {
                    if (!Socket.OSSupportsUnixDomainSockets)
                        throw new PlatformNotSupportedException("Unix domain sockets are not supported on this platform.");

                    var udsPath = string.IsNullOrWhiteSpace(endpoint.UdsPath)
                        ? GetUniqueUdsPath()
                        : endpoint.UdsPath;
                    serverBuilder.UseUds(udsPath);
                    clientBuilder.UseUds(udsPath);
                    resolvedEndpoint = new TransportEndpoint(kind, 0, string.Empty, udsPath);
                    cleanup = () => TryDeleteFile(udsPath);
                    break;
                }
            }

            var server = serverBuilder.Build();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.Start(cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
                {
                    IgnoreExpectedException(ex);
                }
            }, CancellationToken.None);

            var client = clientBuilder.Build();
            var connected = await client.ConnectAsync(cts.Token);
            if (!connected)
                throw new Exception("client connect failed");

            return new TransportHarness(server, serverTask, cts, client, resolvedEndpoint, cleanup);
        }

        public void DisposeServerOnly()
        {
            if (_serverDisposed)
                return;

            _serverDisposed = true;
            try
            {
                (_server as IDisposable)?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                IgnoreExpectedException(ex);
            }
        }

        public void DisposeClientOnly()
        {
            if (_clientDisposed)
                return;

            _clientDisposed = true;
            try
            {
                (Client as IDisposable)?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ArgumentException)
            {
                IgnoreExpectedException(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            DisposeClientOnly();
            try
            {
                await _serverCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // already disposed by racing cleanup path
            }
            DisposeServerOnly();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            try
            {
                _serverCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // already disposed by racing cleanup path
            }
            _cleanup();
        }
    }

    private static void IgnoreExpectedException(Exception ex)
    {
        _ = ex.HResult;
    }
}

[RpcContract]
public interface IConnectionBehaviorService : IService
{
    ValueTask<int> PingAsync(int value);
    ValueTask<int> SlowAsync(int delayMs, CancellationToken cancellationToken = default);
    ValueTask<string> GetAuthenticationSummaryAsync();
    ValueTask<string> GetAuthenticationDetailsAsync();
    ValueTask<int> RequireScopeAsync(string scope);
    ValueTask<int> RequireTenantAsync(string tenantId);
    ValueTask<int> RequireActiveTokenAsync(long unixSeconds);
}

[RpcService]
public sealed class ConnectionBehaviorService : IConnectionBehaviorService
{
    public ValueTask<int> PingAsync(int value) => ValueTask.FromResult(value + 1);

    public async ValueTask<int> SlowAsync(int delayMs, CancellationToken cancellationToken = default)
    {
        await Task.Delay(delayMs, cancellationToken);
        return delayMs;
    }

    public ValueTask<string> GetAuthenticationSummaryAsync()
    {
        var context = SharpLinkCallContext.Current?.Authentication;
        var subject = context?.Subject ?? "<null>";
        var role = context?.GetClaim("role") ?? "<null>";
        return ValueTask.FromResult($"{subject}|{role}");
    }

    public ValueTask<string> GetAuthenticationDetailsAsync()
    {
        var context = SharpLinkCallContext.Current?.Authentication;
        var tenantId = context?.TenantId ?? "<null>";
        var hasRead = context?.HasScope("rpc.read") ?? false;
        var hasWrite = context?.HasScope("rpc.write") ?? false;
        var expiresAt = context?.ExpiresAt?.ToString("O") ?? "<null>";
        return ValueTask.FromResult($"{tenantId}|{hasRead}|{hasWrite}|{expiresAt}");
    }

    public ValueTask<int> RequireScopeAsync(string scope)
    {
        SharpLinkAuthorization.RequireScope(scope);
        return ValueTask.FromResult(1);
    }

    public ValueTask<int> RequireTenantAsync(string tenantId)
    {
        SharpLinkAuthorization.RequireTenant(tenantId);
        return ValueTask.FromResult(1);
    }

    public ValueTask<int> RequireActiveTokenAsync(long unixSeconds)
    {
        SharpLinkAuthorization.RequireActiveToken(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        return ValueTask.FromResult(1);
    }
}
