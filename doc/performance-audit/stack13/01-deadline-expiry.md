# Stack13 01/13 — 预计算 deadline 到期门槛

基底为固定 dev `ba3a16c1c940fa6f516d2d85a631f46408e3f8d9`。本项与之前未合并的 #605、局部 owner、逻辑完成融合是独立改动；这些前置实验不混入本13项的增量成绩。

## 改动与不变量

保持精确向上取整、零预算、模环差值和饱和诊断投影。

## 已有组件证据（2026-09-08）

到期检查 6.12 → 1.55 ns；四种频率下降约72–75%。

RpcDeadline仍40 B；Timestamp诊断读取可能变慢，创建+诊断读取约+11%。

这些来自之前第3–5轮独立微基准，不是本PR的TCP QPS、不保证在新dev上保持同样百分比，也不能与其他项相加。适用范围以测量单元为准。SDK10.0.400/runtime10.0.11，Release，Linux x64。

## 验证与合并顺序

针对性检查：SharpLinkTimePrecisionTests; ServerCallDeadlineSchedulerWrapTests。整栈联合验证和新的端到端数据由末层PR汇总，失败原样保留；不通过删除测试或改容量、截止时间保障来达标。

堆叠分支 `perf/stack13-01-deadline-expiry-20260909`，base为dev。按01→13顺序合并，后续PR应依次重定向base；不要把顶层累计diff作为单个优化的收益证据。均不开启自动合并。
