# SharpLink 0.8.26 迁移指南

English: [`en/migration-0.8.26.md`](en/migration-0.8.26.md)

0.8.26 不改变 Protocol v2、route hash、payload layout、Manifest schema 或合法 public RPC surface。

`[Oneway]` 方法必须返回非泛型 `Task` 或 `ValueTask`。`Task<T>`、`ValueTask<T>` 与 `IAsyncEnumerable<T>` 现在报告 `SHARPLINK056`；这些形状要求响应或 stream，无法保留 Oneway 语义。请移除 `[Oneway]`，或把返回值改为非泛型异步完成类型。

private/protected 且带实现的 default interface helper 不再进入 Manifest 或生成 Proxy/Stub。非 public abstract method 报告 `SHARPLINK054`；若它确实是 RPC，请改为 public。生成局部变量避让与字典 null-key `DataLoss` 均无需调用方修改。

仅大小写不同的 DTO member 现在可安全生成。constructor parameter 优先 exact ordinal member name；若没有 exact match，不区分大小写匹配必须唯一，否则 DTO 报告 `SHARPLINK012`。发生该诊断时，请让 parameter 名称与目标 member 精确一致，或提供可写 member/无参构造方案。
