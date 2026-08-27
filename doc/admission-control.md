# 服务端接入控制

SharpLink 有两层彼此独立的接入保护：连接在进入 Ready 前先经过 connection admission；完成握手后，RPC 调用再经过 call admission。两层都使用固定、可观测的有界资源，不用用户输入创建无界状态。

## 连接与握手边界

`SharpLinkConnectionAdmissionOptions` 保护 accepted/live connection 与 pre-auth handshake。默认最多保留 1024 个 live connection，同时最多允许 64 个连接处于 TLS / Protocol v2 / authentication handshake。handshake slot 在连接 Ready 时立即释放，connection slot 一直保留到该连接的 terminal cleanup。

```csharp
var server = SharpLinkServerBuilder.Create()
    .UseTcp(5000)
    .UseConnectionAdmission(options =>
    {
        options.MaxConcurrentConnections = 1024;
        options.MaxConcurrentHandshakes = 64;
    })
    .Build();
```

默认 handshake 上限是独立的固定安全边界。如果只把 `MaxConcurrentConnections` 配到 64 以下而没有显式设置 handshake 上限，默认 handshake 上限会自动取更低的 connection bound。需要恢复旧的“没有独立 handshake 上限”行为时必须显式设置 `MaxConcurrentHandshakes = 0`；此时实际 handshake 并发仍受 `MaxConcurrentConnections` 限制。显式正值不能大于 connection bound。

超过任一 connection admission 边界时，已 accept 的连接会立即关闭，不进入后续 TLS/Protocol/auth 生命周期，也不排队。服务启动日志会记录最终生效的 `max_connections` 与 `max_handshakes`；`sharplink.connections.handshakes.active` 和 `sharplink.connections.rejected` 可用于观察当前握手占用和拒绝。

## RPC 调用接入

接入控制在请求完整校验后、服务实例创建和业务执行前申请资源。拒绝使用结构化 `ResourceExhausted`，不会关闭健康连接。

## 限制层级

规则按 Global、Contract、Method、Partition 组合；一次调用必须同时取得所有适用 permit。每个 scope 可配置一个并发限制和至多一个速率限制：Token Bucket、Fixed Window 或 Sliding Window。

```csharp
serverBuilder.UseAdmissionControl(options =>
{
    options.Global.UseConcurrency(256);
    options.MaxQueuedCalls = 512;
    options.MaxQueuedBytes = 16 * 1024 * 1024;
    options.MaxQueueDelay = TimeSpan.FromSeconds(2);
    options.AddMethod<IOrders>(nameof(IOrders.SubmitAsync), rule =>
        rule.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 1000;
            rate.TokensPerPeriod = 1000;
            rate.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
        }));
});
```

## 运行时启用、更新和停用

Server 包提供三个运行时控制入口：

```csharp
ISharpLinkServer server = serverBuilder.Build();

server.EnableAdmissionControl(options =>
{
    options.Global.UseConcurrency(256);
    options.MaxQueuedCalls = 512;
    options.MaxQueuedBytes = 16 * 1024 * 1024;
    options.MaxQueueDelay = TimeSpan.FromSeconds(2);
});

server.UpdateAdmissionControl(options =>
{
    // 回调描述完整的 N+1 Admission 配置，而不是增量 patch。
    options.Global.UseConcurrency(128);
    options.MaxQueuedCalls = 256;
    options.MaxQueuedBytes = 8 * 1024 * 1024;
    options.MaxQueueDelay = TimeSpan.FromSeconds(1);
});

server.DisableAdmissionControl();
```

`EnableAdmissionControl` 只支持 Disabled → Enabled；已启用时再次调用会抛出 `InvalidOperationException`。`UpdateAdmissionControl` 只支持 Enabled → Enabled，并要求回调给出完整候选配置；Admission 已停用时调用也会抛出 `InvalidOperationException`。`DisableAdmissionControl` 执行 Enabled → Disabled，对已经停用的状态重复调用是幂等的。不支持这些入口的自定义 `ISharpLinkServer` 实现会抛出 `NotSupportedException`。

Enable 和 Update 都会在 publication/lifecycle 锁之外执行用户回调，并完成候选配置的校验、规则解析和运行时状态绑定。Update 还会记住它实际派生自哪个 source generation；进入短 writer 临界区后必须确认该 generation 仍然是 current，才会提交并发 resize 并原子发布 N+1。若另一个 Update、Disable、Enable 或 Stop 已先改变当前状态，候选会失败并回收，不会自动 rebase，也不会把 losing candidate 的目标值留在 live state 中。

请求只捕获一次 Admission program。N+1 发布后才捕获 Admission 的 Request 使用 N+1；已经捕获 N 的活动或排队 Request 继续使用 N 的不可变策略快照直到终止。因此普通 update/disable 都不会取消旧 Request，也不会把旧 waiter 的超时或 OneWay 策略改成新值。旧 generation 在最后一个用户离开后按 retire/reclaim 生命周期回收。

