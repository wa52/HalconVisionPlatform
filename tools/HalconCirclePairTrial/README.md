# 圆圆测量专项检查

Windows x64、.NET 8、HALCON 24.11 本机运行环境。检查调用真实 HALCON 算子和 WPF，不修改安装或授权。

```powershell
dotnet run --project tools/HalconCirclePairTrial/HalconCirclePairTrial.csproj -c Release -- artifacts/circle-pair-new-check
```

输出目录必须不存在，防止覆盖用户文件。生成合成圆图、空图、OK/故意超差 NG 项目副本、`summary.json` 和完整 `actual-result.json`；失败时另写 `error.txt`。

覆盖解析圆关系、实际最短线段、帧来源和有效域拒绝、空值与异常、预览独立所有权、100 次交替输出、真实两次找圆到统一判定链、20 次完整重跑释放、保存恢复、分组搜索和 WPF 任务配置。界面几何检查覆盖 1180/1366/1580 宽；另需通过真实窗口操作审查最终视觉效果。
