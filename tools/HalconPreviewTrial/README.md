# 检测预览复制、像素与所有权检查

Windows x64、.NET 8、HALCON 24.11。调用当前生产代码的内部 `OwnedPreviewImage`，不加载神经网络、不修改原图和安装、不生成模拟检测结果。

```powershell
dotnet restore tools/HalconPreviewTrial/HalconPreviewTrial.csproj
dotnet build tools/HalconPreviewTrial/HalconPreviewTrial.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
dotnet tools/HalconPreviewTrial/bin/Release/net8.0-windows/HalconPreviewTrial.dll '<真实RGB图片>' '<全新输出目录>' --performance
```

拒绝覆盖输出目录。原图只读，输入文件 SHA256 前后核对。小图覆盖普通 RGB、灰度、共享 RGB 通道、real、uint2、缩小有效域；64 个重叠及部分越界框按绿、红、蓝、黄顺序绘制。验证完整像素和域、颜色数值、上游不变、其他预览不被污染、空预览、取消、对象所有权转移、重复释放和多对象区域。

普通 RGB 与原 `PaintRegion` 路径逐像素比较。灰度及共享通道使用先独立复制通道再 `PaintRegion` 的参考实现，因为旧共享通道路径存在颜色丢失：绿色 `[50,205,50]` 会成为 `[50,50,50]`。四种颜色都有独立像素值断言，不仅比较两段相似代码。

性能使用原图完整尺寸，分别画 2 / 64 个标注。相同进程内交替运行旧路径与产品路径，各 10 次，排除前 2 次，取剩余样本中位数。`--performance` 要求 64 标注绘制耗时小于旧路径一半，不应用于跨机器 CI。计时不包括像素哈希，也不包括推理；64 个标注不等于执行 64 次模型推理。

输出 `summary.json` 包含断言、逐次耗时、颜色诊断和原图哈希，失败写完整 `error.txt` 并返回非零。不能用此工具代替真实节点、WPF 界面、连续运行或模型准确率验收。真实链路像素检查另在 HalconP1P6TaskTrial 的 `verify` 中执行。
