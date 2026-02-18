# SharpLink 性能治理总表（全量）

本文档面向“当前版本较前几版性能下降”的问题，覆盖 `src/` 全链路可优化点，并给出优先级与落地顺序。

## 1. 当前回退的高概率根因（先看这里）

基于最新代码（重点是 `src/SharpLink.Client/SharpLinkClient.cs`）分析，性能回退最可能来自以下组合开销：


## 2. 全链路性能问题清单（按模块）

## 2.1 Client（`src/SharpLink.Client`）

### P0

### P1



## 2.2 Server（`src/SharpLink.Server`）

### P0


### P1

3. session 层任务创建策略

4. 握手路径字符串处理

## 2.3 Runtime（`src/SharpLink.Runtime`）

### P0


### P1




## 2.4 Generator（`src/SharpLink.Generator`）

### P0

2. blittable 跨段回退 `new byte[]`
- 位置：`src/SharpLink.Generator/RpcGenerator.cs:611-613`
- 问题：触发回退时分配临时数组。
- 优化：
  - 小尺寸 `stackalloc`，大尺寸 `ArrayPool<byte>.Shared`。

### P1

## 2.5 Transport

2. NamedPipe 创建 reader/writer 重复
- 位置：`src/SharpLink.Runtime/NamedPipeTransport.cs:11-13`, `:26`
- 问题：属性与返回路径都有创建点，建议统一单一实例。
- 优化：只使用缓存实例，避免潜在重复包装。
