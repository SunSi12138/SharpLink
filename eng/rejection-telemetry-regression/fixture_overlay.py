"""Authored, temporary test overlay; never a production instrumentation hook."""


def replace_once(source: str, old: str, new: str) -> str:
    if source.count(old) != 1:
        raise ValueError(f"Expected exactly one source anchor: {old[:100]!r}")
    return source.replace(old, new, 1)


def create_overlay(source: str, *, baseline: bool) -> str:
    start = source.index("    [Test]\n    public async Task RejectionTelemetryRecordsReasonedRejections()")
    end = source.index("    // ------------------------------------------------------------------ server: real TCP", start)
    method = source[start:end]
    method = replace_once(method, "        var rejected = 0L;", """        var rejected = 0L;
        var diagnosticOwnDisposals = 0;
        var diagnosticUnscopedRejected = 0L;
        var diagnosticUnscopedConnectionLimit = 0L;""")
    method = replace_once(method, """        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {""", """        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            Interlocked.Add(ref diagnosticUnscopedRejected, value);
            foreach (var tag in tags)
            {
                if (tag.Key == "sharplink.admission.reason" && Equals(tag.Value, "connection_limit"))
                    Interlocked.Add(ref diagnosticUnscopedConnectionLimit, value);
            }""")
    method = replace_once(method, "        var scripted = new ScriptedListener();", """        var ownAcceptEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownAcceptRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TestConnection? blockedConnection = null;
        var scripted = new ScriptedListener(async (connection, cancellationToken) =>
        {
            if (connection.Id.StartsWith("telemetry-rejected-", StringComparison.Ordinal))
            {
                blockedConnection = (TestConnection)connection;
                blockedConnection.Disposed += () => Interlocked.Increment(ref diagnosticOwnDisposals);
                ownAcceptEntered.TrySetResult();
                await ownAcceptRelease.Task.WaitAsync(cancellationToken);
            }
        });""")
    cut = "        for (var index = 0; index < 3; index++)" if baseline else "        var rejectedConnections = new TestConnection[3];"
    setup = """        // Diagnostic only: another real server publishes while this fixture's
        // three rejected connections cannot yet leave AcceptAsync.
        try
        {
            var sameReason = Environment.GetEnvironmentVariable("SHARPLINK_REJECTION_PROOF_NOISE") == "connection";
            var unrelatedListener = new ScriptedListener();
            await using var unrelated = await StartServerAsync(unrelatedListener, options =>
            {
                options.MaxConcurrentConnections = sameReason ? 1 : 4;
                options.MaxConcurrentHandshakes = 1;
            });
            unrelatedListener.Enqueue(new TestConnection("proof-unrelated-handshake-holder"));
            await YieldUntilAsync(
                () => unrelated.Server.ConnectionAdmission.ActiveHandshakes == 1,
                "the unrelated server must hold its handshake slot");
            var unrelatedRejections = new TestConnection[3];
            for (var index = 0; index < unrelatedRejections.Length; index++)
            {
                unrelatedRejections[index] = new TestConnection($"proof-unrelated-rejected-{index}");
                unrelatedListener.Enqueue(unrelatedRejections[index]);
            }
            await YieldUntilAsync(
                () => Array.TrueForAll(unrelatedRejections, connection => connection.DisposeCount == 1),
                "the unrelated server must emit all three noise rejections");

            async Task VerifyReadinessAtBlockedAcceptAsync(Func<bool> condition, string message)
            {
                await ownAcceptEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var ready = condition();
                lock (reasons)
                    Console.WriteLine($"REJECTION_PROOF phase=cut ready={ready} observed={rejected} ownDisposals={diagnosticOwnDisposals} unscoped={diagnosticUnscopedRejected} unscopedConnectionLimit={diagnosticUnscopedConnectionLimit} reasons={string.Join(',', reasons)}");
                // Same rule on both inputs: baseline incorrectly declares readiness
                // and reaches its original assertion before releasing own accepts.
                if (!ready)
                    ownAcceptRelease.TrySetResult();
                try
                {
                    await YieldUntilAsync(condition, message);
                }
                finally
                {
                    lock (reasons)
                        Console.WriteLine($"REJECTION_PROOF phase=end observed={rejected} ownDisposals={diagnosticOwnDisposals} unscoped={diagnosticUnscopedRejected} unscopedConnectionLimit={diagnosticUnscopedConnectionLimit} reasons={string.Join(',', reasons)}");
                }
            }

"""
    method = replace_once(method, cut, setup + cut)
    wait = "        await YieldUntilAsync(\n            () => Volatile.Read(ref rejected) >= 3," if baseline else "        await YieldUntilAsync(\n            () =>\n            {"
    method = replace_once(method, wait, wait.replace("YieldUntilAsync", "VerifyReadinessAtBlockedAcceptAsync"))
    closing = method.rfind("    }")
    method = method[:closing] + """        }
        finally
        {
            ownAcceptRelease.TrySetResult();
        }
""" + method[closing:]
    source = source[:start] + method + source[end:]
    source = replace_once(source, """    private sealed class ScriptedListener : IServerTransportListener
    {""", """    private sealed class ScriptedListener : IServerTransportListener
    {
        private readonly Func<ITransportConnection, CancellationToken, Task>? _beforeReturn;

        internal ScriptedListener(Func<ITransportConnection, CancellationToken, Task>? beforeReturn = null)
            => _beforeReturn = beforeReturn;
""")
    return replace_once(source, """            var connection = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return connection;""", """            var connection = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (_beforeReturn is not null)
                await _beforeReturn(connection, cancellationToken).ConfigureAwait(false);
            return connection;""")


def omit_own_publication(source: str) -> str:
    return replace_once(source,
        "                        RecordConnectionAdmissionRejection(ConnectionAdmissionRejectionReason.ConnectionLimit);",
        """                        // Diagnostic only: omit exactly this fixture's three publications.
                        if (connection.Id is not ("telemetry-rejected-0" or "telemetry-rejected-1" or "telemetry-rejected-2"))
                            RecordConnectionAdmissionRejection(ConnectionAdmissionRejectionReason.ConnectionLimit);""")
