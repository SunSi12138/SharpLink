# SharpLink 0.8.24 迁移指南

English: [`en/migration-0.8.24.md`](en/migration-0.8.24.md)

0.8.24 不改变 Protocol v2 或 payload layout。合法 0.8.23 RPC 契约继续生成相同的请求、响应与 stream wire shape。

带参数的 `[Timeout(seconds)]` 必须能转换为正且有限的 `TimeSpan`；零、负数、NaN、Infinity、向下舍入为零或溢出 `TimeSpan` 的常量现在报告 `SHARPLINK050`。`[RpcUnionCase]` tag 必须为正；case 必须是可赋值给所标注 union 的闭合 concrete class/struct，且一个 case type 只能绑定一个 tag，否则报告 `SHARPLINK051`。

`[assembly: SharpLinkRpcContracts()]` 现在明确表示“不扫描任何引用契约程序集”。若旧代码使用空特性但依赖自动发现，请删除该特性；带 marker type 的既有筛选行为不变。

生成 JSON 与 assembly Manifest 的 `generatorVersion` 从错误的固定 `0.8.3` 改为实际 Generator package version。由于 JSON 的完整内容受 `schemaFingerprint` 保护，重新构建会得到新 fingerprint；旧 baseline 会先按自身 generator version 验证完整性，再执行结构兼容比较，无需手工篡改。
