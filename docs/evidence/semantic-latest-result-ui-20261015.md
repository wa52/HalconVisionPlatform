# 最新训练模型的检测结果显示

日期：2026-10-15。

## 模型选择

本次按训练平台模型目录的 manifest.json 时间查找，并确认最新模型状态为 reload_and_evaluation_passed：

- NH320标签 / run_20261015_201455_3233ea79。
- completedAt：2026-10-15T20:18:42.6393291+08:00。
- 输入尺寸 256×256，60 epochs，batchSize 8，类别 0 background 与 1。
- model.hdl SHA256：A1ED9B8151E1915BA0E74BD2D9170B4A535684C8287EC3E36EAAAEE7DF3FFD90。

选择后固定记录模型目录，不在运行期间静默切换到其他新模型。

## 实际窗口与结果

通过 SemanticUiChecks 启动生产 MainWindow，加载真实 .hvproj，调用实际“单次运行”命令和 WorkflowExecutionEngine。界面开启检测模式，用原图作底图，叠加分割节点的区域轮廓、判定、数量和面积。没有增加推理阶段或虚构进度展示。

实测 P1/25.jpg（221×252）：

- 状态 DETECTED，目标类别 1。
- 区域数量 1，面积 5644 原图像素，占比 0.101343。
- 中心 (row, column) = (89.4302, 98.6070)。外接矩形 (0, 83)–(182, 115)。
- ApplyDlModel 122.8949 ms，界面记录整条流程 1647.5 ms。这是本次运行值，不承诺所有机器或冷启动相同。
- 实际图像叠加层含 1 个轮廓，并显示检出状态、数量及面积文字；掩膜和预览 PNG 导出成功。

本机项目：artifacts/semantic-latest-display/NH320_最新模型检测.hvproj。

本机证据：同目录的 result.json、ui-check.log、detection-result-ui.png、preview.png 和 mask.png。真实图片、训练模型及含本机路径的项目文件不上传仓库。

窗口使用真实生产视图和交互逻辑，检查程序的 --keep-open 参数让窗口在完成后继续保留，用户可查看结果或继续单次运行。不是准确率评估或长期稳定性测试。

## 复现

```powershell
dotnet build tests/HalconVision.SemanticUiChecks/HalconVision.SemanticUiChecks.csproj -c Release
tests/HalconVision.SemanticUiChecks/bin/Release/net8.0-windows/HalconVision.SemanticUiChecks.exe <模型目录> <数据集.hdict> <输出目录> --keep-open
```

数据集和 split.json 仅用于检查程序选取 test 集的真实样本；生产检测节点只需要模型目录与输入图像。
