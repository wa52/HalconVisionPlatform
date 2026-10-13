# 架构与资源生命周期

更新：2026-10-13。平台采用五工程模块化单体架构。

```text
HalconVision.App ──> HalconVision.Application ──> HalconVision.Domain
       ├──────────> HalconVision.Halcon ────────> Domain / Application
       └──────────> HalconVision.Persistence ──> Domain
```

## 模块职责

| 模块 | 当前职责 |
| --- | --- |
| Domain | 项目、流程、节点、端口、连线、执行结果，以及 Line2D、GeometryOverlay 等数据契约；不依赖 WPF 或 HALCON |
| Application | 节点注册和执行契约、流程校验、拓扑排序及依赖执行；仍保留早期 InspectionRunner 等兼容实现 |
| Halcon | 图像读取、二值化、Blob、2D Metrology 直线查找、HALCON 采集接口发现和相机句柄管理、图像显示适配 |
| Persistence | 单文件项目保存/恢复、旧格式兼容、流程复制/导入及节点引用重映射；仍保留早期配方/批次结果存储 |
| App | WPF 工作台、节点画布、属性编辑、图像交互、检测模式和运行控制 |

## 项目与执行

新项目使用一个 `.hvproj` 文件保存所有流程和显示设置，不额外创建项目目录。旧版目录项目仍可打开。图像路径当前为本机路径，HALCON 对象不进入项目 JSON。

WorkflowExecutionEngine 校验依赖并执行节点。相机节点每次取一帧，单次和连续运行共用节点执行链路。上游失败时下游跳过；未找到直线返回 NOT_FOUND 和空拟合几何，不伪造零坐标直线。

WorkflowExecutionRun 持有本轮 RuntimeOutputs，并按引用去重释放 IDisposable 对象。工作区替换结果、切换项目及关闭时释放旧运行；相机管理器独立拥有采集句柄，停止/关闭时结束采集并释放。退出等待运行任务与设备操作完成。

## 同图多节点显示

DetectionDashboard / DetectionTile 管理可调整的显示面板，DetectionContentView 选择底图输出端口及多个叠加节点。DetectionOverlaySettings 保存每个节点的图形、判定和字段开关。

DetectionOverlayScene 从本轮结果提取托管显示快照：搜索矩形、拟合直线、区域边界、XLD 轮廓及文本。HSmartWindowControlWPF 显示底图，WPF Canvas 按 HALCON GetPart 范围映射图形坐标。缩放和平移同步更新图形，文字和笔画保持屏幕尺寸。

常用字段为评分、角度、长度、数量；高级坐标折叠显示直线起终点。完整算法输出和节点端口保持独立，可供后续计算使用。显示层不修改原图，也不长期持有 HALCON 临时轮廓。

图形叠加检查共同图像源及尺寸；未来改变图像坐标的节点需扩展显式坐标变换。旧显示布局载入时可转换为同图配置，保存后写回项目文件。

## 待完善

主窗口和工作区 ViewModel 仍需继续拆分。早期检测运行/存储体系与节点体系尚未全部统一。尚未实现独立插件发现与版本兼容协议、PLC/IO、HDevEngine、生产追溯及长时间现场验收。

验收记录：[同图叠加](evidence/overlay-display.md)、[生命周期](evidence/ui-lifecycle-20261012.md)。
