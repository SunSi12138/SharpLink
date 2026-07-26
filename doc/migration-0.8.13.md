# SharpLink 0.8.13 迁移指南

English: [`en/migration-0.8.13.md`](en/migration-0.8.13.md)

0.8.13 不改变 public API、Protocol v2 或 generated Manifest，也不要求配置迁移。PipeReader 仍只允许一个待处理读取；并发的第二次调用继续收到 `InvalidOperationException`，但不再改变已接受读取的取消或通知状态。可取消的共享内存读写等待会由自身 token 及时唤醒；关闭控制通道或带 spill 的 PipeWriter 时，完成会等待相关后台 writer/flush 收敛，因此它不再把尚未结束的资源操作留给调用者之后运行。
