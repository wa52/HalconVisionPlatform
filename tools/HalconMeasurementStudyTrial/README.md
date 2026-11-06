# 测量验证专项

Windows x64、.NET 8、HALCON 24.11。先运行 `HalconUnitConversionTrial` 生成独立合成工程，再传入该工程路径。只创建新的输出目录及项目副本，不修改源工程和图像。

```powershell
dotnet build tools/HalconMeasurementStudyTrial/HalconMeasurementStudyTrial.csproj -c Release -m:1
dotnet tools/HalconMeasurementStudyTrial/bin/Release/net8.0-windows/HalconMeasurementStudyTrial.dll artifacts/study-new artifacts/unit-fixture-new/合成图毫米换算检查.hvproj
```

首个参数必须不存在；第二个是 `HalconUnitConversionTrial` 生成的含 `blank.png` 与线条图的 `.hvproj`。

覆盖统计已知序列、缺值/异常/零/负数、重复和过期结果、跨流程/错帧/尺寸变化、极端数值、CSV 转义/固定格式、JSON 配置快照，以及真实 HALCON 测量→换算→窗口按钮记录→空图失测。验证 780×600、1000×720、1180×800 内容边界及长错误信息。执行窗口实际使用的报告保存方法，读取 JSON/CSV、检查 BOM 和导出失败恢复；正常关闭所有测试窗口和资源。

输出 `summary.json`、已知序列与实际链报告、新建合成项目；失败写 `error.txt`。这些是合成演算与软件回归，不证明实际相机精度。生产程序的窗口操作与截图另见 `docs/evidence/2026-10-26-measurement-study.md`。
