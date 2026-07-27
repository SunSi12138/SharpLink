# 迁移到 SharpLink 0.8.1

English: [`en/migration-0.8.1.md`](en/migration-0.8.1.md)

0.8.1 没有新增公共 API，但进一步收紧 generated request wire correctness。

RPC request 参数包含下列 non-nullable 类型时，0.8.1 从 raw inline layout 改为 length-delimited built-in Codec：`decimal`、`DateOnly`、`DateTime`、`DateTimeOffset`、`TimeOnly`、`Rune`、`Index`、`Range`。这保证 malformed bytes 不能绕过已有 Codec 校验。 affected contract 必须重新生成 baseline，并同步重新编译/部署 Client 与 Server。

`bool` 仍保持单字节 inline layout，因此有效契约 wire 不变；encoder 固定写 `0/1`，decoder 拒绝其他 marker。普通 integer、floating point、`Half`、`Guid`、`TimeSpan`、`Int128` 与 `UInt128` 继续 inline。

Authentication scopes、endpoint snapshots 和 generated manifests 现在是真正的只读视图。依赖强转并修改这些集合属于未受支持行为，应删除。Resolver 释放与 `List<T>` payload wire shape 不变。
