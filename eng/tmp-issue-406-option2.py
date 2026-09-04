from pathlib import Path


def read(path: str) -> str:
    return Path(path).read_text()


def write(path: str, text: str) -> None:
    Path(path).write_text(text)


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected one occurrence, found {count}: {old[:120]!r}")
    write(path, text.replace(old, new, 1))


def replace_all(path: str, old: str, new: str, minimum: int = 1) -> None:
    text = read(path)
    count = text.count(old)
    if count < minimum:
        raise SystemExit(f"{path}: expected at least {minimum} occurrence(s), found {count}: {old[:120]!r}")
    write(path, text.replace(old, new))


# -----------------------------------------------------------------------------
# Abstractions: keep correct-use documentation, but stop promising runtime
# one-shot enforcement. Internal state stays friend-visible only.
# -----------------------------------------------------------------------------
abstractions = "src/SharpLink.Abstractions/SharpLinkInterceptors.cs"
replace_once(
    abstractions,
    "    /// <summary>Gets elapsed time after the pipeline completes.</summary>\n    public TimeSpan Elapsed { get; internal set; }\n}\n\n/// <summary>Represents the boxed terminal result",
    "    /// <summary>Gets elapsed time after the pipeline completes.</summary>\n    public TimeSpan Elapsed { get; internal set; }\n\n    internal object? InterceptorPipelineState { get; set; }\n}\n\n/// <summary>Represents the boxed terminal result")
replace_once(
    abstractions,
    "/// <summary>Continues a client interceptor pipeline. Each delegate instance may be invoked once.</summary>",
    "/// <summary>Continues a client interceptor pipeline. Interceptors should invoke, await, or return this continuation at most once and must not retain it after the interceptor returns.</summary>")
replace_once(
    abstractions,
    "        CancellationToken cancellationToken,\n        ISharpLinkServerInterceptor[]? interceptors = null)",
    "        CancellationToken cancellationToken,\n        object? interceptorGeneration = null)")
replace_once(abstractions, "        Interceptors = interceptors;", "        InterceptorGeneration = interceptorGeneration;")
replace_once(
    abstractions,
    "    internal ISharpLinkServerInterceptor[]? Interceptors { get; }",
    "    internal object? InterceptorGeneration { get; }\n    internal object? InterceptorPipelineState { get; set; }\n    internal bool InterceptorTerminalReached { get; set; }")
replace_once(
    abstractions,
    "/// <summary>Continues a server interceptor pipeline. Each delegate instance may be invoked once.</summary>",
    "/// <summary>Continues a server interceptor pipeline. Interceptors should invoke and await this continuation at most once and must not retain it after the interceptor returns.</summary>")


# -----------------------------------------------------------------------------
# Client immutable generation + publication/capture.
# -----------------------------------------------------------------------------
client_generation = r'''namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    private sealed class ClientInterceptorGeneration
    {
        private static readonly SharpLinkClientInvocationDelegate Terminal = InvokeTerminalAsync;

        private ClientInterceptorGeneration(
            ISharpLinkClientInterceptor[] snapshot,
            SharpLinkClientInvocationDelegate entry)
        {
            Snapshot = snapshot;
            Entry = entry;
        }

        public ISharpLinkClientInterceptor[] Snapshot { get; }
        public int Count => Snapshot.Length;
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
            return new ClientInterceptorGeneration(snapshot, next);
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
'''
write("src/SharpLink.Client/SharpLinkClient.InterceptorGeneration.cs", client_generation)

client_main = "src/SharpLink.Client/SharpLinkClient.cs"
replace_once(client_main, "    private ISharpLinkClientInterceptor[] _clientInterceptors;", "    private ClientInterceptorGeneration _clientInterceptorGeneration;")
replace_once(client_main, "        _clientInterceptors = composition.Interceptors;", "        _clientInterceptorGeneration = ClientInterceptorGeneration.Create(composition.Interceptors);")

client_runtime = "src/SharpLink.Client/SharpLinkClient.RuntimeInterceptors.cs"
replace_once(client_runtime, "        var candidate = CreateInterceptorSnapshot(interceptors);", "        var candidate = ClientInterceptorGeneration.Create(CreateInterceptorSnapshot(interceptors));")
replace_once(client_runtime, "                Volatile.Write(ref _clientInterceptors, candidate);", "                Volatile.Write(ref _clientInterceptorGeneration, candidate);")

client_invokers = "src/SharpLink.Client/SharpLinkClient.Invokers.cs"
replace_all(client_invokers, "Volatile.Read(ref _clientInterceptors)", "Volatile.Read(ref _clientInterceptorGeneration)", minimum=5)
replace_all(client_invokers, "interceptors.Length", "interceptors.Count", minimum=5)

