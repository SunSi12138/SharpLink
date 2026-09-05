namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private sealed class ServerInterceptorGeneration
    {
        private static readonly SharpLinkServerInvocationDelegate Terminal = InvokeTerminalAsync;

        private ServerInterceptorGeneration(int count, SharpLinkServerInvocationDelegate entry)
        {
            Count = count;
            Entry = entry;
        }

        public int Count { get; }
        public SharpLinkServerInvocationDelegate Entry { get; }

        public static ServerInterceptorGeneration Create(ISharpLinkServerInterceptor[] snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            SharpLinkServerInvocationDelegate next = Terminal;
            for (var index = snapshot.Length - 1; index >= 0; index--)
            {
                var node = new ServerInterceptorNode(snapshot[index], next);
                next = node.InvokeAsync;
            }
            return new ServerInterceptorGeneration(snapshot.Length, next);
        }

        private static async ValueTask InvokeTerminalAsync(SharpLinkServerInvocationContext context)
        {
            context.InterceptorTerminalReached = true;
            var stub = context.InterceptorStub as IRpcStub
                ?? throw new InvalidOperationException("The Server interceptor terminal stub is unavailable.");
            var service = context.InterceptorService
                ?? throw new InvalidOperationException("The Server interceptor terminal service is unavailable.");
            var generatedBridge = context.InterceptorGeneratedBridge as IRpcGeneratedServerBridge
                ?? throw new InvalidOperationException("The Server interceptor generated bridge is unavailable.");
            var output = context.InterceptorOutput as IRpcByteBufferWriter;
            var timeProvider = context.InterceptorTimeProvider
                ?? throw new InvalidOperationException("The Server interceptor time provider is unavailable.");

            try
            {
                if (output is null)
                {
                    await stub.InvokeNoReturnCancellableAsync(
                        service, generatedBridge, context.InterceptorMethodId, context.RequestId,
                        context.InterceptorArguments, context.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await stub.InvokeCancellableAsync(
                        service, generatedBridge, context.InterceptorMethodId, context.RequestId,
                        context.InterceptorArguments, output, context.CancellationToken).ConfigureAwait(false);
                }
                if (context.Status == SharpLinkInvocationStatus.Pending)
                    context.Status = SharpLinkInvocationStatus.Succeeded;
            }
            catch (Exception exception)
            {
                RecordInvocationFailure(context, exception);
                throw;
            }
            finally
            {
                context.Elapsed = timeProvider.GetElapsedTime(context.InterceptorStarted);
            }
        }

        private sealed class ServerInterceptorNode(
            ISharpLinkServerInterceptor interceptor,
            SharpLinkServerInvocationDelegate next)
        {
            public ValueTask InvokeAsync(SharpLinkServerInvocationContext context)
            {
                try
                {
                    var generatedBridge = context.InterceptorGeneratedBridge as IRpcGeneratedServerBridge
                        ?? throw new InvalidOperationException("The Server interceptor generated bridge is unavailable.");
                    generatedBridge.EnsureUserCodeEntry(context.RequestId);
                    return interceptor.InvokeAsync(context, next);
                }
                catch (Exception exception)
                {
                    return ValueTask.FromException(exception);
                }
            }
        }
    }
}
