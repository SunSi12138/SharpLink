namespace SharpLink.IntegrationTests;

public partial class TransportConnectionIntegrationTests
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
    public async Task UdsHarnessShouldPublishAnOwnerOnlySocketAndCleanItUp()
    {
        if (OperatingSystem.IsWindows() || !Socket.OSSupportsUnixDomainSockets)
            return;

        string path;
        await using (var harness = await TransportHarness.CreateAsync(TransportKind.Uds))
        {
            path = harness.Endpoint.UdsPath;
            Ensure(File.Exists(path), "filesystem UDS path should exist while the server runs");

            var mode = File.GetUnixFileMode(path);
            Ensure(
                (mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite)) ==
                (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "filesystem UDS must allow owner read/write");
            Ensure(
                (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                         UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0,
                "filesystem UDS must deny group and other access");

            var svc = harness.Client.Get<IConnectionBehaviorService>();
            Ensure(await svc.PingAsync(13) == 14, "uds rpc with hardened socket permissions");
        }

        Ensure(!File.Exists(path), "dispose should remove the owned UDS path");
    }

    [Test]
    public async Task TcpServerUnexpectedDisconnectShouldFailFastPendingCall()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.Tcp);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeServerOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "tcp pending should fail fast after server dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task NamedPipeServerUnexpectedDisconnectShouldFailFastPendingCall()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.NamedPipe);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeServerOnlyAsync();

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
        await harness.DisposeServerOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "uds pending should fail fast after server dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task TcpClientDisposeShouldFailFastPendingCall()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.Tcp);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeClientOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "tcp pending should fail fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task NamedPipeClientDisposeShouldFailFastPendingCall()
    {
        await using var harness = await TransportHarness.CreateAsync(TransportKind.NamedPipe);
        var svc = harness.Client.Get<IConnectionBehaviorService>();

        var pending = svc.SlowAsync(2000, CancellationToken.None).AsTask();
        await Task.Delay(120);
        await harness.DisposeClientOnlyAsync();

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
        await harness.DisposeClientOnlyAsync();

        await EnsureThrowsSharpLinkFast(pending, "uds pending should fail fast after client dispose", SharpLinkErrorCode.ConnectionClosed);
    }

    [Test]
    public async Task TcpConnectWithoutServerShouldThrowSocketException()
    {
        var port = GetFreePort();
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await EnsureThrows<SocketException>(client.ConnectAsync(), "tcp connect without server");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task NamedPipeConnectWithoutServerShouldHonorCancellation()
    {
        var pipeName = $"sharplink-int-no-server-{Guid.NewGuid():N}";
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseNamedPipe(pipeName)

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
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task UdsConnectWithoutServerShouldThrowSocketException()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
            return;

        var socketPath = GetUniqueUdsPath();
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseUds(socketPath)

            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await EnsureThrows<SocketException>(client.ConnectAsync(), "uds connect without server");
        }
        finally
        {
            await client.DisposeAsync();
            TryDeleteFile(socketPath);
        }
    }

    [Test]
    public async Task TcpConnectWithCanceledTokenShouldThrowOperationCanceledException()
    {
        var port = GetFreePort();
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

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
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task TcpServerStartShouldStopNormallyWhenCancellationIsRequested()
    {
        var server = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())

            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await server.RunAsync(cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task NamedPipeServerStartShouldStopNormallyWhenCancellationIsRequested()
    {
        var pipeName = $"sharplink-start-cancel-{Guid.NewGuid():N}";
        var server = SharpLinkServerBuilder.Create()
            .UseNamedPipe(pipeName)

            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await server.RunAsync(cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Test]
    public async Task ServerStartShouldSurfaceTransportAcceptException()
    {
        var server = SharpLinkServerBuilder.Create()
            .UseTransport(new ThrowingConnectTransport(new IOException("accept failed")))

            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await EnsureThrows<IOException>(server.RunAsync(CancellationToken.None).AsTask(), "server start transport accept exception");
        }
        finally
        {
            await server.DisposeAsync();
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
}
