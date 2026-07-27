# SharpLink 0.8.15 迁移指南

English: [`en/migration-0.8.15.md`](en/migration-0.8.15.md)

0.8.15 不改变 public API、Protocol v2 或 generated Manifest。`UseTransport`、`UseEndpointResolver` 和 Server `UseTransport` 接受的对象都是单所有者资源：一次成功 Build（或 Runtime Context 建成后的失败 Build）会把资源从 builder 移走；再次 Build 前必须显式提供新的 transport/resolver。静态 `UseEndpoint(s)` builder 仍可复用，因为每次 Build 都会从 endpoint delegate 创建全新的 factory。Server `Transport` 在所有权转移后返回 null。

Unix-domain listener 不再自动删除既有路径。正常释放仍删除自己成功绑定的路径；崩溃留下的 stale socket 或其他既有 entry 必须由部署/进程所有者确认后显式清理。这避免配置错误覆盖普通文件或抢占另一个进程的 socket。
