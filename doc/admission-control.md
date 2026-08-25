# 服务端接入控制

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
    options.Global.UseTokenBucket(rate =>
    {
        rate.TokenLimit = 1000;
        rate.TokensPerPeriod = 1000;
        rate.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
    });
    options.MaxQueuedCalls = 512;
    options.MaxQueuedBytes = 16 * 1024 * 1024;
    options.MaxQueueDelay = TimeSpan.FromSeconds(2);
});

server.UpdateAdmissionControl(options =>
{
    // 回调描述完整的 N+1 Admission 配置，而不是增量 patch。
    options.Global.UseConcurrency(128);
    options.Global.UseFixedWindow(rate =>
    {
        rate.PermitLimit = 750;
        rate.Window = TimeSpan.FromSeconds(1);
    });
    options.MaxQueuedCalls = 256;
    options.MaxQueuedBytes = 8 * 1024 * 1024;
    options.MaxQueueDelay = TimeSpan.FromSeconds(1);
});

server.DisableAdmissionControl();
```

`EnableAdmissionControl` 只支持 Disabled → Enabled；已启用时再次调用会抛出 `InvalidOperationException`。`UpdateAdmissionControl` 只支持 Enabled → Enabled，并要求回调给出完整候选配置；Admission 已停用时调用也会抛出 `InvalidOperationException`。`DisableAdmissionControl` 执行 Enabled → Disabled，对已经停用的状态重复调用是幂等的。不支持这些入口的自定义 `ISharpLinkServer` 实现会抛出 `NotSupportedException`。

Enable 和 Update 都会在 publication/lifecycle 锁之外执行用户回调，并完成候选配置的校验、规则解析和运行时状态绑定。Update 还会记住它实际派生自哪个 source generation；进入短 writer 临界区后必须确认该 generation 仍然是 current，才会提交已准备好的 concurrency/rate/partition transition 并原子发布 N+1。若另一个 Update、Disable、Enable 或 Stop 已先改变当前状态，候选会失败并回收，不会自动 rebase，也不会把 losing candidate 的 `MaxPartitions`、`IdleTimeout`、并发目标、rate quota 或 selector namespace 变更留在 live state 中。

请求只捕获一次 Admission program。N+1 发布后才捕获 Admission 的 Request 使用 N+1；已经捕获 N 的活动或排队 Request 继续使用 N 的不可变策略快照直到终止。因此普通 update/disable 都不会取消旧 Request，也不会把旧 waiter 的超时、OneWay 策略、partition namespace 或 rate algorithm 静默切换成新值。旧 generation 在最后一个用户离开后按 retire/reclaim 生命周期回收。

### 当前可在线更新的范围

Enabled → Enabled 当前支持：

- Global / Contract / Method concurrency 的新增、移除和 resize；
- Global / Contract / Method rate limiter 的新增、移除、参数更新和 Token Bucket / Fixed Window / Sliding Window 之间的算法替换；
- partition `MaxPartitions` 与 `IdleTimeout`；
- partition concurrency 的新增、移除和 resize；
- partition rate limiter 的新增、移除、参数更新和算法替换；
- partition selector replacement；
- `MaxQueuedCalls`；
- `MaxQueuedBytes`；
- `MaxQueueDelay`；
- `QueueOneWayCalls`。

Global / Contract / Method 的并发状态按逻辑 scope 保持稳定，不以当前数值 limit 作为状态身份。并发从 1 增加到 3 时，已有 1 个 holder 仍计入 active，只新增 2 个可用 permit；已有 FIFO waiter 会按容量释放。并发从 3 缩到 1 且已经有 3 个 holder 时，3 个 holder 都继续执行，不取消任何活动调用，也不会创建一份新的 permit budget；在 active 降到新 limit 以下之前不会再接纳 holder。已经排队的 waiter 同样不会因为 shrink 被取消。

速率状态与并发状态独立持有，logical rate identity 由 Global / Contract(id) / Method(contractId, methodId) 决定，而不是由当前算法参数决定。未变化的 rate policy 会精确复用同一运行时状态；发生 rate 更新时，N+1 会创建新的 policy generation，并在 publication writer 内从 source lineage 提交 quota/history handoff。候选构造本身不会消耗、重置或修改 live source quota。

所有 rate transition 都遵守“配置更新不能凭空制造 quota”的约束：

- Token Bucket 保留已经消耗的 debt；修改 `TokenLimit` 不会 refill，shrink 可暂时阻塞新请求；保持相同补充 cadence 时延续原 monotonic anchor，修改 cadence 时不会因 publication 额外获得一个补充周期。结构性替换进入 Token Bucket 时，source barrier 中的 carried debt 与 target 自己产生的 token debt 共用同一份 replenishment credit：每个补充 credit 只能偿还一处；source barrier 到期前未被 target traffic 使用的 credit 只能预付 carried debt、不能提前释放它，barrier 到期时尚未预付的 carried debt 会转入普通 token debt 并继续按 target cadence 偿还。
- Fixed Window 保留 active window epoch 和已消费 permit；修改 limit 不开启新 window。修改 window duration 使用保守的 monotonic handoff，不能在 publication 时得到完整新 window。
- Sliding Window 保留仍应属于新 horizon 的消费历史。shape/window/segment 变化不安全进行精确映射时，会把 source burden 折叠为有明确 expiry 的保守 transition barrier，而不是清空 segments。
- 算法替换不会机械地把 token 解释为 window segment。source debt 会以 conservative transition barrier 进入目标算法，至少保留到 source debt 合法过期与目标 horizon 要求中的较晚边界。

旧 generation 的 rate waiter/retained lease 仍属于旧 state。它在 N+1 发布后才获得的旧算法 grant 也会保守计入当前 lineage 的 target barrier，因此旧、新 generation 重叠期间不会叠加出免费 burst。每个底层 rate waiter 仍必须对应恰好一个 kernel 外层 queue reservation；rate state 没有第二套 queue capacity limit。

新增 rate component 时，因为该 logical component 在 source 中没有旧 quota，可按新 policy 的初始状态开始；只有 winning candidate 会成为 live lineage。移除 rate component 后，新请求不再经过该 limiter，但旧 generation 用户继续安全完成。若 A 被移除时仍存活，随后相同数值 policy 被重新加入，会创建 current lineage B，不会按“历史参数相同”错误复用 A；Disable/Enable 期间若 B 仍是当前可复用 lineage，则同 policy 会继续绑定 B。

### Partition 在线迁移

Partition 把 selector namespace identity 与可变策略分开。complete-candidate Update 携带与 source 相同的冻结 selector binding 时，视为 selector-compatible update：继续使用同一个 partition namespace、entry dictionary 与既有 key state；不会因为 `MaxPartitions`、`IdleTimeout`、partition concurrency/rate 参数变化而创建第二个 pool。当前实现以保守的 delegate value equality 证明冻结 selector binding 的精确复用，不做 request-path 反射式“语义等价”判断；不能证明精确复用的 selector replacement 会创建新的 namespace generation。

同一 namespace 下，`MaxPartitions` 是 live target。增加上限只增加差额容量；例如已有 100 个 entry 时 100 → 150 最多再允许 50 个新 key。缩小上限不驱逐 active/queued/retained entry，也不复制一份新的容量预算；当 live entry count 仍高于或等于新 target 时，新的 missing key 以正常的 `partition_capacity` 路径拒绝，idle reclaim 使 count 降到 target 以下后才恢复新 key 创建。missing-key lookup/create 与 target publication 通过同一个 reader-safe target epoch 重新授权，因此请求不能在 shrink 后用旧容量判断插入 entry。

`IdleTimeout` 更新沿用每个 entry 现有的 monotonic last-use/idle timestamp，不把时间重置为 publication 时刻。timeout shrink 可以使历史上已经足够久的 idle entry 立即满足回收条件；timeout increase 则从原 last-use 时间延长剩余期限。只要 Request 仍持有 partition entry lease，包括 active、queued 或 retained limiter 使用，entry 就不是可回收 idle state。当前实现仍采用机会式 idle reclaim，不为每个 entry 新增永久 timer/task。

同 selector、同 key 的 partition concurrency 复用稳定 `ResizableConcurrencyState`，遵守与 Global / Contract / Method 相同的非抢占 resize 规则：increase 只暴露差额 permit，shrink 保留现有 holder 和 FIFO waiter，直到 active 自然回落后再接纳新 holder。N+1 发布后首次出现的新 key 直接按 N+1 target 创建，不经历旧 concurrency target。

同 selector、同 key 的 partition rate 使用与其他 scope 相同的 rate lineage/transition 实现。Token Bucket、Fixed Window、Sliding Window 参数更新保留相应 consumption/history，算法替换使用保守 barrier；不会把更新当成 fresh bucket/window，也不会因为旧、新 generation 重叠获得免费 burst。旧 Request 捕获的 partition runtime generation 继续安全使用旧 limiter generation；N+1 Request 使用该 entry 已提交的新 generation。N+1 后首次出现的新 key 没有历史 quota，因此按 N+1 policy 的正常初始状态创建。

selector replacement 属于结构性 namespace replacement，而不是 resize。N+1 使用新的空 entry dictionary；不会枚举或按相同字符串把旧 key 迁移到新 namespace，即使两个 selector 都返回 `"42"` 或都返回 null/default key，也不会共享 entry identity。已经捕获 N 的 active/queued Request 留在旧 namespace 并正常完成，新捕获的 Request 才使用 N+1 selector。旧 namespace 在最后一个 program/use/entry/limiter 所有权退出后回收；重复 selector replacement 不需要永久保留历史 dictionary。

如果 partition policy 被移除后又重新加入，重新加入的是新的 current namespace lineage，而不是按历史配置相同去扫描并复用更早的 namespace。普通 Disable 会保留仍被旧 Request 使用的最新 current lineage；在该 lineage 尚存活且 re-enable 给出精确兼容的 selector/policy 时会继续复用它，避免把 `MaxPartitions`、concurrency 或 rate quota 拆成 sibling pools。若 disabled 区间内旧 state 已完全回收，则后续 Enable 正常创建 fresh state。

## 排队与在线 queue policy

稳定的 server-scoped Admission kernel 是 queue count/byte 的唯一记账域。一个 Request 只有先成功取得恰好一个 kernel queue reservation，才可能进入底层 concurrency/rate limiter 的异步等待；动态修改 `MaxQueuedCalls` 不会复制或拆分内部 queue state。Partition 没有第二套 queue capacity authority；partition waiter 的 cancellation/deadline/terminal cleanup 仍通过同一个 kernel reservation 恰好释放一次 count/bytes。

只有 `MaxQueuedCalls`、`MaxQueuedBytes` 和 `MaxQueueDelay` 都允许时才等待；任何一个边界耗尽都会立即拒绝。排队仍受调用 deadline 和取消 token 约束。

queue bound shrink 不驱逐旧 waiter：例如当前已经有 80 个 waiter，`MaxQueuedCalls` 从 100 降为 20 后，这 80 个 waiter 继续等待，新 N+1 waiter 会在共享 queued count 仍不低于 20 时被拒绝。`MaxQueuedBytes` 使用相同语义，已有 retained payload 继续占用原来的字节 reservation，直到正常终止路径释放。

`MaxQueueDelay` 在 Request 真正进入 Admission queue 时捕获。N 下以 2 秒进入 queue 的 waiter，在更新为 500 ms 后仍保留 2 秒；N+1 的新 waiter 才使用 500 ms。

OneWay 默认不排队，超限即丢弃并记录 `sharplink.admission.oneway.dropped`；设置 `QueueOneWayCalls` 后才允许等待。该值也是 program snapshot：已经在 N 下排队的 OneWay 不会因 N+1 改为 `false` 被丢弃，而 N+1 的新 OneWay 会立即采用新值。Two-way queue 行为不受该开关影响。

## Stop 与 ResourceGovernor

普通的 `DisableAdmissionControl` 或 `UpdateAdmissionControl` 都不是 Server Stop。它们只切换 Admission publication，不触发 `StopAccepting`，也不取消或等待旧 generation。一旦 Server 进入 Draining、Stopped 或 Faulted，Admission control plane 就封口；之后的 Enable/Update/Disable 不再发布 program，并按同一生命周期 writer 顺序线性化。Stop 会终止仍排队的 Admission waiter，并在 generation 用户退出后回收 current/retired rule state、partition namespace/entry state 及其 rate timer；timer callback 不会继续访问已 dispose 的 state。

运行时 Admission 更新不会改变服务器调用容量、解码/预接入预算、保留字节或流式字节的所有权与边界。`ServerCallCapacityGovernor`/ResourceGovernor 相关限制始终在 Admission 之外独立生效；容量拒绝仍发生在昂贵 request decode/decompression 之前，受控拒绝也不会使健康连接失效。Partition selector/entry migration 不改变该资源顺序，也不引入新的 decoded-buffer、compressed-byte 或 pre-admission stream 所有权。

## Partition

Partition selector 必须同步、快速、低基数，返回稳定字符串或 null/default partition。配置 `MaxPartitions` 和 `IdleTimeout`，避免用户输入制造无限状态。partition entry 只有在没有 active/queued/retained ownership 且超过当前 idle timeout 时才回收。

需要保留同一逻辑 namespace 的运行时更新，应复用同一个冻结 selector binding；替换 selector binding 会保守地定义为新的 namespace generation。不要依赖两个不同 selector 恰好生成相同字符串来共享 quota 或 entry state。

## 生命周期与指标

permit 覆盖实际服务执行、异步 continuation 和 terminal cleanup。同步抛错、取消、响应队列失败或 Server Stop 都必须释放 permit。相关指标：active permits、queued calls、rejected calls、queue duration、active partitions。运行时 partition 诊断还可验证 live namespace、entry 与 entry runtime generation 在旧用户 drain 后回到有界状态。

`demo/AdmissionControl` 使用全局并发 1，证明一个调用执行时三个并发请求都收到 `ResourceExhausted`，随后已接入调用正常完成。
