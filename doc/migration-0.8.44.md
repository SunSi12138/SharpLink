# SharpLink 0.8.44 迁移指南

English: [`en/migration-0.8.44.md`](en/migration-0.8.44.md)

0.8.44 不改变公共 API、合法 Protocol v2 framing、method/field ID 或 payload layout，不要求业务代码迁移。

停止 Client、静态 endpoint cluster 或 Server 时，框架现在会保留此前被同组预期连接关闭遮蔽的内部 background/session failure。依赖 Stop 静默忽略自定义 transport、resolver 或 callback 内部异常的代码应修复该异常源；正常取消、连接关闭和已由 `ConnectAsync` 调用者观察的初始连接失败仍按原语义处理。

bounded send queue 拒绝终态响应或终态 stream frame 时，原始异常类型与错误码不变，但 Server call admission、service/request ownership 和 stream flow-control slot 现在一定释放。这是生命周期修正，不改变成功调用、合法 flow-control credit 或 wire bytes。
