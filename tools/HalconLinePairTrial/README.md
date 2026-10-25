# 线线测量专项检查

Windows x64、.NET 8 与 HALCON 24.11 本机运行环境：

```powershell
dotnet run --project tools/HalconLinePairTrial/HalconLinePairTrial.csproj -c Release -- artifacts/line-pair-new-check
```

必须指定不存在的输出目录。程序调用真实 HALCON/WPF，生成两条亮条带图、空图、检查项目、完整输出与 `summary.json`；异常写 `error.txt`，不修改用户原始文件。

覆盖平行、交叉、共线分离/重叠、延长线相交但线段分离、交换输入和反转端点、随机方向、最近点位于有限线段、原图不污染、错帧/退化/无效域拒绝、取消、100 次正常/失测交替、15 次全链路释放、真实两次找线/任务判定、保存恢复及结果表/任务编辑器。
