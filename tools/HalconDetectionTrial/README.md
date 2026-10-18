# HALCON 目标检测节点实测工具

使用现有六 ROI `.hvproj` 提供原图目录和命名区域，生成新的“读取图像 → 目标检测”方案，通过平台真实注册表、图校验和执行引擎运行。源项目、原图与模型只读。

```powershell
dotnet build tools/HalconDetectionTrial/HalconDetectionTrial.csproj -c Release
dotnet tools/HalconDetectionTrial/bin/Release/net8.0-windows/HalconDetectionTrial.dll <原项目> <HALCON模型目录> <新输出目录> --limit=12 --confidence=0.5 --pattern=OR_*.jpg
```

- 原项目须包含读取图像节点及带 `rois` 参数的节点；区域名称须与模型类别相同。新方案设置 `expectedClass=$roi`，每区默认期望一个对应类别目标。
- 按文件名排序后等距抽样，最多 `limit` 张；不是默认全目录精度验收。`--pattern` 可限制原图，避免混入派生预览图。
- 输出新项目、逐图完整节点 JSONL、逐 ROI CSV、前三张原图叠加 PNG 和汇总 JSON。每次使用新的输出目录。
- 保留错类候选，但不作为对应 ROI 的有效检出；空结果不使用旧框。输出框按官方 full_domain rectangle1 半像素缩放约定还原，并检查落在对应 ROI 边界内。
- 检查项目保存恢复、逐图原生输出释放、启动前取消、阈值 1 的空结果、恢复阈值后的缓存参数重设、错误类别导致失败且不保留旧输出、失败会话移除及最终缓存清空。
- 阈值 1 的空结果检查依赖所选首张样本确实没有得分等于 1 的候选；不符合时明确失败，不伪造空结果。
- 测试图可能与训练数据同源，不计算无真值的准确率。蓝色 NG 需要模型真正具有 NG 类，不能由普通类未检出推断。
