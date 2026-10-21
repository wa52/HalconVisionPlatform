# 目标检测输入与负样本对照

用于1111 / P1～P6方案。引用已验证发布目录的DLL，不构建工作区中进行中的应用改动。输入方案、原图、模型和训练标签只读；输出目录必须全新。

```powershell
dotnet build tools/HalconDetectionInputTrial/HalconDetectionInputTrial.csproj -c Release -p:ValidatedRuntimeDir=D:/checked/App
dotnet tools/HalconDetectionInputTrial/bin/Release/net8.0-windows/HalconDetectionInputTrial.dll D:/checks/1111.hvproj D:/checks/new-output probe
```

模式：

- `repro`：仅执行第一张原图，捕获P1/P4/P5/P6四区域未检出的历史现象。捕获成功返回3，否则返回0；返回3不是算法错误或漏检结论。
- `probe`：同图比较原ROI、整图、未经矫正整图、置信度0.05、ROI四周扩大64像素。每次只改变所记录因素，所有结果另写JSON。低阈值仅用于诊断，不能据此改写原方案或宣称效果改善。
- `audit`：在 `audit` 后再提供“按类别放置的裁图目录”和“正常对照原图完整路径”。读取模型manifest指向的COCO导出标签，核对第一张六个裁图的尺寸、原图裁图与训练裁图各通道MAE≤3、分类目录文件与训练导出文件SHA256相同，检查输出与该标签一致；随后用同一基准执行正常对照，要求六区各检出1个目标。只支持该检查数据中能按尺寸及 `_0.jpg` 唯一定位的六类样本，不能推广成通用数据映射算法。

```powershell
dotnet tools/HalconDetectionInputTrial/bin/Release/net8.0-windows/HalconDetectionInputTrial.dll D:/checks/1111.hvproj D:/checks/new-audit audit D:/crops D:/raw/control.jpg
```

三种模式结束均检查输入哈希不变、两个模型缓存清空。失败写 `error.txt` 并返回非零。模型第一次初始化可能明显慢于后续调用。

这些样本与训练数据同源；标签一致性和正常对照不能替代独立mAP、误接受率、真实产品标签复核或产线验收。无框负样本的正确含义是“不接受普通目标”，不要求模型返回不存在的NG类别。
