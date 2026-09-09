using System.Collections.Concurrent;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class ConnectionAdmissionRuntimeUpdateTests
{
    [Test]
    public async Task ConnectionTargetIncreaseUsesTheExistingAccountingDomain()
    {
        var gate = new ServerConnectionAdmission(maxConnections: 1, maxHandshakes: 1);
        Ensure(gate.TryAcquireConnection(out var first), "the initial connection must be admitted");
        Ensure(!gate.TryAcquireConnection(out _), "the initial target must reject a second connection");

        gate.UpdateTargets(maxConnections: 2, maxHandshakes: 2);

        await Assert.That(gate.MaxConnections).IsEqualTo(2);
        Ensure(gate.TryAcquireConnection(out var second), "the increased target must expose one additional slot");
        await Assert.That(gate.ActiveConnections).IsEqualTo(2);

        first.ReleaseConnection();
        second.ReleaseConnection();
        await Assert.That(gate.ActiveConnections).IsEqualTo(0);
    }

    [Test]
    public async Task ConnectionTargetShrinkPreservesExistingLeasesAndBlocksUntilUsageIsBelowTarget()
    {
        var gate = new ServerConnectionAdmission(maxConnections: 3, maxHandshakes: 3);
        Ensure(gate.TryAcquireConnection(out var first), "first connection must be admitted");
        Ensure(gate.TryAcquireConnection(out var second), "second connection must be admitted");
        Ensure(gate.TryAcquireConnection(out var third), "third connection must be admitted");

        gate.UpdateTargets(maxConnections: 1, maxHandshakes: 1);

        await Assert.That(gate.ActiveConnections).IsEqualTo(3);
        Ensure(!gate.TryAcquireConnection(out _), "shrink must reject while usage is above the target");

        first.ReleaseConnection();
        await Assert.That(gate.ActiveConnections).IsEqualTo(2);
        Ensure(!gate.TryAcquireConnection(out _), "usage above the target must remain closed to new admission");

        second.ReleaseConnection();
        await Assert.That(gate.ActiveConnections).IsEqualTo(1);
        Ensure(!gate.TryAcquireConnection(out _), "usage equal to the target must not grant another slot");

        third.ReleaseConnection();
        await Assert.That(gate.ActiveConnections).IsEqualTo(0);
        Ensure(gate.TryAcquireConnection(out var replacement), "admission must resume once usage falls below the target");
        replacement.ReleaseConnection();
        await Assert.That(gate.ActiveConnections).IsEqualTo(0);
    }

    [Test]
    public async Task HandshakeTargetIncreaseAndShrinkPreserveInFlightAccounting()
    {
        var gate = new ServerConnectionAdmission(maxConnections: 4, maxHandshakes: 1);
        Ensure(gate.TryAcquireConnection(out var first), "first connection must be admitted");
        Ensure(gate.TryAcquireConnection(out var second), "second connection must be admitted");
        Ensure(gate.TryAcquireConnection(out var third), "third connection must be admitted");
        Ensure(gate.TryAcquireConnection(out var fourth), "fourth connection must be admitted");

        Ensure(gate.TryAcquireHandshake(first), "the first handshake must be admitted");
        Ensure(!gate.TryAcquireHandshake(second), "the initial handshake target must reject a second handshake");

        gate.UpdateTargets(maxConnections: 4, maxHandshakes: 3);
        Ensure(gate.TryAcquireHandshake(second), "the increased target must admit the second handshake");
        Ensure(gate.TryAcquireHandshake(third), "the increased target must admit the third handshake");
        await Assert.That(gate.ActiveHandshakes).IsEqualTo(3);

        gate.UpdateTargets(maxConnections: 4, maxHandshakes: 1);
        await Assert.That(gate.ActiveHandshakes).IsEqualTo(3);
        Ensure(!gate.TryAcquireHandshake(fourth), "shrink must not grant a new handshake while usage is above target");

        first.ReleaseHandshake();
        Ensure(!gate.TryAcquireHandshake(fourth), "usage above the target must remain closed to handshake admission");
        second.ReleaseHandshake();
        Ensure(!gate.TryAcquireHandshake(fourth), "usage equal to the target must not grant another handshake");
        third.ReleaseHandshake();
        Ensure(gate.TryAcquireHandshake(fourth), "handshake admission must resume below the target");

        fourth.ReleaseHandshake();
        first.ReleaseConnection();
        second.ReleaseConnection();
        third.ReleaseConnection();
        fourth.ReleaseConnection();
        await Assert.That(gate.ActiveHandshakes).IsEqualTo(0);
        await Assert.That(gate.ActiveConnections).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidTargetPairDoesNotPublishPartially()
    {
        var gate = new ServerConnectionAdmission(maxConnections: 4, maxHandshakes: 2);

        var failure = await Assert.ThrowsAsync(() =>
        {
            gate.UpdateTargets(maxConnections: 1, maxHandshakes: 2);
            return Task.CompletedTask;
        });

        await Assert.That(failure).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(gate.MaxConnections).IsEqualTo(4);
        await Assert.That(gate.MaxHandshakes).IsEqualTo(2);
    }

    [Test]
    public async Task RepeatedConcurrentUpdatesAndAcquisitionsReturnOneAccountingDomainToZero()
    {
        var gate = new ServerConnectionAdmission(maxConnections: 64, maxHandshakes: 64);
        var failures = new ConcurrentQueue<Exception>();
        var workers = new Thread[5];

        workers[0] = new Thread(() =>
        {
            try
            {
                for (var index = 0; index < 10000; index++)
                {
                    var target = (index & 1) == 0 ? 1 : 64;
                    gate.UpdateTargets(target, target);
                }
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        });

        for (var workerIndex = 1; workerIndex < workers.Length; workerIndex++)
        {
            workers[workerIndex] = new Thread(() =>
            {
                try
                {
                    for (var index = 0; index < 5000; index++)
                    {
                        if (!gate.TryAcquireConnection(out var lease))
                            continue;
                        if (gate.TryAcquireHandshake(lease))
                            lease.ReleaseHandshake();
                        lease.ReleaseConnection();
                    }
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            });
        }

        foreach (var worker in workers)
            worker.Start();
        foreach (var worker in workers)
            worker.Join();

        Ensure(failures.IsEmpty, "concurrent target publication/acquisition must not throw");
        await Assert.That(gate.ActiveConnections).IsEqualTo(0);
        await Assert.That(gate.ActiveHandshakes).IsEqualTo(0);
    }

    [Test]
    public async Task ServerRuntimeUpdatePreservesDefaultClampAndExplicitZeroSemantics()
    {
        await using var server = CreateServer(options =>
        {
            options.MaxConcurrentConnections = 16;
            options.MaxConcurrentHandshakes = 4;
        });

        server.UpdateConnectionAdmission(options => options.MaxConcurrentConnections = 3);
        await Assert.That(server.ConnectionAdmission.MaxConnections).IsEqualTo(3);
        await Assert.That(server.ConnectionAdmission.MaxHandshakes).IsEqualTo(3);

        server.UpdateConnectionAdmission(options =>
        {
            options.MaxConcurrentConnections = 7;
            options.MaxConcurrentHandshakes = 0;
        });
        await Assert.That(server.ConnectionAdmission.MaxConnections).IsEqualTo(7);
        await Assert.That(server.ConnectionAdmission.MaxHandshakes).IsEqualTo(7);

        var failure = await Assert.ThrowsAsync(() =>
        {
            server.UpdateConnectionAdmission(options =>
            {
                options.MaxConcurrentConnections = 2;
                options.MaxConcurrentHandshakes = 3;
            });
            return Task.CompletedTask;
        });
        await Assert.That(failure).IsTypeOf<ArgumentOutOfRangeException>();
        await Assert.That(server.ConnectionAdmission.MaxConnections).IsEqualTo(7);
        await Assert.That(server.ConnectionAdmission.MaxHandshakes).IsEqualTo(7);
    }

    [Test]
    public async Task StopWinsAgainstAnUpdateCandidateThatHasNotPublished()
    {
        var listener = new BlockingListener();
        await using var server = CreateServer(
            options =>
            {
                options.MaxConcurrentConnections = 4;
                options.MaxConcurrentHandshakes = 2;
            },
            listener);
        var runTask = server.RunAsync().AsTask();
        await listener.AcceptEntered;

        using var candidateEntered = new ManualResetEventSlim();
        using var releaseCandidate = new ManualResetEventSlim();
        Exception? updateFailure = null;
        var updater = new Thread(() =>
        {
            try
            {
                server.UpdateConnectionAdmission(options =>
                {
                    candidateEntered.Set();
                    releaseCandidate.Wait();
                    options.MaxConcurrentConnections = 8;
                    options.MaxConcurrentHandshakes = 4;
                });
            }
            catch (Exception exception)
            {
                updateFailure = exception;
            }
        });

        updater.Start();
        candidateEntered.Wait();
        try
        {
            await server.StopAsync(TimeSpan.Zero);
        }
        finally
        {
            releaseCandidate.Set();
            updater.Join();
        }

        await runTask;
        await Assert.That(updateFailure).IsTypeOf<InvalidOperationException>();
        await Assert.That(server.ConnectionAdmission.MaxConnections).IsEqualTo(4);
        await Assert.That(server.ConnectionAdmission.MaxHandshakes).IsEqualTo(2);
    }

    private static SharpLinkServer CreateServer(
        Action<SharpLinkConnectionAdmissionOptions> configure,
        IServerTransportListener? listener = null)
        => (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(listener ?? new BlockingListener())
            .UseConnectionAdmission(configure)
            .Build();

    private sealed class BlockingListener : IServerTransportListener
    {
        private readonly TaskCompletionSource _acceptEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task AcceptEntered => _acceptEntered.Task;

        public EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            _acceptEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The blocking listener should only complete through cancellation.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
