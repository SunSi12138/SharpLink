namespace SharpLink.IntegrationTests;

public partial class TransportConnectionIntegrationTests
{
    [Test]
    public async Task TcpCustomAuthenticatorShouldAcceptMatchingHandshakeMessage()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())

            .UseAuthenticator(CreateServerAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Success
                : SharpLinkAuthenticationResult.Reject()))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseAuthenticator(CreateClientAuthenticator("expected-token"))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await client.ConnectAsync(cts.Token);
            Ensure(client.State == SharpLinkConnectionState.Ready, "custom authenticator should connect");
            var svc = client.Get<IConnectionBehaviorService>();
            Ensure(await svc.PingAsync(12) == 13, "custom authenticator ping");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpCustomAuthenticatorShouldRejectMismatchedHandshakeMessage()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())

            .UseAuthenticator(CreateServerAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Success
                : SharpLinkAuthenticationResult.Reject()))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseAuthenticator(CreateClientAuthenticator("unexpected-token"))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            var exception = await CaptureSharpLinkException(
                client.ConnectAsync(cts.Token).AsTask(),
                "custom authenticator rejection");
            Ensure(exception.Code == SharpLinkErrorCode.AuthenticationRejected, "custom authenticator diagnostics should expose authentication rejection");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpStructuredAuthenticatorShouldExposeCustomAuthenticationError()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())

            .UseAuthenticator(CreateServerAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Success
                : SharpLinkAuthenticationResult.Reject(
                    SharpLinkErrorCode.AuthenticationExpired,
                    "token expired")))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseAuthenticator(CreateClientAuthenticator("expired-token"))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            var exception = await CaptureSharpLinkException(
                client.ConnectAsync(cts.Token).AsTask(),
                "structured authenticator rejection");
            Ensure(exception.Code == SharpLinkErrorCode.AuthenticationExpired, "structured authenticator code");
            Ensure(exception.Message == "token expired", "structured authenticator message");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpAuthenticatorShouldRejectContradictoryAuthenticatedResult()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer(static (_, _) => ValueTask.FromResult(
                new SharpLinkAuthenticationResult(
                    IsAuthenticated: true,
                    ErrorCode: SharpLinkErrorCode.AuthenticationRejected,
                    ErrorMessage: "provider rejected the credential",
                    Context: null))))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .Build();

        try
        {
            var exception = await CaptureSharpLinkException(
                client.ConnectAsync(cts.Token).AsTask(),
                "contradictory authenticated provider result");
            Ensure(exception.Code == SharpLinkErrorCode.AuthenticationRejected,
                "a provider rejection code must not establish an authenticated connection");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpAuthenticatorShouldSanitizeAnUndefinedRejectionCode()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer(static (_, _) => ValueTask.FromResult(
                new SharpLinkAuthenticationResult(
                    IsAuthenticated: false,
                    ErrorCode: (SharpLinkErrorCode)ushort.MaxValue,
                    ErrorMessage: "undefined provider code",
                    Context: null))))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .Build();

        try
        {
            var exception = await CaptureSharpLinkException(
                client.ConnectAsync(cts.Token).AsTask(),
                "undefined authentication rejection code");
            Ensure(exception.Code == SharpLinkErrorCode.AuthenticationRejected,
                "undefined provider codes must become a stable authentication rejection");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpAuthenticatorShouldRejectExpiredContextDuringHandshake()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())

            .UseAuthenticator(SharpLinkAuthenticator.CreateServer(static (_, _) => ValueTask.FromResult(
                SharpLinkAuthenticationResult.Authenticate(
                    new SharpLinkAuthenticationContext(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1))))))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .Build();

        try
        {
            var exception = await CaptureSharpLinkException(
                client.ConnectAsync(cts.Token).AsTask(),
                "expired authentication context");
            Ensure(exception.Code == SharpLinkErrorCode.AuthenticationExpired, "expired context code");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpClientShouldRejectOversizedAuthenticationPayloadBeforeSend()
    {
        const int maxAuthenticationBytes = 32;
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())

            .UseProtocol(static options => options.MaxMetadataBytes = maxAuthenticationBytes);

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);
        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseProtocol(static options => options.MaxMetadataBytes = maxAuthenticationBytes)
            .UseAuthenticator(SharpLinkAuthenticator.CreateClient(static _ =>
                ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[maxAuthenticationBytes + 1])))
            .Build();

        try
        {
            var exception = await CaptureSharpLinkException(
                client.ConnectAsync(cts.Token).AsTask(),
                "oversized authentication payload");
            Ensure(exception.Code == SharpLinkErrorCode.ResourceExhausted, "authentication payload limit code");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpStructuredAuthenticatorShouldExposeAuthenticationContextToService()
    {
        var expiresAt = new DateTimeOffset(2030, 4, 19, 12, 34, 56, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())

            .UseAuthenticator(CreateServerAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Authenticate(
                    new SharpLinkAuthenticationContext(
                        subject: "user-42",
                        tenantId: "tenant-a",
                        scopes: ["rpc.read", "rpc.write"],
                        expiresAt: new DateTimeOffset(2030, 4, 19, 12, 34, 56, TimeSpan.Zero),
                        claims: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["role"] = "admin"
                        }))
                : SharpLinkAuthenticationResult.Reject()))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseAuthenticator(CreateClientAuthenticator("expected-token"))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await client.ConnectAsync(cts.Token);
            Ensure(client.State == SharpLinkConnectionState.Ready, "structured authenticator should connect");
            var svc = client.Get<IConnectionBehaviorService>();
            Ensure(await svc.GetAuthenticationSummaryAsync() == "user-42|admin", "authentication context should flow into service");
            Ensure(
                await svc.GetAuthenticationDetailsAsync() == $"tenant-a|True|True|{expiresAt:O}",
                "tenant/scopes/expiresAt should flow into service");
        }
        finally
        {
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpAuthenticationContextShouldRemainIsolatedPerConnection()
    {
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())

            .UseAuthenticator(CreateServerAuthenticator(static token =>
                SharpLinkAuthenticationResult.Authenticate(
                    new SharpLinkAuthenticationContext(
                        subject: token,
                        claims: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["role"] = token
                        }))))
            .RequireAuthentication();

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);
        var firstClient = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseAuthenticator(CreateClientAuthenticator("connection-a"))
            .Build();
        var secondClient = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseAuthenticator(CreateClientAuthenticator("connection-b"))
            .Build();

        try
        {
            await Task.WhenAll(
                firstClient.ConnectAsync(cts.Token).AsTask(),
                secondClient.ConnectAsync(cts.Token).AsTask());
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
                Ensure(calls[index].Result == "connection-a|connection-a", "first connection authentication isolation");
                Ensure(calls[index + 1].Result == "connection-b|connection-b", "second connection authentication isolation");
            }
        }
        finally
        {
            await firstClient.DisposeAsync();
            await secondClient.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    [Test]
    public async Task TcpAuthorizationGuardsShouldReturnStructuredRemoteErrors()
    {
        var expiresAt = new DateTimeOffset(2030, 4, 19, 12, 34, 56, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())

            .UseAuthenticator(CreateServerAuthenticator(static message => message == "expected-token"
                ? SharpLinkAuthenticationResult.Authenticate(
                    new SharpLinkAuthenticationContext(
                        subject: "user-42",
                        tenantId: "tenant-a",
                        scopes: ["rpc.read"],
                        expiresAt: new DateTimeOffset(2030, 4, 19, 12, 34, 56, TimeSpan.Zero),
                        claims: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["role"] = "admin"
                        }))
                : SharpLinkAuthenticationResult.Reject()))
            .RequireAuthentication()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
                IgnoreExpectedException(ex);
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseTcp(IPAddress.Loopback.ToString(), port)

            .UseAuthenticator(CreateClientAuthenticator("expected-token"))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500))
            .Build();

        try
        {
            await client.ConnectAsync(cts.Token);
            Ensure(client.State == SharpLinkConnectionState.Ready, "authorization guard client should connect");
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
            await client.DisposeAsync();
            await cts.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }
}
