using System.Reflection;
using Microsoft.Extensions.Logging;
using SharpLink.Client;
using SharpLink.UnitTests.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Validation;

[Explicit]
public sealed class AdmissionDiagnosticsValidationProbe
{
    [Test]
    public async Task Run()
    {
        var scenario = Environment.GetEnvironmentVariable("SHARPLINK_VALIDATION_SCENARIO");
        PendingLifecycleValidationProbe.Require(scenario is "logger-control" or "logger-throw",
            "Use eng/validate-pending-lifecycle.py with an isolated process.");
        var policy = new ThrowingReportPolicy();
        using var logs = new ProbeLoggerFactory(scenario == "logger-throw");
        var endpoint = SharpLinkClientRetrySharedSupport.Endpoint("diagnostic-probe", 5001);
        var transport = new TestClientTransportFactory();
        await using var client = ClientBuilderTestHelper.BuildEndpoint(endpoint, transport, builder =>
        {
            builder.UseEndpointAdmission(policy);
            builder.UseLoggerFactory(logs);
        });
        var method = SharpLinkClientCircuitBreakerSupport.BreakerMethod();
        var candidate = new SharpLinkEndpointCandidate(endpoint, 1, 0, generation: 1);
        var outcomeType = typeof(SharpLinkClient).GetNestedType("AttemptOutcomeState", BindingFlags.NonPublic)!;
        var observer = (IPendingCallCompletionObserver)Activator.CreateInstance(outcomeType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object[] { client, method }, null)!;
        var allowed = (bool)outcomeType.GetMethod("TryAcquire")!.Invoke(observer, new object[] { candidate })!;
        PendingLifecycleValidationProbe.Require(allowed && policy.Acquires == 1, "real admission lease not acquired");
        using var table = PendingRequestTableTestFixture.Create(1);
        // Inspect the original IValueTaskSource status, not AsTask's asynchronously
        // scheduled continuation. Otherwise a healthy response can look incomplete.
        var operation = table.Rent(Int32Codec.Instance, PendingCallKind.Unary, default,
            CancellationToken.None, out var id, completionObserver: observer).AsValueTask();
        var slots = (Array)typeof(PendingRequestTable).GetField("_slots",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;
        var original = slots.GetValue(0)!;
        string? escaped = null;
        var payload = new ReadOnlySequence<byte>(new byte[sizeof(int)]);
        try
        {
            PendingLifecycleValidationProbe.Require(table.Dispatch(id, ref payload), "response not dispatched");
        }
        catch (ProbeLoggerException exception)
        {
            escaped = exception.GetType().Name;
        }
        // Inspect before the next Rent: healthy completion clears these fields on return.
        // An incomplete orphan operation is deliberately NOT awaited: only this child
        // owns it and process exit bounds the leak without modifying production state.
        var completed = operation.IsCompleted;
        var returned = (long)original.GetType().GetProperty("Id")!.GetValue(original)! == 0 &&
            original.GetType().GetProperty("Operation")!.GetValue(original) is null;
        if (completed)
            PendingLifecycleValidationProbe.Require(await operation == 0, "response value changed");
        var countAfter = table.Count;
        var activeAfter = table.ActiveCount;
        var next = table.Rent<int>(out var nextId).AsValueTask().AsTask();
        payload = new ReadOnlySequence<byte>(new byte[sizeof(int)]);
        PendingLifecycleValidationProbe.Require(table.Dispatch(nextId, ref payload), "next response not dispatched");
        var nextSucceeded = await next == 0;
        PendingLifecycleValidationProbe.Write(new
        {
            phase = "complete",
            scenario,
            escaped,
            policyAcquires = policy.Acquires,
            policyReports = policy.Reports,
            loggerReports = logs.Logger.Reports,
            completed,
            returned,
            countAfter,
            activeAfter,
            nextSucceeded,
            connectionsOpened = transport.ConnectCount,
            invariant = completed && returned && escaped is null && nextSucceeded
        });
    }

    private sealed class ThrowingReportPolicy : ISharpLinkEndpointAdmissionPolicy
    {
        internal int Acquires { get; private set; }
        internal int Reports { get; private set; }
        public SharpLinkEndpointAdmissionDecision TryAcquire(
            in SharpLinkEndpointCandidate endpoint, in RpcMethodDescriptor method)
        {
            Acquires++;
            return new SharpLinkEndpointAdmissionDecision(true, 42, null);
        }
        public void Report(in SharpLinkEndpointOutcome outcome, long token)
        {
            PendingLifecycleValidationProbe.Require(token == 42 && outcome.Kind == SharpLinkEndpointOutcomeKind.Success,
                "outcome/token differs from the real successful pending response");
            Reports++;
            throw new ProbeReportException();
        }
    }

    private sealed class ProbeLoggerFactory(bool shouldThrow) : ILoggerFactory
    {
        internal ProbeLogger Logger { get; } = new(shouldThrow);
        public ILogger CreateLogger(string categoryName) => Logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class ProbeLogger(bool shouldThrow) : ILogger
    {
        internal int Reports { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not ProbeReportException) return;
            Reports++;
            if (shouldThrow) throw new ProbeLoggerException();
        }
    }

    private sealed class ProbeReportException : Exception;
    private sealed class ProbeLoggerException : Exception;
}
