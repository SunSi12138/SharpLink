# 迁移到 SharpLink 0.8.0

English: [`en/migration-0.8.0.md`](en/migration-0.8.0.md)

0.8.0 是 1.0 前的 wire-correctness 更新。没有新增公共 API，但生成请求布局和派生契约 fingerprint 可能变化。

## 必须重新编译并同步部署的情况

- RPC 参数包含 user-defined unmanaged struct；即使它此前已选择 Adapter，0.7.11 仍会绕过该 Codec 并 native-blit。
- RPC 参数包含 nullable unmanaged value，例如 `int?`、`Guid?` 或 user-defined `T?`。0.8.0 改为 length-delimited Codec framing。
- `[RpcContract]` 接口继承了声明 RPC 方法的普通基接口。0.8.0 会把这些方法加入 proxy、stub、manifest 与 contract fingerprint。

上述契约的 0.7.11/0.8.0 两端不要混跑；重新生成 contract baseline，并将 Client/Server 一起部署。仅使用固定内置 non-nullable 参数且不继承 RPC 方法的契约不受生成布局变化影响。

## 解码收紧

有效的内置 payload 不变。固定长度、字符串和原生集合现在拒绝尾随字节；Boolean/nullable 标记只接受规范取值。依赖旧版宽松解码的自制 payload 应改为只发送一个完整值。

Stream flow-control wire shape 不变；0.8.0 只补发此前滞留的合法 `WindowUpdate`。
