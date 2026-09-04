# Architecture Decision Records

Architecture Decision Record（ADR）用于记录那些无法仅从最终代码轻易推导、并会长期约束后续实现的架构取舍。SharpLink 的 ADR 保持轻量：一条记录聚焦一个决定，说明背景、选择、替代方案和主要后果，并链接已有的规范性工具或文档，而不是复制它们。

## 什么时候需要 ADR

当决定具有持续影响，并涉及以下一类或多类取舍时，应添加 ADR：

- 性能热路径或 NativeAOT 约束要求采用不直观的结构、缓存、生成策略或运行时限制。
- 线协议、兼容性、版本协商或迁移策略存在多个合理方案，所选方案会约束未来实现。
- 并发、背压、取消或顺序语义依赖不明显的不变量，后续修改必须理解这些约束。
- 跨 Generator / Runtime / Client / Server 的状态、生命周期或资源所有权需要明确归属，或需要长期架构例外。
- 一个维护性或架构例外不只是短期 debt，而会形成可复用先例或长期约束。

局部实现细节、明显 bug 修复、纯机械整理、一次性调查，或已经由 canonical policy 完整决定且没有新增取舍的变更，通常不需要 ADR；PR 描述、测试或相应专题文档即可。

## 文件与状态约定

- ADR 放在本目录，命名为 `NNNN-short-kebab-title.md`，编号单调递增；[`0000-template.md`](0000-template.md) 仅作为模板，不占正式决定编号。
- 保持短小，只记录未来维护者无法仅靠最终代码理解的上下文、不变量和取舍。
- `Status` 使用 `Proposed`、`Accepted`、`Superseded` 或 `Rejected`。讨论中的方向用 `Proposed`；在 PR 合并前方向已经确认时改为 `Accepted`。
- 已 `Accepted` 的 ADR 原则上保留历史。若决定发生实质变化，新增 ADR，并在旧 ADR 标记 `Superseded`、互相链接；拼写和链接等非语义修正可直接更新。
- 一条 ADR 只记录一个主要决定。若多个约束属于同一不可分割的决定，可以共同说明；否则拆成独立 ADR。

## Canonical policy 与证据

ADR 记录“为什么”和长期不变量，不成为阈值、允许边或 CI 命令的第二份来源：

- 生产项目引用规则以 [`../project-reference-boundaries.yml`](../project-reference-boundaries.yml) 为规范来源，人类可读说明见 [`../project-reference-boundaries.md`](../project-reference-boundaries.md)。
- 可维护性 baseline 与例外 review 规则见 [`../../eng/maintainability.md`](../../eng/maintainability.md) 和 `eng/maintainability/baseline.json`。
- PR Fast 的验证范围与本地命令见 [`../pr-fast.md`](../pr-fast.md)。
- 性能决定应链接可复现的基线、提交、负载或证据；不要把易过期的绝对数字复制成架构承诺。

当上述规则变化时，更新 canonical policy；ADR 只需要链接并说明该规则为何与当前决定相关。

## Review 要点

Review ADR 时重点确认：

- `Context` 是否把事实、约束和假设说明清楚，而不是只描述最终实现。
- `Decision` 是否给出明确的不变量、依赖方向或所有权，而不是模糊目标。
- 主要替代方案和取舍是否被记录，尤其是性能、NativeAOT、协议兼容和并发风险。
- `Consequences` 是否包含负面成本、迁移或兼容影响，以及未来修改需要保持的边界。
- 验证证据是否可定位到测试、benchmark、issue/PR 或 canonical command。
- ADR 是否避免复制会演进的阈值、允许边和完整工具规则。

从 [`0000-template.md`](0000-template.md) 复制最小结构即可；不适用的可选小节可以删除。
