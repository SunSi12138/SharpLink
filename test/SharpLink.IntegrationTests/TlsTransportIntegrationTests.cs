using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SharpLink.IntegrationTests;

public class TlsTransportIntegrationTests
{
    [Test]
    public async Task TlsShouldProtectAllRpcShapesAndReconnect()
    {
        using var certificate = CreateCertificate("localhost", serverAuthentication: true);
        var port = GetFreePort();
        await using var firstServer = await StartServerAsync(port, CreateServerOptions(certificate));
        await using var client = CreateClient(port, CreateClientOptions("localhost"));
        await client.ConnectAsync();
        await VerifyRpcShapesAsync(client.Get<ITlsIntegrationService>());

        await firstServer.StopAsync();
        var concreteClient = (SharpLinkClient)client;
        await WaitUntilAsync(() => concreteClient.ReadyConnectionCount == 0);
        await using var secondServer = await StartServerAsync(port, CreateServerOptions(certificate));
        await WaitUntilAsync(() =>
            concreteClient.ReadyConnectionCount != 0 &&
            concreteClient.State == SharpLinkConnectionState.Ready);
        Ensure(await client.Get<ITlsIntegrationService>().AddAsync(20, 22) == 42, "TLS reconnect RPC");
    }

    [Test]
    public async Task TlsShouldRejectWrongHostname()
    {
        using var certificate = CreateCertificate("localhost", serverAuthentication: true);
        await using var server = await StartServerAsync(0, CreateServerOptions(certificate));
        await using var client = CreateClient(server.Port, CreateClientOptions("wrong.example"));
        await EnsureThrows<AuthenticationException>(client.ConnectAsync().AsTask(), "wrong TLS hostname");
    }

    [Test]
    public async Task TlsShouldRejectUntrustedCertificateByDefault()
    {
        using var certificate = CreateCertificate("localhost", serverAuthentication: true);
        await using var server = await StartServerAsync(0, CreateServerOptions(certificate));
        await using var client = CreateClient(server.Port, new SslClientAuthenticationOptions
        {
            TargetHost = "localhost"
        });
        await EnsureThrows<AuthenticationException>(client.ConnectAsync().AsTask(), "untrusted TLS certificate");
    }

    [Test]
    public async Task TlsShouldRejectExpiredCertificate()
    {
        using var certificate = CreateCertificate(
            "localhost",
            serverAuthentication: true,
            notBefore: DateTimeOffset.UtcNow.AddDays(-3),
            notAfter: DateTimeOffset.UtcNow.AddDays(-2));
        await using var server = await StartServerAsync(0, CreateServerOptions(certificate));
        await using var client = CreateClient(server.Port, CreateClientOptions("localhost"));
        await EnsureThrows<AuthenticationException>(client.ConnectAsync().AsTask(), "expired TLS certificate");
    }

    [Test]
    public async Task MutualTlsShouldRequireAndAcceptClientCertificate()
    {
        using var serverCertificate = CreateCertificate("localhost", serverAuthentication: true);
        using var clientCertificate = CreateCertificate("sharplink-client", serverAuthentication: false);
        var serverOptions = CreateServerOptions(serverCertificate);
        serverOptions.ClientCertificateRequired = true;
        serverOptions.EnabledSslProtocols = SslProtocols.Tls12;
        serverOptions.RemoteCertificateValidationCallback = ValidateTestCertificate;
        await using var server = await StartServerAsync(0, serverOptions);

        var missingCertificateOptions = CreateClientOptions("localhost");
        missingCertificateOptions.EnabledSslProtocols = SslProtocols.Tls12;
        await using (var missingCertificateClient = CreateClient(server.Port, missingCertificateOptions))
        {
            await EnsureTlsFailure(
                missingCertificateClient.ConnectAsync().AsTask(),
                "missing mutual TLS certificate");
        }

        var clientOptions = CreateClientOptions("localhost");
        clientOptions.EnabledSslProtocols = SslProtocols.Tls12;
        clientOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
        await using var client = CreateClient(server.Port, clientOptions);
        await client.ConnectAsync();
        Ensure(await client.Get<ITlsIntegrationService>().AddAsync(2, 3) == 5, "mutual TLS RPC");
    }

