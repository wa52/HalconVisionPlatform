# HalconVisionPlatform

基于 HALCON 24.11 x64、.NET 8 和 WPF 的节点式视觉检测工作台。

## 当前功能

- **项目**：新建空白 `.hvproj` 单文件项目，支持多个流程、保存和重新打开；双击关联的项目文件直接加载默认流程、节点、连线和参数。兼容旧版项目格式。
- **流程编辑**：节点库、紧凑节点、端口拖动连线、节点移动、复制、Delete 删除、撤销/重做、缩放、平移、流程校验和依赖顺序执行。
- **图像读取**：单张图片或文件夹序列；文件夹按文件名顺序读取，每次单次运行切换下一张。目录不递归扫描。
- **二值化与 Blob**：HALCON Threshold；RGB 图像先灰度转换。Blob 使用 Connection、SelectShape 和 CountObj。
- **直线查找**：在图像上绘制矩形搜索范围，配置方向、测量卡尺和边缘参数，通过 HALCON 2D Metrology 寻边并拟合直线。显示搜索矩形和延伸到矩形边界的拟合线，输出坐标、角度、长度、评分及 FOUND/NOT_FOUND 状态。
- **语义分割检测**：加载 HalconTrainingPlatform 导出的模型目录，使用保存的 preprocess.hdict 和官方 preprocess_dl_samples，执行 HALCON 深度学习推理。分割掩膜最近邻还原到原图尺寸并转为 byte，按类别和面积筛选区域，输出预览、掩膜、区域、数量、面积与对象坐标。支持 GPU/CPU 选择；已通过 NH320 模型 3 张真实图片的 GPU 推理和输出释放检查，准确率、CPU 及长期稳定性仍待验收。[验证记录](docs/evidence/semantic-inference-20261015.md)
- **多 ROI 裁剪推理**：语义分割和分类节点支持最多 64 个命名矩形，可新增、重绘、启用/禁用和删除，随项目保存。各 ROI 独立裁剪推理，分割结果映射回原图并保留独立掩膜，检测模式显示各区域结果；重叠区域分别统计。空列表使用全图，旧单 ROI 项目保持兼容。裁剪会改变模型输入内容，效果不保证与全图一致。
- **分类检测**：对接训练平台分类模型，通过 roiResults 输出各 ROI 的类别、置信度、全部类别评分及可配置的低置信度状态，支持原图叠加类别、置信度和 ROI。单 ROI 保留原标量端口；多 ROI 标量为空，避免误取某一区域代表全部。2026-10-16 多 ROI 与分类功能已编译接入，尚未进行实际推理验收。[实现说明](docs/DL_ROI_CLASSIFICATION.md)
- **相机采集**：选择本机可用的 HALCON 采集接口、扫描设备、连接/断开、连接预览。DirectShow 用于 Windows 摄像头；采集节点每次执行取一帧。
- **运行控制**：单次运行、连续运行和停止；连续执行支持文件夹回放及相机流程，防止重复启动。
- **图像交互**：适应窗口、缩放按钮、滚轮缩放和拖动平移；可查看节点输出图像。
- **检测模式**：选择一张底图，在同一视口组合多个节点的图形、判定和数值。常用字段为评分、角度、长度、数量；“高级数据（坐标）”折叠区提供直线起终点坐标。完整节点输出仍可供后续计算使用。
- **配置与资源**：底图、叠加节点和字段选择随项目保存；复制/导入流程重映射节点引用。HALCON 运行对象不写入项目文件，重跑、项目切换和关闭时集中释放。

## 使用方式

1. 新建或打开 `.hvproj` 项目，从节点库添加读取图像或相机采集节点。
2. 连接二值化、Blob 或直线查找节点，在属性区配置参数；直线查找先绘制矩形。
3. 点击“单次运行”检查链路，使用“连续运行”和“停止”控制循环。
4. 进入“检测模式”，选择底图，展开“叠加节点与显示字段”，勾选多个节点及所需数据。
5. 保存项目。显示选择与算法参数一同保存在项目文件内。

深度学习节点的多 ROI 入口为“节点属性 → 多 ROI 检测范围 → 新增 ROI”，随后在节点图像区拖动绘制。检测模式选择 mask 端口时，可切换各 ROI 的掩膜；检测结果/输出参数提供 roiResults 明细。列表非空且全部禁用会报错，清空列表才表示全图推理。

语义分割链路为“读取图像/相机采集 → 语义分割检测”。在属性区选择训练模型目录，设置目标/背景类别 ID 及面积条件；检测模式中可在原图上叠加分割区域轮廓。详见 [语义分割节点](docs/SEMANTIC_SEGMENTATION_NODE.md)。

## 构建与部署

要求 Windows x64、.NET 8 SDK 和本机 HALCON 24.11 x64 运行环境。MVTec 包版本为 `24111.0.0`。

```powershell
dotnet build src/HalconVision.App/HalconVision.App.csproj -c Release
dotnet run --project src/HalconVision.App/HalconVision.App.csproj
```

发布并更新桌面快捷方式及 `.hvproj` 文件关联：

```powershell
dotnet publish src/HalconVision.App/HalconVision.App.csproj -c Release -m:1 -o artifacts/publish
powershell -NoProfile -File tools/InstallDesktop.ps1 -PublishedDirectory artifacts/publish
```

脚本默认安装到 `artifacts/desktop`；也可显式指定项目 artifacts 下的安装目录。更新后关闭旧版本，再通过快捷方式或项目文件启动。脚本不覆盖 Windows 受保护的默认应用选择；必要时在“打开方式”中手动选择本平台。

## 验证与边界

已完成独立 HALCON 算子调用、真实图片直线查找、WPF 显示及生命周期回归。2026-10-12 同图叠加版本通过 47 项真实图片检查和 39 项生命周期检查；包含 200 次执行释放 400 个旧图像/区域、连续刷新、反复启停和关闭清理。2026-10-13 文档同步了后续的常用/高级字段调整；这次文档更新未重新运行测试。

相机生命周期回归使用 HALCON 官方 File 采集接口，不能替代真实物理相机及驱动验收。相机可用性取决于本机接口、驱动、设备占用和配置。PLC/IO、自定义 HDevelop 程序导入、生产追溯、权限和长时间产线验收尚未完成。语义分割内部使用 HDevEngine 执行官方预处理，当前只支持 full_domain，每次执行加载并清理模型，未实现模型会话缓存。

图像路径仍为本机路径。叠加图形要求共同图像来源及匹配尺寸；深度学习节点内部 ROI 已映射回原图，通用裁剪、旋转、重采样节点间的坐标变换尚未实现。找到/未找到、分类结果与执行成功是不同状态，结果应结合实际样品和参数判断。

## 文档

- [架构与资源生命周期](docs/ARCHITECTURE.md)
- [开发计划及当前进度](docs/PLAN.md)
- [运行环境与验证边界](docs/COMPATIBILITY.md)
- [开发历史](docs/LOOP.md)
- [VisionInspection 显示实现参考](docs/VISION_INSPECTION_DISPLAY_REFERENCE.md)
- [同图叠加验收](docs/evidence/overlay-display.md)
- [UI 与生命周期验收](docs/evidence/ui-lifecycle-20261012.md)
- [HALCON 独立调用证据](docs/evidence/HALCON_SMOKE_TEST.md)
