# SharpLink 0.8.8 迁移指南

English: [`en/migration-0.8.8.md`](en/migration-0.8.8.md)

0.8.8 不改变 public API、Protocol v2 或 generated Manifest。匿名管道与共享内存连接现在会在前序清理异常后继续释放全部所有权资源；动态模块和 Server 全局清理在多个 owner 失败时可能以 `AggregateException` 暴露完整原因。无配置迁移要求；依赖单一清理异常类型的诊断代码应遍历内部异常。