    [Test]
    public async Task MutualTlsShouldPreserveClientCertificateContext()
    {
        using var serverCertificate = CreateCertificate("localhost", serverAuthentication: true);
        using var clientCertificate = CreateCertificate("sharplink-client", serverAuthentication: false);
        var serverOptions = CreateServerOptions(serverCertificate);
        serverOptions.ClientCertificateRequired = true;
        serverOptions.EnabledSslProtocols = SslProtocols.Tls12;
        serverOptions.RemoteCertificateValidationCallback = ValidateTestCertificate;
        await using var server = await StartServerAsync(0, serverOptions);

        var clientOptions = CreateClientOptions("localhost");
        clientOptions.EnabledSslProtocols = SslProtocols.Tls12;
        clientOptions.ClientCertificateContext = SslStreamCertificateContext.Create(
            clientCertificate,
            additionalCertificates: null,
            offline: true);
        await using var client = CreateClient(server.Port, clientOptions);
        await client.ConnectAsync();
        Ensure(await client.Get<ITlsIntegrationService>().AddAsync(3, 4) == 7,
            "client certificate context mutual TLS RPC");
    }

    [Test]
    public async Task TlsHandshakeShouldHonorIndependentTimeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var acceptCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var acceptTask = listener.AcceptSocketAsync(acceptCts.Token).AsTask();
        await using var client = SharpClientBuilder.Create()
            .UseTcp(
                IPAddress.Loopback.ToString(),
                port,
                new SslClientAuthenticationOptions { TargetHost = "localhost" },
                TimeSpan.FromMilliseconds(100))
            .Build();

