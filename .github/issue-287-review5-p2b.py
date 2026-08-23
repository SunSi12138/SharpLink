from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    assert count == 1, f"{path}: expected one match, found {count}"
    p.write_text(text.replace(old, new, 1))


# Bound the whole non-streaming interceptor chain, not only the post-await result.
path = "src/SharpLink.Client/SharpLinkClient.Interceptors.cs"
replace_once(
    path,
    "var result = await InvokeNextAsync(0, _context).ConfigureAwait(false);",
    "var result = await AwaitInvocationWithinFrozenDeadlineAsync(\n                    InvokeNextAsync(0, _context)).ConfigureAwait(false);")
# The same source text occurs three times; replace_once only handled the first. Apply to the remaining two.
p = Path(path)
text = p.read_text()
old = "var result = await InvokeNextAsync(0, _context).ConfigureAwait(false);"
assert text.count(old) == 2, f"{path}: expected two remaining direct interceptor awaits"
text = text.replace(
    old,
    "var result = await AwaitInvocationWithinFrozenDeadlineAsync(\n                    InvokeNextAsync(0, _context)).ConfigureAwait(false);")
p.write_text(text)

replace_once(
    path,
    """        private ValueTask<SharpLinkClientInvocationResult> InvokeNextAsync(
            int index,
            SharpLinkClientInvocationContext context)""",
    """        private async ValueTask<SharpLinkClientInvocationResult> AwaitInvocationWithinFrozenDeadlineAsync(
            ValueTask<SharpLinkClientInvocationResult> invocation)
        {
            if (!_control.Deadline.HasValue || invocation.IsCompletedSuccessfully)
                return await invocation.ConfigureAwait(false);

            var invocationTask = invocation.AsTask();
            if (!await SharpLinkTimer.WaitAsync(
                    invocationTask,
                    _control.Deadline,
                    _client._runtimeContext.TimeProvider,
                    CancellationToken.None).ConfigureAwait(false))
            {
                _ = ObserveAbandonedInvocationAsync(invocationTask);
                throw CreateDeadlineExceededException();
            }
            return await invocationTask.ConfigureAwait(false);
        }

        private static async Task ObserveAbandonedInvocationAsync(
            Task<SharpLinkClientInvocationResult> invocationTask)
        {
            try { _ = await invocationTask.ConfigureAwait(false); }
            catch { }
        }

        private ValueTask<SharpLinkClientInvocationResult> InvokeNextAsync(
            int index,
            SharpLinkClientInvocationContext context)""")

# Bound stream creation as well as enumeration, and arbitrate EOF after MoveNext completes.
replace_once(
    path,
    """            ThrowIfDeadlineExpired();
            var stream = (await invocation.ConfigureAwait(false)).GetValue<IAsyncEnumerable<T>>();
            ThrowIfDeadlineExpired();""",
    """            ThrowIfDeadlineExpired();
            var invocationResult = await AwaitInvocationWithinDeadlineAsync(invocation).ConfigureAwait(false);
            ThrowIfDeadlineExpired();
            var stream = invocationResult.GetValue<IAsyncEnumerable<T>>();""")

replace_once(
    path,
    """                    if (!hasNext)
                        yield break;
                    ThrowIfDeadlineExpired();
                    var item = enumerator.Current;""",
    """                    ThrowIfDeadlineExpired();
                    if (!hasNext)
                        yield break;
                    var item = enumerator.Current;""")

replace_once(
    path,
    """        private static async Task ObserveAbandonedMoveNextAsync(Task<bool> task)
        {""",
    """        private async ValueTask<SharpLinkClientInvocationResult> AwaitInvocationWithinDeadlineAsync(
            ValueTask<SharpLinkClientInvocationResult> pendingInvocation)
        {
            if (!deadline.HasValue || pendingInvocation.IsCompletedSuccessfully)
                return await pendingInvocation.ConfigureAwait(false);

            var invocationTask = pendingInvocation.AsTask();
            if (!await SharpLinkTimer.WaitAsync(
                    invocationTask,
                    deadline,
                    timeProvider,
                    CancellationToken.None).ConfigureAwait(false))
            {
                _ = ObserveAbandonedInvocationAsync(invocationTask);
                throw CreateDeadlineExceededException();
            }
            return await invocationTask.ConfigureAwait(false);
        }

        private static async Task ObserveAbandonedInvocationAsync(
            Task<SharpLinkClientInvocationResult> task)
        {
            try { _ = await task.ConfigureAwait(false); }
            catch { }
        }

        private static async Task ObserveAbandonedMoveNextAsync(Task<bool> task)
        {""")

