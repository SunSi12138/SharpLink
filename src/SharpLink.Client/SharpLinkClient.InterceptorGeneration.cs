namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private sealed class ClientInterceptorGeneration
    {
        private static readonly SharpLinkClientInvocationDelegate Terminal = InvokeTerminalAsync;

        private ClientInterceptorGeneration(int count, SharpLinkClientInvocationDelegate entry)
        {
            Count = count;
            Entry = entry;
        }

        public int Count { get; }
        public SharpLinkClientInvocationDelegate Entry { get; }

        public static ClientInterceptorGeneration Create(ISharpLinkClientInterceptor[] snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            SharpLinkClientInvocationDelegate next = Terminal;
            for (var index = snapshot.Length - 1; index >= 0; index--)
            {
                var node = new ClientInterceptorNode(snapshot[index], next);
                next = node.InvokeAsync;
            }
            return new ClientInterceptorGeneration(snapshot.Length, next);
        }

        private static ValueTask<SharpLinkClientInvocationResult> InvokeTerminalAsync(
            SharpLinkClientInvocationContext context)
            => GetState(context).InvokeComposedTerminalAsync(context);

        private static ClientInterceptorState GetState(SharpLinkClientInvocationContext context)
            => context.InterceptorPipelineState as ClientInterceptorState
                ?? throw new InvalidOperationException("The Client interceptor pipeline state is unavailable.");

        private sealed class ClientInterceptorNode(
            ISharpLinkClientInterceptor interceptor,
            SharpLinkClientInvocationDelegate next)
        {
            public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
                SharpLinkClientInvocationContext context)
                => GetState(context).InvokeComposedInterceptorAsync(interceptor, next, context);
        }
    }
}
