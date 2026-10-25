# 比例标定与单位换算专项

```powershell
dotnet run --project tools/HalconUnitConversionTrial/HalconUnitConversionTrial.csproj -c Release -- artifacts/unit-conversion-new-check
```

Windows x64、.NET 8、HALCON 24.11；输出目录必须不存在，避免覆盖。程序生成合成条带与空图、完整输出、参考参数已填写的演算方案和未确认方案，不修改用户原始项目/图像/模型或 HALCON 安装。

覆盖默认未配置、正负零长度、尺寸和帧身份、错误单位连线、无单位评分、重复毫米换算、数值溢出/下溢、缺值不回退旧结果、100 次正常/缺值交替、取消、实际找线/测量/毫米公差链、20 次完整释放、保存恢复、数据表和任务编辑器。比例 0.1 mm/px 仅为演算，不是硬件标定或实物精度验收。

输出 `summary.json`、`actual-result.json`、项目与测试图片；异常写 `error.txt`。共享引擎单位/帧元数据变更还需回归既有节点、任务、位置链和生命周期。
