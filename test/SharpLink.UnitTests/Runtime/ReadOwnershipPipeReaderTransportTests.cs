using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SharpLink.UnitTests.Runtime;

public class ReadOwnershipPipeReaderTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Test]
    [Arguments("tcp")]
    [Arguments("tls")]
    [Arguments("named-pipe")]
    public async Task PublicTransportInputsShouldPreserveLongSequentialBidirectionalReads(string transport)
    {
        await using var pair = await TransportPair.CreateAsync(transport);
        using var deadline = new CancellationTokenSource(Timeout);

        for (var sequence = 0; sequence < 2_048; sequence++)
        {
            var toClient = Payload(sequence, 0x31);
            var toServer = Payload(sequence, 0x72);
            var clientRead = pair.Client.Input.ReadAsync(deadline.Token);
            var serverRead = pair.Server.Input.ReadAsync(deadline.Token);
            Ensure(!clientRead.IsCompleted && !serverRead.IsCompleted,
                "each drained transport must start its next read before its peer writes");

            var clientFrame = ReadFrameAsync(pair.Client.Input, clientRead, toClient, deadline.Token);
            var serverFrame = ReadFrameAsync(pair.Server.Input, serverRead, toServer, deadline.Token);
            await pair.Server.Output.WriteAsync(toClient, deadline.Token);
            await pair.Client.Output.WriteAsync(toServer, deadline.Token);
            await Task.WhenAll(clientFrame, serverFrame).WaitAsync(Timeout);
        }
    }

    [Test]
    [Arguments("tcp")]
    [Arguments("tls")]
    [Arguments("named-pipe")]
    public async Task PublicTransportInputShouldRecoverAfterRepeatedTokenCancellation(string transport)
    {
        await using var pair = await TransportPair.CreateAsync(transport);
        using var deadline = new CancellationTokenSource(Timeout);

        for (var sequence = 0; sequence < 64; sequence++)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var canceledRead = pair.Server.Input.ReadAsync(cancellation.Token);
            Ensure(!canceledRead.IsCompleted, "the token must cancel a genuinely suspended transport read");
            cancellation.Cancel();
            var failure = await CaptureFailureAsync(canceledRead);
            Ensure(failure is OperationCanceledException,
                "caller cancellation must remain an observable cancellation exception");

            var payload = Payload(sequence, 0x43);
            var nextRead = pair.Server.Input.ReadAsync(deadline.Token);
            Ensure(!nextRead.IsCompleted, "cancellation must release ownership so another read can suspend");
            var nextFrame = ReadFrameAsync(pair.Server.Input, nextRead, payload, deadline.Token);
            await pair.Client.Output.WriteAsync(payload, deadline.Token);
            await nextFrame.WaitAsync(Timeout);
        }
    }

    [Test]
    [Arguments("tcp")]
    [Arguments("tls")]
    [Arguments("named-pipe")]
    public async Task PublicTransportCanceledResultShouldRetainOwnershipUntilAdvanced(string transport)
    {
        await using var pair = await TransportPair.CreateAsync(transport);
        using var deadline = new CancellationTokenSource(Timeout);
        var reader = (ReadOwnershipPipeReader)pair.Client.Input;
        var pending = reader.ReadAsync(deadline.Token);
        Ensure(!pending.IsCompleted, "CancelPendingRead must interrupt a suspended transport read");
        reader.CancelPendingRead();
        var result = await pending.AsTask().WaitAsync(Timeout);
        Task completion;
        try
        {
            Ensure(result.IsCanceled, "CancelPendingRead must produce a canceled ReadResult");
            Ensure(result.Buffer.IsEmpty, "canceling an empty transport must not fabricate a payload");
            completion = reader.CompleteAsync().AsTask();
            Ensure(reader.CompletionRequested && !completion.IsCompleted,
                "a canceled ReadResult still owns its buffer until AdvanceTo");
        }
        finally
        {
            reader.AdvanceTo(result.Buffer.End);
        }

        await completion.WaitAsync(Timeout);
        await AssertCompletingReaderAsync(reader);
    }

    [Test]
    [Arguments("tcp")]
    [Arguments("tls")]
    [Arguments("named-pipe")]
    public async Task PublicTransportDisposalShouldWaitForRetainedReadAfterPeerClose(string transport)
    {
        await using var pair = await TransportPair.CreateAsync(transport);
        using var deadline = new CancellationTokenSource(Timeout);
        var reader = (ReadOwnershipPipeReader)pair.Client.Input;
        var payload = new byte[8_192];
        for (var index = 0; index < payload.Length; index++)
            payload[index] = (byte)(index * 17 + 3);
        var pending = reader.ReadAsync(deadline.Token);
        Ensure(!pending.IsCompleted, "the retained buffer must come from a suspended real-stream read");
        await pair.Server.Output.WriteAsync(payload, deadline.Token);
        var result = await pending.AsTask().WaitAsync(Timeout);
        Task disposal;
        try
        {
            Ensure(!result.Buffer.IsEmpty, "the real stream must deliver some of the sent payload");
            var retained = result.Buffer.ToArray();
            Ensure(retained.AsSpan().SequenceEqual(payload.AsSpan(0, retained.Length)),
                "a possibly partial transport read must preserve the payload prefix");
            await pair.Server.DisposeAsync().AsTask().WaitAsync(Timeout);
            disposal = pair.Client.DisposeAsync().AsTask();
            await WaitForCompletionRequestAsync(reader, deadline.Token);
            Ensure(!disposal.IsCompleted,
                "local disposal must wait while the consumer retains data, even after its peer closes");
            Ensure(result.Buffer.ToArray().AsSpan().SequenceEqual(retained),
                "transport close must not return or overwrite a consumer-owned segment");
        }
        finally
        {
            reader.AdvanceTo(result.Buffer.End);
        }

        await disposal.WaitAsync(Timeout);
        await AssertCompletingReaderAsync(reader);
    }

    [Test]
    [Arguments("tcp")]
    [Arguments("tls")]
    [Arguments("named-pipe")]
    public async Task PublicTransportPeerCloseShouldCompletePendingReadWithoutPayload(string transport)
    {
        await using var pair = await TransportPair.CreateAsync(transport);
        using var deadline = new CancellationTokenSource(Timeout);
        var reader = pair.Client.Input;
        var pending = reader.ReadAsync(deadline.Token);
        Ensure(!pending.IsCompleted, "the read must be waiting for the peer before close");
        await pair.Server.DisposeAsync().AsTask().WaitAsync(Timeout);
        var result = await pending.AsTask().WaitAsync(Timeout);
        try
        {
            Ensure(result.IsCompleted && !result.IsCanceled && result.Buffer.IsEmpty,
                "an orderly peer close must produce an empty completed read");
        }
        finally
        {
            reader.AdvanceTo(result.Buffer.End);
        }
    }

    [Test]
    public async Task StreamPipeReaderShouldKeepPooledMemoryUntilRetainedReadIsAdvanced()
    {
        var pipe = new Pipe();
        using var pool = new TrackingMemoryPool();
        await using var stream = pipe.Reader.AsStream(leaveOpen: true);
        var reader = new ReadOwnershipPipeReader(PipeReader.Create(stream,
            new StreamPipeReaderOptions(pool: pool, bufferSize: 256, minimumReadSize: 64, leaveOpen: true)));
        using var deadline = new CancellationTokenSource(Timeout);
        try
        {
            var payload = Payload(1_234, 0x5a);
            var pending = reader.ReadAsync(deadline.Token);
            Ensure(!pending.IsCompleted, "the real StreamPipeReader must suspend before the pipe is written");
            await pipe.Writer.WriteAsync(payload, deadline.Token);
            var result = await pending.AsTask().WaitAsync(Timeout);
            Task completion;
            try
            {
                Ensure(result.Buffer.ToArray().AsSpan().SequenceEqual(payload),
                    "the stream-backed read must expose the written payload");
                completion = reader.CompleteAsync().AsTask();
                Ensure(!completion.IsCompleted && pool.Rented > 0 && pool.Returned == 0,
                    "completion must not return pooled stream buffers before the consumer releases its read");
                Ensure(result.Buffer.ToArray().AsSpan().SequenceEqual(payload),
                    "consumer-owned bytes must remain intact after completion is requested");
            }
            finally
            {
                reader.AdvanceTo(result.Buffer.End);
            }

            await completion.WaitAsync(Timeout);
            Ensure(pool.Returned == pool.Rented,
                "completion must return every rented stream buffer after the retained read is advanced");
        }
        finally
        {
            await reader.CompleteAsync().AsTask().WaitAsync(Timeout);
            await pipe.Reader.CompleteAsync();
            await pipe.Writer.CompleteAsync();
        }
    }

    [Test]
    public async Task RealPipeFaultShouldReleaseOwnershipAndPreserveFailure()
    {
        var pipe = new Pipe();
        var reader = new ReadOwnershipPipeReader(pipe.Reader);
        using var deadline = new CancellationTokenSource(Timeout);
        try
        {
            var pending = reader.ReadAsync(deadline.Token);
            Ensure(!pending.IsCompleted, "the real pipe must suspend before its writer faults");
            var expected = new IOException("real-pipe read fault");
            await pipe.Writer.CompleteAsync(expected);
            var observed = await CaptureFailureAsync(pending);
            Ensure(ReferenceEquals(expected, observed), "the wrapper must preserve the real pipe's fault instance");
            await reader.CompleteAsync().AsTask().WaitAsync(Timeout);
            await AssertCompletingReaderAsync(reader);
        }
        finally
        {
            await reader.CompleteAsync().AsTask().WaitAsync(Timeout);
            await pipe.Writer.CompleteAsync();
        }
    }

    private static byte[] Payload(int sequence, byte direction)
    {
        var payload = new byte[12];
        BitConverter.TryWriteBytes(payload.AsSpan(), sequence);
        BitConverter.TryWriteBytes(payload.AsSpan(4), ~sequence);
        payload.AsSpan(8).Fill(direction);
        return payload;
    }

    private static async Task ReadFrameAsync(PipeReader reader, ValueTask<ReadResult> pending,
        byte[] expected, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < expected.Length)
        {
            // Exercise the ordinary pooled ValueTask consumer directly, including partial reads.
            var result = await pending.ConfigureAwait(false);
            try
            {
                Ensure(!result.IsCanceled && !result.IsCompleted && !result.Buffer.IsEmpty,
                    "an active exchange must yield data rather than cancellation or premature EOF");
                var bytes = result.Buffer.ToArray();
                Ensure(bytes.Length <= expected.Length - offset,
                    "a sequential exchange must not duplicate or append bytes from another read");
                Ensure(bytes.AsSpan().SequenceEqual(expected.AsSpan(offset, bytes.Length)),
                    "each transport must preserve frame bytes and sequence order across pooled reads");
                offset += bytes.Length;
            }
            finally
            {
                reader.AdvanceTo(result.Buffer.End);
            }
            if (offset < expected.Length)
                pending = reader.ReadAsync(cancellationToken);
        }
    }

    private static async Task<Exception> CaptureFailureAsync(ValueTask<ReadResult> read)
    {
        try
        {
            await read.AsTask().WaitAsync(Timeout);
        }
        catch (Exception failure)
        {
            return failure;
        }
        throw new InvalidOperationException("the read should have failed");
    }

    private static async Task AssertCompletingReaderAsync(ReadOwnershipPipeReader reader)
    {
        Ensure(!reader.TryRead(out _), "a completed transport reader must refuse TryRead");
        var failure = await CaptureFailureAsync(reader.ReadAsync());
        Ensure(failure is InvalidOperationException,
            "a completed transport reader must refuse new asynchronous reads");
    }

    private static async Task WaitForCompletionRequestAsync(ReadOwnershipPipeReader reader,
        CancellationToken cancellationToken)
    {
        while (!reader.CompletionRequested)
            await Task.Delay(1, cancellationToken);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class TransportPair(ITransportConnection client, ITransportConnection server,
        X509Certificate2? certificate) : IAsyncDisposable
    {
        internal ITransportConnection Client => client;
        internal ITransportConnection Server => server;

        internal static async Task<TransportPair> CreateAsync(string transport)
        {
            using var deadline = new CancellationTokenSource(Timeout);
            var certificate = transport == "tls" ? CreateCertificate() : null;
            var pipeName = $"read-ownership-{Guid.NewGuid():N}";
            await using IServerTransportListener listener = transport == "named-pipe"
                ? new NamedPipeServerTransportListener(pipeName)
                : new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0),
                    tlsOptions: certificate is null ? null : new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        AllowRenegotiation = false
                    });
            await using IClientTransportFactory factory = transport == "named-pipe"
                ? new NamedPipeClientTransportFactory(pipeName)
                : new SocketClientTransportFactory(listener.LocalEndPoint!,
                    tlsOptions: certificate is null ? null : new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        AllowRenegotiation = false,
                        RemoteCertificateValidationCallback = (_, remote, _, _) =>
                            remote?.GetCertHashString() == certificate.GetCertHashString()
                    });
            var connecting = factory.ConnectAsync(deadline.Token).AsTask();
            ITransportConnection? client = null;
            ITransportConnection? server = null;
            try
            {
                server = await listener.AcceptAsync(deadline.Token).AsTask().WaitAsync(Timeout);
                if (server is ITransportSecurityHandshake handshake)
                    await handshake.AuthenticateAsync(deadline.Token).AsTask().WaitAsync(Timeout);
                client = await connecting.WaitAsync(Timeout);
                Ensure(client.Input is ReadOwnershipPipeReader && server.Input is ReadOwnershipPipeReader,
                    "both public transport entry points must expose the production ownership reader");
                if (transport == "tls")
                    Ensure(client is ITransportSecurityInfo && server is ITransportSecurityInfo,
                        "TLS cases must exercise authenticated SslStream-backed connections");
                return new TransportPair(client, server, certificate);
            }
            catch
            {
                deadline.Cancel();
                if (client is null)
                {
                    try { client = await connecting.WaitAsync(Timeout); }
                    catch { }
                }
                if (client is not null)
                    await client.DisposeAsync().AsTask().WaitAsync(Timeout);
                if (server is not null)
                    await server.DisposeAsync().AsTask().WaitAsync(Timeout);
                certificate?.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask())
                    .WaitAsync(Timeout);
            }
            finally
            {
                certificate?.Dispose();
            }
        }

        private static X509Certificate2 CreateCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12),
                password: null, X509KeyStorageFlags.DefaultKeySet);
        }
    }

    private sealed class TrackingMemoryPool : MemoryPool<byte>
    {
        private int _rented;
        private int _returned;
        internal int Rented => Volatile.Read(ref _rented);
        internal int Returned => Volatile.Read(ref _returned);
        public override int MaxBufferSize => int.MaxValue;
        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            Interlocked.Increment(ref _rented);
            return new Owner(new byte[Math.Max(256, minBufferSize)], this);
        }
        protected override void Dispose(bool disposing) { }

        private sealed class Owner(byte[] bytes, TrackingMemoryPool pool) : IMemoryOwner<byte>
        {
            private int _disposed;
            public Memory<byte> Memory => bytes;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;
                bytes.AsSpan().Fill(0xdd);
                Interlocked.Increment(ref pool._returned);
            }
        }
    }
}
