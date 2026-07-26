using Microsoft.Extensions.DependencyInjection;
using SharpLink.Server;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public class ServiceRegistrationTests
{
    [Test]
    public async Task ActivationRollbackShouldPreserveActivationAndScopeFailures()
    {
        var registration = ServiceRegistration.CreatePerCall(
            typeof(object),
            new StubMarker(),
            new ThrowingScopeFactory("scope cleanup failed"),
            static _ => throw new InvalidOperationException("activation failed"),
            disposeService: true);

        var failure = await CaptureAsync(() => registration.AcquireAsync(null!, isStream: false));

        Ensure(ContainsMessage(failure, "activation failed"),
            "activation rollback must retain the primary activation failure");
        Ensure(ContainsMessage(failure, "scope cleanup failed"),
            "activation rollback must retain the scope cleanup failure");
    }

    [Test]
    public async Task ConnectionActivationRollbackShouldPreserveActivationAndScopeFailures()
    {
        var registration = ServiceRegistration.CreateConnection(
            typeof(object),
            new StubMarker(),
            new ThrowingScopeFactory("connection activation scope cleanup failed"),
            static _ => throw new InvalidOperationException("connection activation failed"),
            disposeService: true);

        var failure = await CaptureAsync(registration.CreateConnectionServiceAsync);

        Ensure(ContainsMessage(failure, "connection activation failed"),
            "connection activation rollback must retain the primary activation failure");
        Ensure(ContainsMessage(failure, "connection activation scope cleanup failed"),
            "connection activation rollback must retain the scope cleanup failure");
    }

    [Test]
    public async Task ServiceLeaseShouldPreserveServiceAndScopeDisposalFailures()
    {
        var lease = new ServiceLease(
            new ThrowingAsyncDisposable("service disposal failed"),
            new ThrowingScope("scope disposal failed"),
            disposeService: true);

        var failure = await CaptureAsync(lease.DisposeAsync);

        Ensure(ContainsMessage(failure, "service disposal failed"),
            "lease cleanup must retain the service disposal failure");
        Ensure(ContainsMessage(failure, "scope disposal failed"),
            "lease cleanup must retain the scope disposal failure");
    }

    [Test]
    public async Task ConnectionServiceShouldPreserveServiceAndScopeDisposalFailures()
    {
        var instance = new ConnectionServiceInstance(
            new ThrowingAsyncDisposable("connection service disposal failed"),
            new ThrowingScope("connection scope disposal failed"),
            disposeService: true);

        var failure = await CaptureAsync(instance.DisposeAsync);

        Ensure(ContainsMessage(failure, "connection service disposal failed"),
            "connection cleanup must retain the service disposal failure");
        Ensure(ContainsMessage(failure, "connection scope disposal failed"),
            "connection cleanup must retain the scope disposal failure");
    }

    private static async Task<Exception> CaptureAsync(Func<ValueTask> action)
    {
        try
        {
            await action();
            throw new Exception("expected operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception> CaptureAsync<T>(Func<ValueTask<T>> action)
    {
        try
        {
            await action();
            throw new Exception("expected operation to fail");
        }
        catch (Exception exception)
        {
            return exception;
        }
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class ThrowingScopeFactory(string message) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new ThrowingScope(message);
    }

    private sealed class ThrowingScope(string message) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } = new EmptyServiceProvider();

        public void Dispose() => throw new InvalidOperationException(message);

        public ValueTask DisposeAsync()
            => ValueTask.FromException(new InvalidOperationException(message));
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class ThrowingAsyncDisposable(string message) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
            => ValueTask.FromException(new InvalidOperationException(message));
    }

    private sealed class StubMarker : IRpcStub
    {
        public long InterfaceHash => 1;

        public ValueTask InvokeNoReturnAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;

        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask InvokeAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output)
            => ValueTask.CompletedTask;

        public ValueTask InvokeCancellableAsync(object service, IRpcSession session, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
