# HalconVisionPlatform

独立的 HALCON 24.11 x64 / .NET 8 WPF 离线视觉检测平台。当前实现用于工程开发和本地图像验证，不代表相机、PLC、授权环境或产线验收已完成。

## 当前能力

- 打开单张图像，显示图像尺寸和像素坐标。
- 在 `HSmartWindowControlWPF` 视窗拖拽绘制矩形 ROI。
- 配置灰度阈值、连通域面积上下限和目标数量，运行 Blob 检测并显示 ROI/结果框。
- JSON 方案加载与保存；单图检测保存原图副本和结构化结果。
- 选择目录执行顺序批量检测；每张图单独记录 OK、NG 或 ERROR，错误图不会中止整批；结束后保存整批 JSON 汇总。
- 多选图像循环回放用于交互调试；回放结果只显示在界面，不作为批量或生产追溯记录。

数据存储在 `%ProgramData%\HalconVisionPlatform\` 下的 `recipes`、`results` 和 `batches` 目录。所有 HALCON 图像算子位于 `HalconVision.Halcon` 适配层，领域结果不暴露 HALCON 原生对象。

## 构建与运行

要求 Windows x64、.NET 8 SDK、HALCON 24.11 x64 Runtime，以及由本机环境提供的 HALCON 运行配置。HALCON 包版本为 `24111.0.0`。

```powershell
dotnet build .\HalconVisionPlatform.sln
dotnet run --project .\src\HalconVision.App\HalconVision.App.csproj
```

批量检测前先打开样本图像或加载方案以设置 ROI 和参数，再选择目标目录。批次报告中的图像路径为执行机器上的来源路径；单图结果记录会将输入图像复制到结果目录。

## 已知边界

当前没有真实相机、PLC、HDevEngine、自定义节点流程、测量/标定、用户权限、安装包和产线状态机。界面坐标拖拽与布局尚需在目标显示缩放下人工验收。HALCON 真实调用的本机证据见 [docs/evidence/HALCON_SMOKE_TEST.md](docs/evidence/HALCON_SMOKE_TEST.md)；阶段与限制见 [docs/LOOP.md](docs/LOOP.md)。

项目路线：[docs/PLAN.md on GitHub](https://github.com/wa52/HalconVisionPlatform/blob/main/docs/PLAN.md)。
