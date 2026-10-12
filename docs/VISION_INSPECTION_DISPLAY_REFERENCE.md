# VisionInspection 图像显示参考

参考仓库：wa52/VisionInspection。已核对提交 `843cf6da8b059cb6afa4814a8598b1f159d7acb4` 的实际代码，并将同图叠加机制适配到 HALCON 平台。

## 核心机制

1. 每个节点通过 NodeResult 分别提供 OutputImage、Annotations、Values、Decision。PipelineRunContext 按节点名称索引图像与结果。
2. OverlayDisplayNode 独立选择底图 source，勾选 overlay_nodes，汇总所选节点已有的标注；按 show_decision/show_values 添加判定和数值摘要。默认不叠加未勾选节点。
3. UI 使用 WPF Image 显示 BitmapSource，Canvas 绘制矩形、多边形、点和文字。图像与 Canvas 共享缩放和平移变换；坐标由 UniformFit 映射。鼠标滚轮缩放，中键平移，按钮适应窗口。
4. 节点缩略图切换底图；连续运行保留用户选中的节点。自动显示优先选择叠加显示节点，再选择检测节点。
5. 图像从 OpenCV Mat 转成冻结的 WPF 位图；按引用去重后释放 Mat。这个图像所有权方式不能直接套用到本平台仍由 WorkflowExecutionRun 持有的 HALCON 对象。

## 实际代码

- [节点结果与图形契约](https://github.com/wa52/VisionInspection/blob/843cf6da8b059cb6afa4814a8598b1f159d7acb4/Detection/IModelNode.cs#L16)
- [同图叠加节点](https://github.com/wa52/VisionInspection/blob/843cf6da8b059cb6afa4814a8598b1f159d7acb4/Detection/OverlayDisplayNode.cs#L12)
- [多选叠加来源 UI](https://github.com/wa52/VisionInspection/blob/843cf6da8b059cb6afa4814a8598b1f159d7acb4/MainWindow.NodeEditor.cs#L1420)
- [图形绘制](https://github.com/wa52/VisionInspection/blob/843cf6da8b059cb6afa4814a8598b1f159d7acb4/MainWindow.RoiOverlay.cs#L1134)
- [缩放、平移、结果刷新](https://github.com/wa52/VisionInspection/blob/843cf6da8b059cb6afa4814a8598b1f159d7acb4/MainWindow.Results.cs#L33)
- [位图转换与释放](https://github.com/wa52/VisionInspection/blob/843cf6da8b059cb6afa4814a8598b1f159d7acb4/UiExtensions.cs#L20)
- [自动显示目标选择](https://github.com/wa52/VisionInspection/blob/843cf6da8b059cb6afa4814a8598b1f159d7acb4/Detection/Pipeline.cs#L461)

## HALCON 平台的实现

DetectionContentView 选择底图端口，通过 DetectionOverlaySettings 多选节点，并分别控制图形、判定及重要数值字段。常用显示选项提供评分、角度（°）、长度（像素）、数量，按实际输出提供；“高级数据（坐标）”折叠区提供直线起终点行列坐标。图像元数据和重复内部字段隐藏，节点完整输出及计算端口不变。DetectionOverlayScene 将当前运行的结构化结果转换为托管图形和文本快照，一个 HSmartWindowControl 图像视口同时显示全部所选内容。

默认使用读取图像/相机采集原图作为底图。旧版无 Overlays 的多内容布局在载入时转换为同图叠加配置；转换只修改内存，用户保存后写入项目。额外的参数表或独立图像内容仍可手动添加。

保留 HSmartWindowControl 及 HALCON 算法接口，复用 GeometryOverlay 的搜索矩形及拟合直线，也支持 HRegion 边界和 HXLDCont 轮廓。在 WPF Canvas 上按 HALCON GetPart 范围映射坐标；缩放和平移同步更新图形位置，笔画和文字保持屏幕尺寸。当前校验共同图像源节点与尺寸；尚不支持跨裁剪、旋转、重采样节点的坐标变换。

显示层不修改原始 HImage。ROI、结果线和文字分别保存为托管快照；来源、图层开关和字段随项目保存，复制/导入流程时重映射节点 ID。运行结果仍由 WorkflowExecutionRun 拥有并集中释放，提取边界产生的 HALCON 临时对象同步释放。

实际验收见 `docs/evidence/overlay-display.md`。
