# #775 最终轮：构建诊断与完整数据归档（2026-10-21 JST）

用户要求本轮为最后一轮，并且不论新诊断是否成功，都把此前全部研究数据更新到issue。
研究台账首先写入#775的评论6295616045；不关闭issue、不合并#862、不推进第二阶段。

## 固定的最后诊断

恰好两个新的VM job，各自使用固定SDK 10.0.113、旧静态6cfb09e7及固定桥接本地候选
8fc84d486c242d67fe05d4cf9962661d8351c779/tree 1bd11d1d56ef951428440ee5cede21af94d1c80e。
桥接由既有5487a21f驱动及锁定补丁重建，不加入BindingSite拆分或新的生产优化。

每臂同一路径两次完整清理bin/obj后的原命令构建，再单独一次生成源码导出构建。
每次先关闭build server；第三次明确增加EmitCompilerGeneratedFiles，仅为诊断，不冒充
原命令的重复样本。保存全部DLL/PDB/deps/runtimeconfig、生成源码/AssemblyInfo、项目assets、
实际binlog、评估后的Compile/Analyzer顺序及关键MSBuild属性。binlog不导出项目imports，
子进程剔除token/secret/password命名的环境变量。没有更改Deterministic、PathMap、编译器、
运行时优化策略、生产src或分配预算。没有新的BDN、吞吐或allocation执行。

只读PE/元数据检查器不加载或执行业务程序集；保留MVID、debug目录、方法IL哈希、符号化
方法体、类型字段布局与custom attribute原值。Token规范化必须记录未解析数；方法体相同
不是整个程序行为等价或性能因果证明。新DLL不能替代上轮没有保存字节的历史DLL。
无论差异复现、未复现或构建/检查器失败，原始结果都保留，不重试选择有利数据。

## 无条件归档

archive job依赖diagnosis但使用always()，即使诊断失败也执行。枚举该研究分支全部
issue-775工作流及研究前缀产物，包括失败作业；逐个下载原始ZIP、验证上游digest并保存
独立研究数据release asset。研究归档标为prerelease且make_latest=false，不是产品版本，
不上传NuGet、不合并、不修改生产源码。该job仅有此归档所需contents/actions/issues权限。

索引保存全部研究workflow运行元数据、原始PR/issue评论快照、可用产物的ID/名称/来源SHA/
退出状态/原始SHA256/永久副本入口，以及所有已保存summary/provenance/identity/失败列表。
现有eng/issue775复现脚本和完整最终driver源码也入索引包。全部原始ZIP独立可取，
不要求在issue评论粘贴数亿个数字；issue内提供完整目录、机器可读清单及全阶段数值台账。

API已删除或过期的产物不能凭链接或哈希恢复；目录必须明确记录缺失而不是声称保存完成。
原始评论可能提到已经不可见的ID，另列缺口。原始ZIP重复出现不增加独立样本数。
若存储/API失败，已完成的对象与错误清单保留，issue仍有先行台账，最终如实报告缺口。
最后一次归档不授权新的性能复验或后续研究轮次。
