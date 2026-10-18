# #775：异步大请求解压后保留静态方法事实

## 缺陷与修复

8KiB同步压缩回归通过，不代表大请求异步解压路径正确。
`AwaitPersistentDecodeAndContinueAsync`完成解压后调用ReadRequestEnvelope，重建封装时丢失了
在实际Session上完成token/MethodId/调用形态验证所得的BoundMethod。
后续ContinueRpcDispatch仍需要该引用；错误发生在业务进入之前。

修复提交 `628a2f44014d899c25c1e94ecf59e0575602c2c1`：只在实验编译路径，
保存解压前的request.BoundMethod，重读字节封装后恢复同一引用。
没有按当前注册重查、重解释MethodId、换绑、增加lease或放宽队列/解码预算。
注册、deadline、异常/取消处理和原有资源转移路径不变。默认构建不增加该状态。
这属于正确性修复，不是吞吐优化，也不引入动态Replace、Receipt或Streaming。

相对已测4e16候选，本轮只改一个生产文件：
`src/SharpLink.Server/SharpLinkServer.PersistentDecodeDispatch.cs`。

## 真实传输回归

只读workflow `issue-775-persistent-binding.yml`以固定旧源码
`2763bb4276a6f715e9dfd4827450e7285f919363`及候选SHA运行同一测试overlay。
不改生产源码进行测试注入。使用真实Zstd、TCP/SharedMemory、真实generated Unary代理和业务Echo。

每臂12场景：两种传输×有/无metadata×以下三种路径：

| 路径 | 参数payload | 请求deadline | 预期实际decode worker启动 |
|---|---:|---|---:|
| sync-small | 8 KiB | 无 | 0 |
| sync-large | 2 MiB | 无 | 0 |
| persistent-large | 2 MiB | 20秒 | 1 |

大请求不仅检查配置，还检查server.DecodeStartedWorkCountForDiagnostics；
每个场景必须确实解压一次。校验业务调用数、返回完整payload、metadata内容、deadline存在性、
后续小请求成功、同一绑定计数未改变，以及Stop后active call和decode队列/预留/调度连接计数归零。
没有固定sleep、扩容或重试挑样本；所有失败信息和完成边界存入JSON。

deadline测试只验证presence，不宣称已经覆盖全部deadline数值/取消竞态。
Drained是上述诊断计数检查，不代表对所有池、lease和collectible资源的全面泄漏证明。
这是Unary的persistent解码修复，不把8KiB OneWay或动态服务测试混充为该覆盖。

## 已完成验证（2026-10-18）

首次专项运行39109011153，候选628a，失败于测试代码缺少SharpLink.Sdk命名空间，
新旧两臂都没有运行到业务。该失败保留，不当成旧缺陷复现。
测试修正 `fb1565c66eb3bd272428d3f0275fd1e3fd9652a4`只增加using，不修改生产逻辑。

成功专项运行：https://github.com/SunSi12138/SharpLink/actions/runs/39109415417

- 旧2763的8个同步场景通过；4个persistent场景全部实际解压一次、worker启动一次，
  但BusinessCalls=0，等待超时，Stop后的Drained=false。后续Add仍可成功，不能由此声称资源已全部释放。
- 修复后fb1565的12个场景全部通过：包括4个persistent场景实际进入业务、正确返回、
  metadata/deadline presence、后续小请求、绑定计数及Drained检查。
- 同一源码的默认/实验回归运行39109415409通过：实验Release、443项smoke、默认Release全方案、
  2,123单元测试和271生成器测试、格式及原维护性门禁。两个测试报告SHA均为fb1565，无failed/skipped。
- Python证据读取器4项unittest方法通过，包含18个篡改拒绝子场景；本地未运行.NET编译，均由Actions完成。

原始专项artifact `12129897330`，ZIP SHA256
`c40edd1d6734bbfd22fa2d4a9079b1f55904874d21c3727b3d350a4e08b5c0bf`。
首次失败artifact `12129742248`，ZIP SHA256
`8a44240376ab990553c35f57a11fc54aff62c359b6c5b07eab164e13c7ae7297`。

## 复现

```sh
python3 -m unittest discover -s eng/issue775 -p test_persistent_binding.py -v
# old/ checkout上面的2763，candidate/ checkout待验证提交，均为干净目录。
python3 candidate/eng/issue775/persistent_regression.py --old old --candidate candidate \
  --expected-candidate "$(git -C candidate rev-parse HEAD)" --output persistent-regression
```

构建失败、未协商压缩、没有实际worker启动或业务未完成都不能通过证据校验。
本轮没有在修复源码上重新跑性能。已完成的4e16性能结果见BOUNDARY-PERF.md，不能搬到当前HEAD。
性能恢复仍未成立；不合并或关闭#775，不扩大第二阶段。
