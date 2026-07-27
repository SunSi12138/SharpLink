using Microsoft.Extensions.DependencyInjection;
using SharpLink.Server;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class SharpLinkServerInvocationTests
{
    [Test]
    [NotInParallel]
    public async Task CallAdmissionShouldNotCrossTheServerDrainBoundary()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        var input = new System.IO.Pipelines.Pipe();
        var output = new System.IO.Pipelines.Pipe();
        await using var session = new RpcSession(
            "admission-drain-race",
            input.Reader,
            output.Writer,
            static () => { },
            static () => true);
        var connection = new ServerConnectionState(
            session,
            new RuntimeConcurrencyOptions(),
            CancellationToken.None);
        Ensure(connection.MarkReady(null), "connection ready");

        var tryAcquire = CreatePrivateCall<Func<SharpLinkServer, ServerConnectionState, int>>(
            typeof(SharpLinkServer).GetMethod(
                "TryAcquireCall",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find Server call admission path"));
        var setState = CreateInterlockedInt32Setter<SharpLinkServer>("_state");
        var globalActiveCalls = typeof(SharpLinkServer).GetField(
            "_globalActiveCalls",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find global active-call counter");
        var connectionActiveCalls = typeof(ServerConnectionState).GetField(
            "_activeCalls",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find connection active-call counter");

        const int running = 2;
        const int draining = 3;
        const int acquired = 0;
        const int delayVariants = 96;
        const int iterationsPerDelay = 2_000;
        using var phase = new Barrier(2);
        var admissionResult = -1;
        var witnessedLateAdmission = false;
        var worker = new Thread(() =>
        {
            for (var delay = 0; delay < delayVariants; delay++)
            {
                for (var iteration = 0; iteration < iterationsPerDelay; iteration++)
                {
                    phase.SignalAndWait();
                    admissionResult = tryAcquire(server, connection);
                    phase.SignalAndWait();
                }
            }
        })
        {
            IsBackground = true,
            Name = "SharpLink admission/drain race probe"
        };
        worker.Start();

        for (var delay = 0; delay < delayVariants; delay++)
        {
            for (var iteration = 0; iteration < iterationsPerDelay; iteration++)
            {
                setState(server, running);
                globalActiveCalls.SetValue(server, 0);
                connectionActiveCalls.SetValue(connection, 0);
                admissionResult = -1;
                phase.SignalAndWait();
                Thread.SpinWait(delay);
                setState(server, draining);
                var drainObservedZeroCalls = (int)globalActiveCalls.GetValue(server)! == 0;
                phase.SignalAndWait();
                if (drainObservedZeroCalls && admissionResult == acquired)
                    witnessedLateAdmission = true;
            }
        }
        worker.Join();

        globalActiveCalls.SetValue(server, 0);
        connectionActiveCalls.SetValue(connection, 0);
        setState(server, draining);
        Ensure(!witnessedLateAdmission,
            "Stop observed zero active calls but a racing request was still admitted after the drain boundary");
        Ensure((int)globalActiveCalls.GetValue(server)! == 0, "global active-call counter rollback");
        Ensure(connection.ActiveCalls == 0, "connection active-call counter rollback");
        await connection.CloseAsync();
    }

    [Test]
    public async Task FailedInvocationShouldPreserveLeaseCleanupFailure()
    {
        await using var server = SharpLinkServerBuilder.Create()
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        await using var session = new RpcSession(new TestTransportConnection());
        var lease = new ServiceLease(
            new ThrowingService(),
            new ThrowingScope(),
            disposeService: true);
        var method = typeof(SharpLinkServer).GetMethod(
            "InvokeServiceWithLeaseAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find leased invocation path");

        Exception failure;
        try
        {
            var invocation = (ValueTask)method.Invoke(server,
            [
                new ThrowingStub(),
                lease,
                session,
                1L,
                1L,
                ReadOnlySequence<byte>.Empty,
                null,
                CancellationToken.None,
                new SharpLinkCallContextSnapshot(session.Id, authentication: null),
                false
            ])!;
            await invocation;
            throw new Exception("expected leased invocation failure");
        }
        catch (Exception exception)
        {
            failure = exception is TargetInvocationException { InnerException: { } inner }
                ? inner
                : exception;
        }

        Ensure(ContainsMessage(failure, "handler failed"),
            "leased invocation must retain the handler failure");
        Ensure(ContainsMessage(failure, "lease cleanup failed"),
            "leased invocation must retain the lease cleanup failure");
    }

    private static bool ContainsMessage(Exception exception, string message)
    {
        if (exception.Message == message)
            return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (ContainsMessage(inner, message))
                    return true;
            }
            return false;
        }
        return exception.InnerException is { } nested && ContainsMessage(nested, message);
    }

    private static TDelegate CreatePrivateCall<TDelegate>(MethodInfo method)
        where TDelegate : Delegate
    {
        var invoke = typeof(TDelegate).GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(static parameter => parameter.ParameterType).ToArray();
        var dynamicMethod = new DynamicMethod(
            $"Call_{method.Name}",
            invoke.ReturnType,
            parameters,
            typeof(SharpLinkServerInvocationTests).Module,
            skipVisibility: true);
        var generator = dynamicMethod.GetILGenerator();
        for (var index = 0; index < parameters.Length; index++)
            generator.Emit(OpCodes.Ldarg, index);
        generator.Emit(OpCodes.Call, method);
        generator.Emit(OpCodes.Ret);
        return dynamicMethod.CreateDelegate<TDelegate>();
    }

    private static Action<TTarget, int> CreateInterlockedInt32Setter<TTarget>(string fieldName)
    {
        var field = typeof(TTarget).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find field {fieldName}");
        var dynamicMethod = new DynamicMethod(
            $"Set_{fieldName}",
            typeof(void),
            [typeof(TTarget), typeof(int)],
            typeof(SharpLinkServerInvocationTests).Module,
            skipVisibility: true);
        var generator = dynamicMethod.GetILGenerator();
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldflda, field);
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Call, typeof(Interlocked).GetMethod(
            nameof(Interlocked.Exchange),
            [typeof(int).MakeByRefType(), typeof(int)])!);
        generator.Emit(OpCodes.Pop);
        generator.Emit(OpCodes.Ret);
        return dynamicMethod.CreateDelegate<Action<TTarget, int>>();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;

        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingService : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
            => ValueTask.FromException(new InvalidOperationException("lease cleanup failed"));
    }

    private sealed class ThrowingScope : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } = new EmptyServiceProvider();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class ThrowingStub : IRpcStub
    {
        public long InterfaceHash => 1;

        public ValueTask InvokeNoReturnAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => Fail();

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken) => Fail();

        public ValueTask InvokeAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output) => Fail();

        public ValueTask InvokeCancellableAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output,
            CancellationToken cancellationToken) => Fail();

        private static ValueTask Fail()
            => ValueTask.FromException(new InvalidOperationException("handler failed"));
    }
}