# Server-generated outbound streams use the same call-context monotonic deadline, including
# an in-flight MoveNext/send race, without changing the Generated bridge ABI.
replace_once(
    "src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs",
    """        await foreach (var item in stream
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!payloadNullable && default(T) is null && item is null)
            {
                throw new SharpLinkException(
                    SharpLinkErrorCode.Internal,
                    "A non-nullable RPC stream response was null.");
            }

            await SendGeneratedStreamChunkAsync(
                requestId,
                streamId,
                item,
                codec,
                cancellationToken).ConfigureAwait(false);
        }

        this.SendStreamCompleteAsync(requestId, streamId);""",
    """        var callContext = SharpLinkCallContext.Current;
        var deadline = callContext?.LocalRpcDeadline ?? default;
        var deadlineTimeProvider = callContext?.DeadlineTimeProvider;
        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = stream.GetAsyncEnumerator(lifetimeCancellation.Token);
        var deadlineWon = false;
        try
        {
            while (true)
            {
                ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
                var moveNext = enumerator.MoveNextAsync();
                bool hasNext;
                if (!deadline.HasValue || deadlineTimeProvider is null || moveNext.IsCompletedSuccessfully)
                {
                    hasNext = await moveNext.ConfigureAwait(false);
                }
                else
                {
                    var moveNextTask = moveNext.AsTask();
                    if (!await SharpLinkTimer.WaitAsync(
                            moveNextTask,
                            deadline,
                            deadlineTimeProvider,
                            lifetimeCancellation.Token).ConfigureAwait(false))
                    {
                        deadlineWon = true;
                        lifetimeCancellation.Cancel();
                        _ = ObserveAbandonedGeneratedMoveNextAsync(moveNextTask);
                        throw CreateGeneratedStreamDeadlineExceededException();
                    }
                    hasNext = await moveNextTask.ConfigureAwait(false);
                }

                ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
                if (!hasNext)
                    break;

                var item = enumerator.Current;
                if (!payloadNullable && default(T) is null && item is null)
                {
                    throw new SharpLinkException(
                        SharpLinkErrorCode.Internal,
                        "A non-nullable RPC stream response was null.");
                }

                var send = SendGeneratedStreamChunkAsync(
                    requestId,
                    streamId,
                    item,
                    codec,
                    lifetimeCancellation.Token);
                if (!deadline.HasValue || deadlineTimeProvider is null || send.IsCompletedSuccessfully)
                {
                    await send.ConfigureAwait(false);
                }
                else
                {
                    var sendTask = send.AsTask();
                    if (!await SharpLinkTimer.WaitAsync(
                            sendTask,
                            deadline,
                            deadlineTimeProvider,
                            lifetimeCancellation.Token).ConfigureAwait(false))
                    {
                        deadlineWon = true;
                        lifetimeCancellation.Cancel();
                        _ = ObserveAbandonedGeneratedSendAsync(sendTask);
                        throw CreateGeneratedStreamDeadlineExceededException();
                    }
                    await sendTask.ConfigureAwait(false);
                }
            }

            ThrowIfGeneratedStreamDeadlineExpired(deadline, deadlineTimeProvider);
            SendStreamCompleteAsync(requestId, streamId);
        }
        finally
        {
            lifetimeCancellation.Cancel();
            try
            {
                var dispose = enumerator.DisposeAsync();
                if (deadlineWon && !dispose.IsCompletedSuccessfully)
                    _ = ObserveAbandonedGeneratedDisposeAsync(dispose);
                else
                    await dispose.ConfigureAwait(false);
            }
            catch when (deadlineWon)
            {
                // The monotonic deadline is already terminal; a user enumerator that ignores
                // cancellation cannot delay the RPC while its disposal completes.
            }
        }""")

replace_once(
    "src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs",
    """    // Keep the generated-server path concrete and codec-bound. Exact-size codecs retain the""",
    """    private static void ThrowIfGeneratedStreamDeadlineExpired(
        RpcDeadline deadline,
        TimeProvider? timeProvider)
    {
        if (timeProvider is not null && deadline.IsExpired(timeProvider))
            throw CreateGeneratedStreamDeadlineExceededException();
    }

    private static SharpLinkException CreateGeneratedStreamDeadlineExceededException()
        => new(
            SharpLinkErrorCode.DeadlineExceeded,
            "RPC deadline exceeded during server stream production.");

    private static async Task ObserveAbandonedGeneratedMoveNextAsync(Task<bool> task)
    {
        try { _ = await task.ConfigureAwait(false); }
        catch { }
    }

    private static async Task ObserveAbandonedGeneratedSendAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
    }

    private static async Task ObserveAbandonedGeneratedDisposeAsync(ValueTask dispose)
    {
        try { await dispose.ConfigureAwait(false); }
        catch { }
    }

    // Keep the generated-server path concrete and codec-bound. Exact-size codecs retain the""")

