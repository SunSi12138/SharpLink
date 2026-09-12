# Stack13 03/13 — 定时请求前缀一次预留

基底为固定 dev `ba3a16c1c940fa6f516d2d85a631f46408e3f8d9`。本项与之前未合并的 #605、局部 owner、逻辑完成融合是独立改动；这些前置实验不混入本13项的增量成绩。

## 改动与不变量

仍在emission边界填写实际剩余预算；generic/delegate入口同时修改。

## 已有组件证据（2026-09-08）

24 B路由+预算占位写入6.07 → 3.96 ns（约-35%）。

无新增对象；非定时请求没有多写8 B。

这些来自之前第3–5轮独立微基准，不是本PR的TCP QPS、不保证在新dev上保持同样百分比，也不能与其他项相加。适用范围以测量单元为准。SDK10.0.400/runtime10.0.11，Release，Linux x64。

## 验证与合并顺序

针对性检查：SharpLinkClientTrackedEmissionDeadlineTests; ProtocolV2 tests。整栈联合验证和新的端到端数据由末层PR汇总，失败原样保留；不通过删除测试或改容量、截止时间保障来达标。

堆叠分支 `perf/stack13-03-timed-prefix-20260909`，base为前一层分支。按01→13顺序合并，后续PR应依次重定向base；不要把顶层累计diff作为单个优化的收益证据。均不开启自动合并。
