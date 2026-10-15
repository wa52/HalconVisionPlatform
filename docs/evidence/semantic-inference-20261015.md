# 语义分割真实推理检查

日期：2026-10-15。

## 环境与输入

- Windows x64 / .NET 8 / HALCON 24.11.1.1。
- GPU：NVIDIA GeForce RTX 5060 Laptop GPU，HALCON GPU 设备索引 0。
- 模型：HalconTrainingPlatform 导出的 NH320标签 / run_20261013_090850_4e9f1175；512×512 输入，类别 0 background 和 1。
- 从模型 split.json 的 test 集中取前 3 张实际图片；执行真实 WorkflowExecutionEngine 的“读取图像 → 语义分割检测”。
- 默认类别/面积参数，未修改训练模型、预处理字典、训练数据或原有检测项目。

## 结果

| 样本 | 原图/输出尺寸 | 区域数 | 区域面积（像素） | ApplyDlModel 耗时 | 整条链路耗时 |
| --- | --- | --- | --- | --- | --- |
| P1/25.jpg | 221×252 | 1 | 4686 | 61529.9 ms | 74376.8 ms |
| P2/62.jpg | 226×259 | 1 | 3951 | 52.4 ms | 904.9 ms |
| P1/29.jpg | 221×252 | 1 | 4708 | 53.2 ms | 843.2 ms |

3 张均返回 DETECTED，掩膜和预览 PNG 均成功保存，输出对象释放后 IsInitialized 为 false，检查程序退出码 0。首次运行包含明显的 GPU 初始化开销；不能把后续 52–53 ms 推理时间当作整条检测耗时。当前每帧仍加载并清理模型，未实现模型会话缓存。

本轮修复：实际 HALCON 标签图类型不能直接导出 PNG。最近邻还原后将标签转为 byte（模型类别 ID 已限制为 0–255），与训练平台 SemanticTester 的导出方式一致。

结果仅证明指定模型、三张图片及当前 GPU 的推理数据通路可运行，不是准确率评估，也不代表 CPU、全部样本、长时间连续运行或 WPF 全交互验收。

本机证据保存在 artifacts/semantic-inference-check/results-fixed/result.json 及对应 PNG；不上传真实图片、模型和数据集。首次执行使用临时 Probe，后续复用检查源码已收录在 tests/HalconVision.SemanticChecks。

## 复现

```powershell
dotnet build tests/HalconVision.SemanticChecks/HalconVision.SemanticChecks.csproj -c Release
dotnet tests/HalconVision.SemanticChecks/bin/Release/net8.0-windows/HalconVision.SemanticChecks.dll <模型目录> <数据集.hdict> <输出目录>
```

模型目录需包含训练时的 split.json；数据集用于只读选择实际测试图片，生产推理节点不需要训练数据集或 split.json。