# Old locator signatures remain binary metadata stubs only; do not expose rejected shapes as
# valid new 2.0 authoring APIs.
replace_once(
    "src/SharpLink.Abstractions/SharpLinkGeneratedAssemblyManifest.cs",
    """    public SharpLinkGeneratedAssemblyManifestAttribute(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
        Type manifestType)""",
    """    internal SharpLinkGeneratedAssemblyManifestAttribute(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
        Type manifestType)""")
replace_once(
    "src/SharpLink.Abstractions/SharpLinkGeneratedAssemblyManifest.cs",
    """    public SharpLinkGeneratedAssemblyManifestAttribute(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
        Type manifestType,
        int apiVersion,
        int protocolVersion,
        string generatorVersion)""",
    """    internal SharpLinkGeneratedAssemblyManifestAttribute(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
        Type manifestType,
        int apiVersion,
        int protocolVersion,
        string generatorVersion)""")

# Caller-selected metadata migration guidance.
replace_once(
    "doc/calls-and-streaming.md",
    """Metadata 是 RPC envelope state，不是业务合同参数。Client interceptor 可通过 `SharpLinkClientInvocationContext.Metadata` 为当前逻辑调用提供 metadata；服务端从 `SharpLinkCallContext.Current?.Metadata` 或 Server interceptor context 读取。Metadata 受 `MaxMetadataBytes` 限制，适合低基数路由/诊断信息，不适合大对象、凭据日志或无限增长标签。""",
    """Metadata 是 RPC envelope state，不是业务合同参数。需要由调用方为某一次 invocation 明确选择 metadata 时，使用窄能力 `GetWithMetadata<TContract>(SharpLinkMetadata)` 获取绑定该 metadata 的 proxy，再正常调用业务方法；它不会恢复通用 options bag，也不会使用 ambient/global state。例如：

```csharp
var tenantProxy = client.GetWithMetadata<IMyService>(new SharpLinkMetadata
{
    [\"tenant\"] = \"tenant-a\"
});
await tenantProxy.GetAsync(id, cancellationToken);
```

Client interceptor 仍可通过 `SharpLinkClientInvocationContext.Metadata` 为横切策略补充/变换当前逻辑调用的 metadata；不要用调用顺序或全局可变 interceptor 状态模拟 caller-selected metadata。服务端从 `SharpLinkCallContext.Current?.Metadata` 或 Server interceptor context 读取。Metadata 受 `MaxMetadataBytes` 限制，适合低基数路由/诊断信息，不适合大对象、凭据日志或无限增长标签。""")

replace_once(
    "doc/migration.md",
    """## Runtime engine API boundary""",
    """## `SharpLinkCallOptions` 迁移

2.0 不保留 `SharpLinkCallOptions` 或兼容 options bag。旧调用点按能力迁移：

- `SharpLinkCallOptions.Metadata` → `client.GetWithMetadata<TContract>(metadata)`，用于调用方为单次/一组显式 invocation 选择 metadata；横切 metadata policy 仍可使用 Client interceptor。
- `SharpLinkCallOptions.Timeout` → 契约方法 `[Timeout]`，或 Client 的 `UseRequestTimeout` / `DisableRequestTimeout` fallback policy。timeout 不再是业务方法伪参数。
- `SharpLinkCallOptions.CancellationToken` → 业务方法原有 `CancellationToken` 参数；没有 token 的 RPC 必须明确审计 `[NonCancellable]`。
- `WaitForReady` 不再有每调用兼容开关；连接/readiness 使用 Client readiness API 和拓扑策略表达。

因此生成的业务签名和 `IRpcChannel` ABI 都不再接收 `SharpLinkCallOptions`。迁移时应删除旧 options 参数并重新生成全部 API 4 proxy/stub，而不是创建新的通用调用控制对象。

## Runtime engine API boundary""")

replace_once(
    "CHANGELOG.md",
    """### Breaking

- Protocol v2 minor 4 is the SharpLink 2.0 wire baseline""",
    """### Breaking

- `SharpLinkCallOptions` is removed from generated/service business signatures and from the generated `IRpcChannel` ABI. Per-call timeout now comes from method `[Timeout]` or the Client timeout policy, caller cancellation remains the method `CancellationToken`, and caller-selected metadata uses the narrow `GetWithMetadata<TContract>(SharpLinkMetadata)` proxy capability. No generic compatibility options bag is retained; regenerate all contracts/proxies/stubs and see [`doc/migration.md`](doc/migration.md).
- Protocol v2 minor 4 is the SharpLink 2.0 wire baseline""")

print("review5 P2B source patch applied")
