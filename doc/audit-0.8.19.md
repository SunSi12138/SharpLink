# SharpLink 0.8.19 深度审核

English: [`en/audit-0.8.19.md`](en/audit-0.8.19.md)

以 0.8.18 commit `1b380e6` 为基线，本批确认五项 P2 以上改进：畸形认证 provider result 可凭 `IsAuthenticated=true` 绕过 rejection code；Client/Server interceptor 可重复调用共享 `next` 并重复执行非幂等终点；已完成的 faulted Client background task 会从跟踪集合静默消失；Generic Host Server Stop 的后续 Dispose failure 会替换先前取消或 Stop failure；公开的超长 resolver/heartbeat/admission 时间配置超出便携原生 timer 范围。

六个聚焦测试覆盖认证、Client/Server interceptor、后台故障、Hosted 双重故障、delegate/DNS polling 和 admission validation。所有探针都在对应生产修复前失败：完整 Integration 基线为原有 228 项通过、新增 2 项失败；timer 阶段的 Unit 运行是 434 项通过、新增 2 项失败，后台与 Hosted 探针也分别直接观察到缺失日志和丢失取消原因。一个仅能由 friend test assembly 构造的 internal topology lifecycle 异常候选因普通用户不可达被明确淘汰，没有计入版本。

最终实现校验认证 success sentinel，为每一级 interceptor 创建一次性 continuation，统一观察 faulted Client background task，完整保留 Hosted Stop 清理错误，并复用便携长延迟分片；admission queue delay 在进入运行期前校验原生 timer 上限。修复后非增量 Release 构建为 0 warning / 0 error，Generator 83/83、Unit 436/436、Integration 230/230、七包打包与全新缓存 package smoke 全部通过。

迁移见 [`migration-0.8.19.md`](migration-0.8.19.md)，性能见 [`performance-0.8.19.md`](performance-0.8.19.md)。