client_telemetry = "src/SharpLink.Client/SharpLinkClient.Telemetry.cs"
replace_all(client_telemetry, "ISharpLinkClientInterceptor[] interceptors", "ClientInterceptorGeneration interceptors", minimum=5)
replace_all(client_telemetry, "interceptors.Length", "interceptors.Count", minimum=5)

client_interceptors = "src/SharpLink.Client/SharpLinkClient.Interceptors.cs"
replace_all(client_interceptors, "ISharpLinkClientInterceptor[] interceptors", "ClientInterceptorGeneration interceptors", minimum=10)
replace_once(client_interceptors, "        private readonly ISharpLinkClientInterceptor[] _interceptors;", "        private readonly ClientInterceptorGeneration _interceptors;")
replace_once(
    client_interceptors,
    "            _context = new SharpLinkClientInvocationContext(\n                method, request, _control.Metadata, cancellationToken);",
    "            _context = new SharpLinkClientInvocationContext(\n                method, request, _control.Metadata, cancellationToken);\n            _context.InterceptorPipelineState = this;")
replace_all(client_interceptors, "InvokeNextAsync(0, _context)", "_interceptors.Entry(_context)", minimum=3)
replace_once(
    client_interceptors,
    "        private ValueTask<SharpLinkClientInvocationResult> InvokeNextAsync(\n            int index,\n            SharpLinkClientInvocationContext context)\n        {",
    "        internal ValueTask<SharpLinkClientInvocationResult> InvokeComposedInterceptorAsync(\n            ISharpLinkClientInterceptor interceptor,\n            SharpLinkClientInvocationDelegate next,\n            SharpLinkClientInvocationContext context)\n        {\n            if (_control.LogicalCall is { } logicalCall && !logicalCall.TryEnterProgress())\n            {\n                return ValueTask.FromException<SharpLinkClientInvocationResult>(\n                    CreateDeadlineExceededException());\n            }\n\n            try\n            {\n                return interceptor.InvokeAsync(context, next);\n            }\n            catch (Exception exception)\n            {\n                return ValueTask.FromException<SharpLinkClientInvocationResult>(exception);\n            }\n        }\n\n        internal ValueTask<SharpLinkClientInvocationResult> InvokeComposedTerminalAsync(\n            SharpLinkClientInvocationContext context)\n        {\n            if (_control.LogicalCall is { } logicalCall && !logicalCall.TryEnterProgress())\n            {\n                return ValueTask.FromException<SharpLinkClientInvocationResult>(\n                    CreateDeadlineExceededException());\n            }\n\n            return InvokeTerminalTrackedAsync(context);\n        }\n\n        private ValueTask<SharpLinkClientInvocationResult> InvokeNextAsync(\n            int index,\n            SharpLinkClientInvocationContext context)\n        {")
replace_all(client_interceptors, "_interceptors.Length", "_interceptors.Count", minimum=1)
replace_all(client_interceptors, "_interceptors[index]", "_interceptors.Snapshot[index]", minimum=1)


# -----------------------------------------------------------------------------
# Server immutable generation + opaque capture in Abstractions context. The first
# correct prototype intentionally turns ServerPipelineFacts into one per-call
# object so its allocation cost is measured rather than hidden.
# -----------------------------------------------------------------------------
server_generation = r'''namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private sealed class ServerInterceptorGeneration
    {
        private static readonly SharpLinkServerInvocationDelegate Terminal = InvokeTerminalAsync;

        private ServerInterceptorGeneration(
            ISharpLinkServerInterceptor[] snapshot,
            SharpLinkServerInvocationDelegate entry)
        {
            Snapshot = snapshot;
            Entry = entry;
        }

        public ISharpLinkServerInterceptor[] Snapshot { get; }
        public int Count => Snapshot.Length;
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
            return new ServerInterceptorGeneration(snapshot, next);
        }

        private static ValueTask InvokeTerminalAsync(SharpLinkServerInvocationContext context)
            => GetState(context).InvokeComposedTerminalAsync(context);

        private static ServerPipelineFacts GetState(SharpLinkServerInvocationContext context)
            => context.InterceptorPipelineState as ServerPipelineFacts
                ?? throw new InvalidOperationException("The Server interceptor pipeline state is unavailable.");

        private sealed class ServerInterceptorNode(
            ISharpLinkServerInterceptor interceptor,
            SharpLinkServerInvocationDelegate next)
        {
            public ValueTask InvokeAsync(SharpLinkServerInvocationContext context)
                => GetState(context).InvokeComposedInterceptorAsync(interceptor, next, context);
        }
    }
}
'''
write("src/SharpLink.Server/SharpLinkServer.InterceptorGeneration.cs", server_generation)

