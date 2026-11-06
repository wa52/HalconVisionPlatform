# 检测运行归档基础

2026-11-06：存储 API 已接入工作台自动保存和历史查询，入口为“项目 → 运行归档”。操作与边界见[工作台说明](RUN_ARCHIVE_WORKBENCH.md)。

`WorkflowRunArchiveStore.Save` 接收项目身份、执行时流程快照和完成结果；`ProjectStore.SaveRun` 同样要求显式传入执行快照，避免从项目旧配置猜测本轮参数。调用方必须提供真正执行的快照；归档不能自行证明调用方没有传错参数。

归档保存流程、参数、接线、检测任务、RunId、时间、节点结构化结果、日志、错误及产品判定。运行时图像/模型对象不在序列化契约内。当前不打包原图、模型或算法二进制，不能仅凭归档独立重放检测；文件路径所指资源仍需另行保存。

文件为版本 1 的 JSON 信封，包含数据字符串和 SHA256。读取时检查版本、哈希、项目/运行身份、节点归属、重复节点及未完成节点。SHA256 用于发现损坏，不是签名，也不能抵抗同时修改数据和哈希的恶意篡改。64 MB 上限；同一 RunId 不可覆盖。保存采用同目录唯一临时文件、刷盘和无覆盖移动，失败清理临时文件。

检查：主程序 Release 构建 0 警告/0 错误；专项 20 项通过，覆盖成功/失败/取消恢复、重复覆盖拒绝、损坏 JSON、数据改动、跨流程、空身份、未完成节点及临时文件清理。特殊运行时对象的属性读取会抛错，仍能归档，证明该契约没有序列化运行时对象。首轮检查工具未捕获 InvalidDataException，修正检查工具后通过，未修改完整性保护。

复跑（输出目录必须不存在）：

```powershell
dotnet build tools/HalconRunArchiveTrial/HalconRunArchiveTrial.csproj -c Release -m:1
dotnet tools/HalconRunArchiveTrial/bin/Release/net8.0/HalconRunArchiveTrial.dll artifacts/run-archive-new
```

后续仍需原图/模型哈希与资源保留、全局检索和复现入口，以及真实磁盘故障与现场验收。当前不是完整的生产追溯方案。
