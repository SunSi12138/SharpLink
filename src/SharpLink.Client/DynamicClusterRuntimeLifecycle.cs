namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Owns dynamic-cluster connect/resolver workers, stop state and resource cleanup supervision.
    /// The runtime supplies topology mutation callbacks while this coordinator owns lifecycle races.
    /// </summary>
    private sealed class DynamicClusterRuntimeLifecycle
    {
        private readonly SharpLinkClient _client;
        private readonly ISharpLinkEndpointResolver _resolver;
        private readonly Lock _gate;
        private readonly Func<SharpLinkEndpointSnapshot, Task<bool>> _applySnapshotAsync;
        private readonly Action _updateClientReadiness;
        private Task? _connectTask;
        private Task? _resolverTask;
        private Task? _stopTask;
        private int _stopping;
        private int _resolverDisposed;
        private IClientTransportFactory[] _stoppedFactories = [];

        public DynamicClusterRuntimeLifecycle(
            SharpLinkClient client,
            ISharpLinkEndpointResolver resolver,
            Lock gate,
            Func<SharpLinkEndpointSnapshot, Task<bool>> applySnapshotAsync,
            Action updateClientReadiness)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _applySnapshotAsync = applySnapshotAsync ?? throw new ArgumentNullException(nameof(applySnapshotAsync));
            _updateClientReadiness = updateClientReadiness ?? throw new ArgumentNullException(nameof(updateClientReadiness));
        }

        public bool IsStopping => Volatile.Read(ref _stopping) != 0;

        public void BeginStop()
        {
            lock (_gate)
                Volatile.Write(ref _stopping, 1);
        }

        public ValueTask ConnectAsync(
            CancellationToken cancellationToken,
            Func<int> getReadyConnectionCount,
            Func<bool> hasCurrentTopology,
            Func<CancellationToken, Task> startAsync,
            Func<Task> waitForRecoveryAsync)
        {
            ArgumentNullException.ThrowIfNull(getReadyConnectionCount);
            ArgumentNullException.ThrowIfNull(hasCurrentTopology);
            ArgumentNullException.ThrowIfNull(startAsync);
            ArgumentNullException.ThrowIfNull(waitForRecoveryAsync);

            Task task;
            lock (_gate)
            {
                if (Volatile.Read(ref _client._stopStarted) != 0 || IsStopping ||
                    _client._shutdownCts.IsCancellationRequested)
                {
                    return ValueTask.FromException(CreateConnectionClosedException("Client has stopped."));
                }
                if (getReadyConnectionCount() != 0)
                    return ValueTask.CompletedTask;

                _client.TransitionTo(SharpLinkConnectionState.Connecting);
                if (_connectTask is null ||
                    ((_connectTask.IsFaulted || _connectTask.IsCanceled) && _resolverTask is null))
                {
                    _connectTask = startAsync(_client._shutdownCts.Token);
                    TrackTask(
                        _connectTask,
                        "DynamicClusterInitialConnect",
                        TaskObservationMode.ExternallyObserved);
                }
                else if (_connectTask.IsFaulted || _connectTask.IsCanceled ||
                         (_connectTask.IsCompletedSuccessfully && hasCurrentTopology()))
                {
                    _connectTask = waitForRecoveryAsync();
                    TrackTask(
                        _connectTask,
                        "DynamicClusterRecoveryWait",
                        TaskObservationMode.ExternallyObserved);
                }
                task = _connectTask;
            }

            return cancellationToken.CanBeCanceled
                ? new ValueTask(task.WaitAsync(cancellationToken))
                : new ValueTask(task);
        }

        public void StartResolverWorker(bool resolveBeforeWatch)
        {
            lock (_gate)
            {
                if (IsStopping || _resolverTask is { IsCompleted: false })
                    return;
                _resolverTask = RunResolverWorkerAsync(resolveBeforeWatch);
                TrackTask(_resolverTask, "DynamicClusterTopologyResolver");
            }
        }

        public void TrackTask(Task task, string name)
            => _client.TrackFrameworkTask(task, name);

        public void TrackTask(Task task, string name, TaskObservationMode observationMode)
            => _client.TrackFrameworkTask(task, name, observationMode);

        public ValueTask StopAsync(Func<DynamicClusterStopSnapshot> detachForStopLocked)
        {
            ArgumentNullException.ThrowIfNull(detachForStopLocked);
            lock (_gate)
            {
                _stopTask ??= StopCoreAsync(detachForStopLocked);
                return new ValueTask(_stopTask);
            }
        }

        public async ValueTask DisposeResourcesAsync()
        {
            var cleanupFailures = new List<Exception>();
            var factories = Interlocked.Exchange(ref _stoppedFactories, []);
            for (var index = 0; index < factories.Length; index++)
            {
                try { await DisposeFactoryQuietlyAsync(factories[index]).ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            ThrowCleanupFailures(cleanupFailures);
        }

        public static async Task DisposeConnectionAsync(ClientConnection connection)
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) { }
        }

        public static async Task DisposeFactoryQuietlyAsync(IClientTransportFactory factory)
        {
            try { await factory.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException) { }
        }

        public async Task DisposeCreatedFactoriesAsync(
            IEnumerable<DynamicEndpointState> states,
            ISet<IClientTransportFactory>? preservedFactories = null)
        {
            var factories = new HashSet<IClientTransportFactory>(ReferenceEqualityComparer.Instance);
            foreach (var state in states)
            {
                var factory = state.Configuration.TransportFactory;
                if (factories.Add(factory) && (preservedFactories is null || !preservedFactories.Contains(factory)))
                {
                    try { await DisposeFactoryQuietlyAsync(factory).ConfigureAwait(false); }
                    catch (Exception exception)
                    {
                        LogClientBackgroundLoopUnhandledException(
                            _client._logger,
                            nameof(DisposeCreatedFactoriesAsync),
                            exception);
                    }
                }
            }
        }

        private async Task RunResolverWorkerAsync(bool resolveBeforeWatch)
        {
            var delayMilliseconds = 100;
            var mustResolve = resolveBeforeWatch;
            while (!IsStopping && !_client._shutdownCts.IsCancellationRequested)
            {
                if (mustResolve)
                {
                    try
                    {
                        var snapshot = await _resolver.ResolveAsync(_client._shutdownCts.Token).ConfigureAwait(false);
                        if (await _applySnapshotAsync(snapshot).ConfigureAwait(false))
                            delayMilliseconds = 100;
                        mustResolve = false;
                        _updateClientReadiness();
                    }
                    catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        SharpLinkTelemetry.RecordClientResolverFailure();
                        LogClientResolverUpdateFailed(_client._logger, nameof(RunResolverWorkerAsync), exception);
                        await DelayResolverRetryAsync(delayMilliseconds).ConfigureAwait(false);
                        delayMilliseconds = Math.Min(delayMilliseconds * 2, 30_000);
                        continue;
                    }
                }

                try
                {
                    await foreach (var snapshot in _resolver.WatchAsync(_client._shutdownCts.Token)
                                       .WithCancellation(_client._shutdownCts.Token)
                                       .ConfigureAwait(false))
                    {
                        if (IsStopping)
                            return;
                        if (await _applySnapshotAsync(snapshot).ConfigureAwait(false))
                            delayMilliseconds = 100;
                        _updateClientReadiness();
                    }
                    mustResolve = true;
                }
                catch (OperationCanceledException) when (_client._shutdownCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    SharpLinkTelemetry.RecordClientResolverFailure();
                    LogClientResolverUpdateFailed(_client._logger, nameof(RunResolverWorkerAsync), exception);
                    mustResolve = true;
                }

                await DelayResolverRetryAsync(delayMilliseconds).ConfigureAwait(false);
                delayMilliseconds = Math.Min(delayMilliseconds * 2, 30_000);
            }
        }

        private async Task DelayResolverRetryAsync(int delayMilliseconds)
        {
            await Task.Delay(
                    _client._reconnectJitter.ScaleTwentyPercent(delayMilliseconds),
                    _client._runtimeContext.TimeProvider,
                    _client._shutdownCts.Token)
                .ConfigureAwait(false);
        }

        private async Task StopCoreAsync(Func<DynamicClusterStopSnapshot> detachForStopLocked)
        {
            Interlocked.Exchange(ref _stopping, 1);
            var cleanupFailures = new List<Exception>();
            DynamicClusterStopSnapshot snapshot;
            lock (_gate)
            {
                snapshot = detachForStopLocked();
                _stoppedFactories = snapshot.Factories;
            }

            if (Interlocked.Exchange(ref _resolverDisposed, 1) == 0)
            {
                try { await _resolver.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }

            var stopping = CreateConnectionClosedException("Client is stopping.");
            for (var index = 0; index < snapshot.Connections.Length; index++)
            {
                snapshot.Connections[index].Fail(stopping);
                try { await DisposeConnectionAsync(snapshot.Connections[index]).ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            ThrowCleanupFailures(cleanupFailures);
        }

        private static void ThrowCleanupFailures(List<Exception> failures)
        {
            if (failures.Count == 0)
                return;
            if (failures.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            throw new AggregateException(failures);
        }
    }

    private readonly record struct DynamicClusterStopSnapshot(
        ClientConnection[] Connections,
        IClientTransportFactory[] Factories);
}
