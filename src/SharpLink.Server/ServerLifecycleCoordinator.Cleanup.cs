namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    internal sealed partial class ServerLifecycleCoordinator
    {
        private async Task SendGoAwayToAllAsync()
        {
            var connections = _server._connectionRegistry.SnapshotActive();
            var tasks = new Task[connections.Length];
            for (var index = 0; index < connections.Length; index++)
            {
                var connection = connections[index];
                connection.MarkDraining();
                tasks[index] = SendGoAwayAsync(connection);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private static async Task SendGoAwayAsync(ServerConnectionState connection)
        {
            try
            {
                await connection.Session.SendGoAwayAsync(
                    connection.LastAcceptedRequestId,
                    SharpLinkErrorCode.Unavailable,
                    "Server is draining.").ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is SharpLinkException or System.IO.IOException or ObjectDisposedException)
            {
            }
        }

        private async Task FlushAllSessionsAsync()
        {
            var connections = _server._connectionRegistry.SnapshotActive();
            var tasks = new Task[connections.Length];
            for (var index = 0; index < connections.Length; index++)
                tasks[index] = FlushSessionAsync(connections[index]);
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private static async Task FlushSessionAsync(ServerConnectionState connection)
        {
            try
            {
                await connection.Session.FlushSendQueueAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is SharpLinkException or System.IO.IOException or ObjectDisposedException)
            {
            }
        }

        private async Task DisposeAllSessionsAsync()
        {
            var connections = _server._connectionRegistry.SnapshotActive();
            var tasks = new Task[connections.Length];
            for (var index = 0; index < connections.Length; index++)
                tasks[index] = _server.DisconnectConnectionAsync(connections[index]).AsTask();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                ThrowUnexpectedShutdownTaskFailures(tasks);
            }
        }

        private async Task DisposeServicesWhenDrainedAsync(Task callsDrained)
        {
            try
            {
                await callsDrained.ConfigureAwait(false);
                await DisposeRegisteredServicesAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SharpLinkServer.LogDeferredCleanupFailed(_server._logger, "Services", exception);
            }
        }

        private async Task DisposeRegisteredServicesAsync()
        {
            List<Exception>? failures = null;
            try
            {
                await _server.ReleaseDrainedDynamicModulesAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                await _server._serviceCleanup.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            if (_server._admissionController is not null)
            {
                try
                {
                    await _server._admissionController.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            try
            {
                _server._runtimeContext.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            if (failures is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is not null)
                throw new AggregateException(failures);
        }

        private Task<bool> WaitUntilWithRuntimeTimeAsync(Task task, long deadline)
            => WaitUntilWithProviderAsync(task, deadline, _server._runtimeContext.TimeProvider);

        private static async Task<bool> WaitUntilWithProviderAsync(
            Task task,
            long deadline,
            TimeProvider timeProvider)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return true;
            }

            var remaining = SharpLinkTime.GetRemaining(
                deadline,
                timeProvider.GetTimestamp(),
                timeProvider.TimestampFrequency);
            if (remaining <= TimeSpan.Zero)
                return false;
            return await SharpLinkTimer.WaitAsync(task, remaining, timeProvider).ConfigureAwait(false);
        }

        private static Task StartListenerDispose(IServerTransportListener listener)
        {
            try
            {
                return listener.DisposeAsync().AsTask();
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }

        private void CancelForShutdown(CancellationTokenSource cancellation, string cleanupName)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (Exception exception)
            {
                SharpLinkServer.LogDeferredCleanupFailed(_server._logger, cleanupName, exception);
            }
        }

        private async Task ObserveShutdownAndDisposeTokensAsync(Task shutdownTask)
        {
            try
            {
                await shutdownTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SharpLinkServer.LogDeferredCleanupFailed(_server._logger, "Framework", exception);
            }
            finally
            {
                _acceptCts.Dispose();
                _forceStopCts.Dispose();
            }
        }

        private async Task ObserveCleanupFailureAsync(Task cleanupTask, string cleanupName)
        {
            try
            {
                await cleanupTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SharpLinkServer.LogDeferredCleanupFailed(_server._logger, cleanupName, exception);
            }
        }

        private static void AddTaskFailures(
            ref List<Exception>? failures,
            Task task,
            Exception fallback)
        {
            if (task.Exception is not { } aggregate)
            {
                (failures ??= []).Add(fallback);
                return;
            }

            foreach (var exception in aggregate.Flatten().InnerExceptions)
                (failures ??= []).Add(exception);
        }

        private static Exception? CreateTerminalFailure(
            bool faulted,
            List<Exception>? failures)
        {
            if (failures is { Count: 1 })
                return failures[0];
            if (failures is { Count: > 1 })
                return new AggregateException(failures);
            return faulted
                ? new SharpLinkException(
                    SharpLinkErrorCode.Internal,
                    "Server reached a faulted terminal state during shutdown.")
                : null;
        }

        private static void ThrowUnexpectedShutdownTaskFailures(Task[] tasks)
        {
            List<Exception>? unexpected = null;
            for (var taskIndex = 0; taskIndex < tasks.Length; taskIndex++)
            {
                if (tasks[taskIndex].Exception is not { } aggregate)
                    continue;
                foreach (var exception in aggregate.Flatten().InnerExceptions)
                {
                    if (SharpLinkServer.IsExpectedSessionShutdownException(exception))
                        continue;
                    (unexpected ??= []).Add(exception);
                }
            }

            if (unexpected is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unexpected[0]).Throw();
            if (unexpected is not null)
                throw new AggregateException(unexpected);
        }
    }
}