server_main = "src/SharpLink.Server/SharpLinkServer.cs"
replace_once(server_main, "    private ISharpLinkServerInterceptor[] _serverInterceptors;", "    private ServerInterceptorGeneration _serverInterceptorGeneration;")
replace_once(server_main, "        _serverInterceptors = composition.Interceptors;", "        _serverInterceptorGeneration = ServerInterceptorGeneration.Create(composition.Interceptors);")
replace_once(
    server_main,
    "        var interceptors = Volatile.Read(ref _serverInterceptors);\n        if (interceptors.Length == 0)",
    "        var interceptors = Volatile.Read(ref _serverInterceptorGeneration);\n        if (interceptors.Count == 0)")
replace_once(
    server_main,
    "        ISharpLinkServerInterceptor[]? interceptors = null)",
    "        ServerInterceptorGeneration? interceptors = null)")

server_runtime = "src/SharpLink.Server/SharpLinkServer.RuntimeInterceptors.cs"
replace_once(server_runtime, "        var candidate = CreateInterceptorSnapshot(interceptors);", "        var candidate = ServerInterceptorGeneration.Create(CreateInterceptorSnapshot(interceptors));")
replace_once(server_runtime, "            Volatile.Write(ref _serverInterceptors, candidate);", "            Volatile.Write(ref _serverInterceptorGeneration, candidate);")

server_interceptors = "src/SharpLink.Server/SharpLinkServer.Interceptors.cs"
replace_once(
    server_interceptors,
    "        var interceptors = (context as SharpLinkServerInvocationContext)?.Interceptors;\n        if (interceptors is null || interceptors.Length == 0)",
    "        var interceptors = (context as SharpLinkServerInvocationContext)?.InterceptorGeneration as ServerInterceptorGeneration;\n        if (interceptors is null || interceptors.Count == 0)")
replace_all(server_interceptors, "ISharpLinkServerInterceptor[] interceptors", "ServerInterceptorGeneration interceptors", minimum=2)
replace_once(server_interceptors, "    private struct ServerPipelineFacts", "    private sealed class ServerPipelineFacts")
replace_once(server_interceptors, "        private readonly ISharpLinkServerInterceptor[] _interceptors;", "        private readonly ServerInterceptorGeneration _interceptors;")
replace_once(
    server_interceptors,
    "                await InvokeNextAsync(0, context).ConfigureAwait(false);\n                if (context.Status == SharpLinkInvocationStatus.Pending)",
    "                context.InterceptorPipelineState = this;\n                context.InterceptorTerminalReached = false;\n                await _interceptors.Entry(context).ConfigureAwait(false);\n                if (_output is not null && !context.InterceptorTerminalReached)\n                {\n                    throw new InvalidOperationException(\n                        \"A Server interceptor must invoke its continuation for a response-bearing RPC.\");\n                }\n                if (context.Status == SharpLinkInvocationStatus.Pending)")
replace_once(
    server_interceptors,
    "        private ValueTask InvokeNextAsync(int index, SharpLinkServerInvocationContext context)\n        {",
    "        internal ValueTask InvokeComposedInterceptorAsync(\n            ISharpLinkServerInterceptor interceptor,\n            SharpLinkServerInvocationDelegate next,\n            SharpLinkServerInvocationContext context)\n        {\n            try\n            {\n                _generatedBridge.EnsureUserCodeEntry(_requestId);\n                return interceptor.InvokeAsync(context, next);\n            }\n            catch (Exception exception)\n            {\n                return ValueTask.FromException(exception);\n            }\n        }\n\n        internal ValueTask InvokeComposedTerminalAsync(SharpLinkServerInvocationContext context)\n        {\n            context.InterceptorTerminalReached = true;\n            return InvokeTerminalTrackedAsync(context);\n        }\n\n        private ValueTask InvokeNextAsync(int index, SharpLinkServerInvocationContext context)\n        {")
replace_all(server_interceptors, "_interceptors.Length", "_interceptors.Count", minimum=1)
replace_all(server_interceptors, "_interceptors[index]", "_interceptors.Snapshot[index]", minimum=1)


# Guard the intended publication model and ensure old hot-path helpers are no longer called
# from the root execution path.
checks = {
    client_main: ["ClientInterceptorGeneration _clientInterceptorGeneration"],
    client_runtime: ["Volatile.Write(ref _clientInterceptorGeneration, candidate)"],
    client_interceptors: ["_interceptors.Entry(_context)", "InvokeComposedInterceptorAsync", "InvokeComposedTerminalAsync"],
    server_main: ["ServerInterceptorGeneration _serverInterceptorGeneration"],
    server_runtime: ["Volatile.Write(ref _serverInterceptorGeneration, candidate)"],
    server_interceptors: ["await _interceptors.Entry(context)", "InvokeComposedInterceptorAsync", "InterceptorTerminalReached"],
}
for path, tokens in checks.items():
    text = read(path)
    for token in tokens:
        if token not in text:
            raise SystemExit(f"{path}: missing expected token {token!r}")

print("issue 406 option-2 prototype patched")