        try
        {
            var exception = await EnsureThrows<SharpLinkException>(
                client.ConnectAsync().AsTask(),
                "TLS handshake timeout");
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "TLS timeout error code");
        }
        finally
        {
            listener.Stop();
            if (acceptTask.IsCompletedSuccessfully)
                acceptTask.Result.Dispose();
        }
    }

    [Test]
    public async Task StaticTlsEndpointsShouldUseEndpointAuthorityAndIsolateFailure()
    {
        using var certificate = CreateCertificate("localhost", serverAuthentication: true);
        await using var first = await StartServerAsync(0, CreateServerOptions(certificate));
        await using var second = await StartServerAsync(0, CreateServerOptions(certificate));
        var tlsOptions = CreateClientOptions(string.Empty);
        await using var client = SharpClientBuilder.Create()
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
            .UseEndpoints(
                [
                    new SharpLinkEndpoint
                    {
                        Id = "first",
                        Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), first.Port),
                        Authority = "localhost"
                    },
                    new SharpLinkEndpoint
                    {
                        Id = "second",
                        Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), second.Port),
                        Authority = "localhost"
                    }
                ],
                SharpLinkTransportFactories.Sockets(tlsOptions, tlsHandshakeTimeout: TimeSpan.FromSeconds(2)))
            .Build();

        await client.ConnectAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 2);
        Ensure(await client.Get<ITlsIntegrationService>().AddAsync(5, 6) == 11, "static TLS RPC");
        await first.StopAsync();
        await WaitUntilAsync(() => ((SharpLinkClient)client).ReadyConnectionCount == 1);
        Ensure(await client.Get<ITlsIntegrationService>().AddAsync(7, 8) == 15, "remaining static TLS endpoint");
    }

    private static ISharpLinkClient CreateClient(int port, SslClientAuthenticationOptions options)
        => SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port, options, TimeSpan.FromSeconds(2))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
            .Build();

    private static async Task<TlsServerHarness> StartServerAsync(int port, SslServerAuthenticationOptions options)
    {
        var builder = SharpLinkServerBuilder.Create()
            .UseTcp(port, options, IPAddress.Loopback.ToString(), tlsHandshakeTimeout: TimeSpan.FromSeconds(2))
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
        var boundPort = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        var server = builder.Build();
        var cts = new CancellationTokenSource();
        var runTask = server.RunAsync(cts.Token).AsTask();
        await Task.Yield();
        return new TlsServerHarness(boundPort, server, cts, runTask);
    }

    private static SslClientAuthenticationOptions CreateClientOptions(string targetHost)
        => new()
        {
            TargetHost = targetHost,
            RemoteCertificateValidationCallback = ValidateTestCertificate
        };

    private static SslServerAuthenticationOptions CreateServerOptions(X509Certificate2 certificate)
        => new()
        {
            ServerCertificate = certificate
        };

    private static bool ValidateTestCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 ||
            (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
        {
            return false;
        }

        if (chain is null)
            return errors == SslPolicyErrors.None;
        foreach (var status in chain.ChainStatus)
        {
            if (status.Status is X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.PartialChain)
                continue;
            if (status.Status != X509ChainStatusFlags.NoError)
                return false;
        }
        return true;
    }

    private static X509Certificate2 CreateCertificate(
        string subjectName,
        bool serverAuthentication,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subjectName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new(serverAuthentication ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")
            },
            true));
        if (serverAuthentication)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(subjectName);
            request.CertificateExtensions.Add(names.Build());
        }

        using var generated = request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.DefaultKeySet);
    }

    private static async Task VerifyRpcShapesAsync(ITlsIntegrationService service)
    {
        Ensure(await service.AddAsync(1, 2) == 3, "TLS unary");
        Ensure(await service.SumAsync(ToAsync([1, 2, 3])) == 6, "TLS client stream");
        Ensure((await CollectAsync(service.RangeAsync(3))).SequenceEqual([0, 1, 2]), "TLS server stream");
        Ensure((await CollectAsync(service.EchoAsync(0L, ToAsync([4, 5])))).SequenceEqual([4, 5]), "TLS duplex stream");
    }

    private static async IAsyncEnumerable<int> ToAsync(IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            yield return value;
            await Task.Yield();
        }
    }

    private static async Task<List<int>> CollectAsync(IAsyncEnumerable<int> values)
    {
        var result = new List<int>();
        await foreach (var value in values)
            result.Add(value);
        return result;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private static async Task<TException> EnsureThrows<TException>(Task task, string name)
        where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"assert failed: {name} should throw {typeof(TException).Name}");
        }
        catch (TException exception)
        {
            return exception;
        }
    }

    private static async Task EnsureTlsFailure(Task task, string name)
    {
        try
        {
            await task;
            throw new Exception($"assert failed: {name} should fail TLS authentication");
        }
        catch (Exception exception) when (exception is AuthenticationException or IOException or SharpLinkException)
        {
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private sealed class TlsServerHarness(
        int port,
        ISharpLinkServer server,
        CancellationTokenSource cancellation,
        Task runTask) : IAsyncDisposable
    {
        private int _stopped;
        public int Port { get; } = port;

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            await server.StopAsync(TimeSpan.Zero);
            await cancellation.CancelAsync();
            await Task.WhenAny(runTask, Task.Delay(1000, CancellationToken.None));
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            cancellation.Dispose();
        }
    }
}

[RpcContract]
public interface ITlsIntegrationService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);
    [NonCancellable]
    ValueTask<int> SumAsync(IAsyncEnumerable<int> values);
    [NonCancellable]
    IAsyncEnumerable<int> RangeAsync(int count);
    [NonCancellable]
    IAsyncEnumerable<int> EchoAsync(long marker, IAsyncEnumerable<int> values);
}

[RpcService]
public sealed class TlsIntegrationService : ITlsIntegrationService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public async ValueTask<int> SumAsync(IAsyncEnumerable<int> values)
    {
        var sum = 0;
        await foreach (var value in values)
            sum += value;
        return sum;
    }

    public async IAsyncEnumerable<int> RangeAsync(int count)
    {
        for (var value = 0; value < count; value++)
        {
            yield return value;
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<int> EchoAsync(long marker, IAsyncEnumerable<int> values)
    {
        await foreach (var value in values)
            yield return value;
    }
}
