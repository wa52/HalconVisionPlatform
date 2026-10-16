# 原图多位置分类试运行

复用平台的 `HalconClassificationExecutor` 和模型缓存：从 `P*.hobj` 读取区域外接矩形 → 验证坐标和图像尺寸 → 原图多 ROI 裁剪推理 → 输出各位置的类别、置信度、次高类别及完整评分 → 逐张释放原生输出，批处理结束清理模型缓存。

输入通过命令参数提供，原图、ROI 文件和训练模型均为只读。输出目录保存新建 `.hvproj` 方案、ROI 坐标、原图叠加示例、CSV、逐图 JSONL 和汇总 JSON。默认使用 GPU 0，置信度阈值 0.8（写入新方案，可在平台属性中修改）。原生推理与裁剪逻辑来自平台执行器，预览使用 WPF DrawingVisual；不使用 HALCON 显示算子。

可用 `--device=cpu` / `--device=gpu`、`--device-index=0` 和 `--confidence=0.8` 配置推理设备及阈值。

```powershell
dotnet restore tools/HalconClassificationTrial/HalconClassificationTrial.csproj --disable-parallel -m:1 -p:NuGetAudit=false
dotnet build tools/HalconClassificationTrial/HalconClassificationTrial.csproj -c Release --no-restore -m:1
dotnet tools/HalconClassificationTrial/bin/Release/net8.0-windows/HalconClassificationTrial.dll <模型目录> <图像文件夹> <ROI文件夹> <新输出目录> --inspect-only
dotnet tools/HalconClassificationTrial/bin/Release/net8.0-windows/HalconClassificationTrial.dll <模型目录> <图像文件夹> <ROI文件夹> <新输出目录>
dotnet tools/HalconClassificationTrial/bin/Release/net8.0-windows/HalconClassificationTrial.dll <模型目录> <图像文件夹> <ROI文件夹> <已有输出目录> --render-examples
```

- `--inspect-only` 只检查 ROI、输出原图框选图与平台方案，不加载推理模型。
- `--render-examples` 读取已有 JSONL 结果，生成普通类别、NG 类和最低置信度示例，不重新推理或改写方案。
- 每张图与首张尺寸必须一致；不自动缩放坐标。非矩形区域仅使用外接矩形，`rectangleCoverage` 记录原区域在矩形内的占比，1 表示完整矩形。
- `results.csv` 可直接用 Excel 打开，`results.jsonl` 保留完整类别评分。`summary.json` 汇总各位置的类别数量、平均/最低置信度、低置信度和位置类别不符数量。
- “位置类别一致”只检查 Pn 或 NGPn 是否对应 ROI 名称，不是人工真值或准确率。工具不从类别名称自动执行产线 OK/NG 判定。
- 首张包括模型加载和预热；核心推理耗时与整链路耗时分别保存。每次使用新的输出目录，以免与历史记录混淆。
- 训练数据同源或已在训练划分中使用的原图，不能作为独立泛化评估。没有逐图真值时不计算准确率。