### 当前可在线更新的范围

Enabled → Enabled 当前仅支持：

- Global / Contract / Method concurrency 的新增、移除和 resize；
- `MaxQueuedCalls`；
- `MaxQueuedBytes`；
- `MaxQueueDelay`；
- `QueueOneWayCalls`。

Global / Contract / Method 的并发状态按逻辑 scope 保持稳定，不以当前数值 limit 作为状态身份。并发从 1 增加到 3 时，已有 1 个 holder 仍计入 active，只新增 2 个可用 permit；已有 FIFO waiter 会按容量释放。并发从 3 缩到 1 且已经有 3 个 holder 时，3 个 holder 都继续执行，不取消任何活动调用，也不会创建一份新的 permit budget；在 active 降到新 limit 以下之前不会再接纳 holder。已经排队的 waiter 同样不会因为 shrink 被取消。

速率状态与并发状态独立持有。只修改 concurrency 或 queue policy 时，未变化的 Token Bucket、Fixed Window、Sliding Window 会继续使用同一运行时状态，因此不会获得免费 burst，也不会重置 window。当前 slice 不支持修改速率参数、切换速率算法、增加或移除 rate limiter；这些候选会在发布前事务性拒绝。

Partition 配置迁移同样暂不支持：selector、`MaxPartitions`、`IdleTimeout`、partition concurrency/rate 配置都必须保持不变。全局 queue policy 或非 partition concurrency 更新会精确复用既有 partition pool 和其中的活动 entry/rate history；任何 partition 配置变化都会事务性拒绝。

## 排队与在线 queue policy

稳定的 server-scoped Admission kernel 是 queue count/byte 的唯一记账域。一个 Request 只有先成功取得恰好一个 kernel queue reservation，才可能进入底层 concurrency/rate limiter 的异步等待；动态修改 `MaxQueuedCalls` 不会复制或拆分内部 queue state。

只有 `MaxQueuedCalls`、`MaxQueuedBytes` 和 `MaxQueueDelay` 都允许时才等待；任何一个边界耗尽都会立即拒绝。排队仍受调用 deadline 和取消 token 约束。

queue bound shrink 不驱逐旧 waiter：例如当前已经有 80 个 waiter，`MaxQueuedCalls` 从 100 降为 20 后，这 80 个 waiter 继续等待，新 N+1 waiter 会在共享 queued count 仍不低于 20 时被拒绝。`MaxQueuedBytes` 使用相同语义，已有 retained payload 继续占用原来的字节 reservation，直到正常终止路径释放。

`MaxQueueDelay` 在 Request 真正进入 Admission queue 时捕获。N 下以 2 秒进入 queue 的 waiter，在更新为 500 ms 后仍保留 2 秒；N+1 的新 waiter 才使用 500 ms。

OneWay 默认不排队，超限即丢弃并记录 `sharplink.admission.oneway.dropped`；设置 `QueueOneWayCalls` 后才允许等待。该值也是 program snapshot：已经在 N 下排队的 OneWay 不会因 N+1 改为 `false` 被丢弃，而 N+1 的新 OneWay 会立即采用新值。Two-way queue 行为不受该开关影响。

## Stop 与 ResourceGovernor

普通的 `DisableAdmissionControl` 或 `UpdateAdmissionControl` 都不是 Server Stop。它们只切换 Admission publication，不触发 `StopAccepting`，也不取消或等待旧 generation。一旦 Server 进入 Draining、Stopped 或 Faulted，Admission control plane 就封口；之后的 Enable/Update/Disable 不再发布 program，并按同一生命周期 writer 顺序线性化。

运行时 Admission 更新不会改变服务器调用容量、解码/预接入预算、保留字节或流式字节的所有权与边界。`ServerCallCapacityGovernor`/ResourceGovernor 相关限制始终在 Admission 之外独立生效；容量拒绝仍发生在昂贵 request decode/decompression 之前，受控拒绝也不会使健康连接失效。

## Partition

Partition selector 必须同步、快速、低基数，返回稳定字符串或 null/default partition。配置 `MaxPartitions` 和 `IdleTimeout`，避免用户输入制造无限状态。partition entry 只有空闲并超过 idle timeout 才回收。

## 生命周期与指标

permit 覆盖实际服务执行、异步 continuation 和 terminal cleanup。同步抛错、取消、响应队列失败或 Server Stop 都必须释放 permit。相关指标：active permits、queued calls、rejected calls、queue duration、active partitions。

`demo/AdmissionControl` 使用全局并发 1，证明一个调用执行时三个并发请求都收到 `ResourceExhausted`，随后已接入调用正常完成。
