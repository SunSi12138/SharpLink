# #775 最终构建诊断结论与研究收尾（2026-10-21 JST）

## 结论

静态Unary/OneWay原型已经跑通，发现的persistent解码BoundMethod丢失已修复并保留。
全阶段性能研究仍未证明恢复原始dev基线，不集成未验收候选、不合并#862、不关闭#775，
不授权动态Replace/Receipt/Streaming。用户指定这是最后一轮；本轮不再调优或增加性能样本。
完整阶段台账已直接写入issue：
https://github.com/SunSi12138/SharpLink/issues/775#issuecomment-6295616045

## 最后一项诊断的实际执行

运行39457744667，驱动c61f39ae3cfa584fb8d524b654d3c491fdbb742a，两台build-diagnosis均success。
固定旧静态6cfb09e7及既有桥接本地候选8fc84d486c242d67fe05d4cf9962661d8351c779，SDK10.0.113。
桥接候选仍是固定补丁在隔离checkout内生成，不是远程分支源码。

每台每臂两次清理后同路径普通构建，另一次EmitCompilerGeneratedFiles=true导出构建。
共12次构建、每台每臂首次一次443项smoke，零BDN/吞吐/分配测试新增样本。
第三次的生成源码导出是不同诊断输入，不能算作原命令相同输入重复样本。

两台分别为AMD EPYC9V74、Intel Xeon Platinum8573C；不是同CPU性能实验，且本轮根本不计性能。
保存了实际DLL/PDB/依赖配置、生成源码、AssemblyInfo、assets、编译项/Analyzer顺序、关键属性及binlog。
没有更改Deterministic、PathMap或排序策略，没有通过调整编译输入制造“已修复”结论。

## 字节与IL复核

每次构建11个SharpLink托管程序集。旧臂17,327个方法定义、候选17,328个方法定义
（包含无方法体定义，不能称作全是可执行方法）；检查器未解析token数均为0。

普通构建1与2共44组同臂、同路径DLL比较：41组逐字节一致，3组不同：

| 宿主 | 臂 | 文件 | 不同字节数 | 标识/调试区外差异 |
|---|---|---|---:|---:|
| 1 | candidate | SharpLink.Server.dll |72|0|
| 2 | candidate | SharpLink.Client.dll |72|0|
| 2 | candidate | SharpLink.Server.dll |72|0|

三个不同DLL均同长度，全部差异准确落在COFF时间标记4字节、metadata #GUID中的MVID16字节、
debug时间标记4字节、CodeView GUID16字节、PDB checksum32字节，共72字节。
其他metadata流和这些范围之外的全部DLL字节一致，不只是抽查一两个方法。

所有同臂原命令重复的raw方法IL、解析后局部变量/EH/符号化方法体、类型字段布局、
custom attribute行均一致；构建1/2保存的obj输入hash字典也一致。
PDB本身存在#Pdb/#~/#Blob差异，未进一步确证其排序来源或具体编译器缺陷。

跨宿主同臂对照的全部DLL字节不同，但路径包含不同host编号，不能假定完整编译输入相同。
所检查raw IL和规范化方法体仍一致。生成源码导出构建另外比较，不混入44组相同输入结论。

**这些证据说明本次复现的DLL哈希变化不等于方法IL变化；不是整个程序行为/性能等价证明。**
历史三VM产物只保存了部分DLL哈希，未保存字节；本次只有部分历史hash能匹配，
不能将新DLL倒填成旧轮原始文件，更不能据此声称已量化旧OneWay/Unary性能差异的根因。

## 原始诊断产物

- VM1：artifact12280144673，92,824,447字节，SHA256
  `1b756636b457347c027fa767895bbfaf73721de6a2ad74cf0a5a0e7210587350`。
- VM2：artifact12281068641，92,839,084字节，SHA256
  `f48f683fea19db9d892efb0e64cd748d408a515b67d8271be47257f3a253addf`。

两份ZIP均984项，含完整binaries、metadata、命令和输入。最终研究release另保存原ZIP副本及校验目录。

## 归档故障与修正，不是新研究轮

39457744667的两个诊断job成功，但archive-all-research在仓库级/actions/artifacts GET收到HTTP500。
整条workflow因此保留failure；没有把诊断成功当成整条成功，也没有宣称当时已上传全部研究数据。
失败索引artifact12280124923保留了7项issue/PR/运行快照，SHA256
`eb9c055c9048b95f431e080568df66d267c499c3474be3fe204d2cf58c35ec3e`。

仅归档修正改为逐研究运行枚举产物，并补查历史评论中的artifact/run ID；GET临时错误仅有界重试。
不重新执行成功构建诊断或任何性能测试。原始ZIP逐个保存SHA256核对副本，
索引同时存储所有阶段summary、原始评论快照、源码和复现工具。缺失/过期/失败必须明列。
该研究数据release使用prerelease和make_latest=false，不是产品发布，不含产品包。
最终实际保存数量与链接以issue自动追加的归档结果和机器可读manifest为准。
