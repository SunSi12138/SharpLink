# SharpLink 0.8.43 迁移指南

English: [`en/migration-0.8.43.md`](en/migration-0.8.43.md)

0.8.43 不改变公共 API、合法 Protocol v2 framing、method/field ID 或 payload layout，不要求业务代码迁移。

共享内存启动清理现在只把至少一分钟前、且能独占打开的 mapping 视为 abandoned。框架生成的随机 mapping 路径和连接握手不变；运维清理若依赖“创建下一条连接立即删除刚生成的同目录文件”，应改为显式清理或等待 stale 门槛。

没有显式异常的连接关闭现在向调用方保留 `SharpLinkErrorCode.ConnectionClosed`，不再错误显示为 `Internal`。在远端终态前释放 Client response stream 会把 Activity 标为 Error，并增加 `sharplink.calls.abandoned` 的 `consumer_abandoned` 原因；这只修正诊断语义，不改变取消 wire bytes。

flow-control 和动态 admission retirement 修复均为内部生命周期变化。合法 stream credit、selector、breaker 配置与正常调用结果不变；持续动态地址轮换不再保留已释放 generation 的 breaker sample rings。
