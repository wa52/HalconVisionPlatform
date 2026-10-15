# 裁剪 ROI 与分类检测

## 实现前流程分析

两个深度学习节点共用矩形裁剪约定：ROI 参数存原图行列坐标，端点可反向绘制，越界部分裁到图像边界；完全越界、非有限数和不足 2×2 像素的范围报错。未设置时使用全图。先 CropRectangle1，再使用对应模型目录的官方预处理参数推理，不通过 ReduceDomain 假装裁剪输入。

语义分割将模型标签最近邻还原到 ROI 大小，再平移放回原图大小的掩膜，并限制掩膜有效域为 ROI，避免框外背景参与类别统计。区域、中心、外接矩形天然回到原图坐标。面积和坐标单位仍为原图像素，占比仍以原图总像素为分母。输出 roi 记录有效范围；导出掩膜时还需保留此范围，框外不是有效预测。

分类模型读取 HalconTrainingPlatform 的 ClassificationPipeline：model.hdl、preprocess.hdict、manifest.json 和可选类别/校验文件；调用 preprocess_dl_samples，再读取 classification_class_ids、classification_class_names、classification_confidences。校验实际模型类别映射，输出 Top-1 类别、置信度及所有类别评分；低于配置阈值标记 LOW_CONFIDENCE，不伪造识别结果，也不由类别名称前缀推断产品 OK/NG。

本机已找到最新完成评估的分类模型 run_20261015_204851_6dfa373c（256×256，10 类）。准备的 artifacts/dl-roi-classification/NH320_分割与分类ROI.hvproj 同时绑定该分类模型和最新分割模型，ROI 初始为空，需用户按检测场景绘制；原有项目未改动。模型路径只写入本机项目文件，程序不硬编码机器路径。

分类预览保留原图，并显示 ROI；分类文字通过统一检测面板叠加。所有输出和 ROI 参数随项目保存，沿用文件夹/相机运行链路。两个节点每次执行独立拥有模型和临时资源，成功清理模型后才把输出交给 WorkflowExecutionRun；取消无法抢占原生算子。

ROI 裁剪改变输入内容和缩放比例，实际效果需要样品确认，不能保证与全图结果一致。新功能本轮进行编译集成，尚未对裁剪 ROI 和分类模型做实际推理验收。
