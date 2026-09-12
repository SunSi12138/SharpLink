# Server 异常映射边界

## #408 结论：保留 `IRpcExceptionMapper`

`IRpcExceptionMapper` 不能完全折叠进 Server interceptor。原因不是 Unary 行为，而是 response stream 的生产阶段拥有独立于 interceptor `catch` 的终端异常边界。

当前 Server 有两个相关的异常处理位置：

1. invocation/interceptor 边界：服务调用在 `await next(context)` 内抛出的异常会先沿 interceptor 栈回退；未处理异常随后进入框架的安全映射。
2. response-stream producer 边界：Server streaming / Duplex 的 `IAsyncEnumerable<T>` 在 stream bridge 中继续产出 item。producer 抛出的异常由 session 的 service-exception mapper 处理，失败状态写回 invocation context；原始 producer 异常不会重新沿已经完成的 interceptor `await next(context)` 抛出。

配置自定义 mapper 时，invocation 终端映射也会把 interceptor 抛出的 `SharpLinkException` 交给 mapper。因此，自定义 mapper 若希望保持 structured error，应像默认 mapper 一样先原样返回 `SharpLinkException`。

因此，应用通过 interceptor `try/catch` 可以覆盖 invocation 期异常，但不能覆盖所有合法的流式业务异常。如果删除公开 mapper，要保持现有能力只能额外包裹 response stream 或引入等价的新 hook，这只是把 `IRpcExceptionMapper` 换名，并增加流式热路径复杂度。

## RPC shape characterization

| Shape | domain exception 相对 `await next(...)` 的位置 | 应用 interceptor 能否直接 catch/translate | 需要 mapper 的独立边界 |
| --- | --- | --- | --- |
| Unary | invocation 内 | 是 | 仅作未处理异常的终端安全边界 |
| OneWay | invocation 内；无响应帧 | 是 | 仅作未处理异常的终端安全边界 |
| Client streaming | 服务消费 request stream / 完成服务调用期间 | 是 | 仅作未处理异常的终端安全边界 |
| Server streaming | response item production 位于 stream producer 边界 | 否 | 是 |
| Duplex streaming | response item production 位于 stream producer 边界 | 否 | 是 |

`ExceptionMapperInterceptorBoundaryTests.MapperMustRemainForResponseProducerFailuresOutsideInterceptorCatch` 使用同一个 domain exception 和同一个 interceptor 映射策略验证以上差异：Unary、OneWay、Client streaming 被 interceptor 捕获；Server streaming、Duplex producer 失败不会命中 interceptor catch，而是命中 `IRpcExceptionMapper`。

## Framework safety invariants

保留 mapper 不改变框架已有的终端安全约束：

- 默认 mapper 原样保留 `SharpLinkException`；自定义 mapper 若要保持该语义应同样 pass-through；
- deadline / owner cancellation 保持 canonical 状态，不应被应用 mapper 意外降级为 `Internal`；
- 未处理异常默认清洗为 `Internal`，不泄漏服务端内部 detail；
- mapper 自身失败仍必须回退到安全的 `Internal`；
- response producer 失败必须把最终 failure 状态留在 `SharpLinkServerInvocationContext`，供 interceptor 在 `next` 返回后观察。

这些行为由现有 `InterceptorIntegrationTests` 中的 structured-error、sanitization、cancellation、throwing-mapper 和 mapped-stream tests 持续覆盖。

## API / performance decision

#408 采用 No-Go：继续保留 `IRpcExceptionMapper` 和 `SharpLinkServerBuilder.UseExceptionMapper(...)`。本次不修改运行时代码，不增加 wrapper、allocation 或每调用 dispatch，因此 enabled/disabled hot path 与 `dev` 相同。
