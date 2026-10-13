# 语义分割检测节点

## 流程与所有权分析

训练来源为独立项目 HalconTrainingPlatform 的 SemanticTrainer / SemanticTester。本平台只读取导出的模型目录，不改训练程序或模型文件。

流程：输入原图 → 校验模型目录与校验和 → 加载 model.hdl 和 preprocess.hdict → 校验 segmentation 类型、类别及 full_domain → 选择 GPU/CPU 设备 → 官方 preprocess_dl_samples → ApplyDlModel → 最近邻还原标签到原图尺寸 → 按类别提取连通域与面积筛选 → 输出预览、掩膜、区域和结构化数据。

模型目录包含 manifest.json、model.hdl、preprocess.hdict，可选 classes.json 和 checksums.json；存在校验和时必须匹配。类别来源为 manifest，存在 classes.json 时要求一致，并与实际模型类别核对。只支持训练平台当前 full_domain 的坐标映射，不猜测 crop/padding 还原。

每次执行独立加载并清理模型，不缓存 GPU 模型。速度优先的模型会话缓存属于后续优化。输入图像借用运行上下文，输出图像/区域转移到 WorkflowExecutionRun；样本、预测字典、元组及临时区域在当前调用释放，模型在 finally 清理。取消在算子前后检查，不能抢占正在执行的 HALCON 原生调用。

## 参数与输出

- 模型目录：选择训练输出目录或其中 manifest.json / model.hdl。
- 推理设备：GPU 或 CPU；设备索引默认 0，不自动切换设备。
- 背景类别 ID：默认 0。目标类别 ID 留空表示全部非背景类，也可填逗号分隔 ID。
- 连通域最小/最大面积：单位为原图像素。
- 输出 preview、mask、regions、count、area、objects、classes、status。每个对象包含类别名称/ID、面积、面积比例、中心和外接矩形。
- 输出参数表展开对象及类别明细（最多前 200 项）；选中数组行可复制完整 JSON。限制只作用于 UI 展开，算法结果和下游数据不截断。
- 状态为 DETECTED 或 EMPTY；运行错误由流程引擎记录 Failed。不把“有区域”自动等同产品 OK，不伪造置信度评分。
- mask 包含全部原始类别标签，regions/objects 为筛选后的目标区域；类别面积统计为筛选前各类像素统计。

连接“读取图像/相机采集 → 语义分割检测”。选中节点可查看预览和掩膜，在检测模式中选择输入原图并勾选分割节点图形，显示目标区域轮廓。文件夹回放与连续运行沿用现有流程调度。

本轮仅编译集成，未运行真实模型推理测试；物理设备、显存、预测效果及性能需单独验收。模型、训练数据和依赖运行库不上传 GitHub。
