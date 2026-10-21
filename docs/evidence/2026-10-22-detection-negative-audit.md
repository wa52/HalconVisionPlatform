# 首张四处未检出的负样本核对 · 2026-10-22

## 结论

上轮1111首张中的 P1/P4/P5/P6 四个未检出结果，符合当前训练数据的无框负样本标签，**不能计作四个已证实的漏检**。P2/P3有普通目标框，实际检出；另一张正常原图通过同一模板、位置基准及六ROI检测链后，六区各检出1个目标。

本轮没有修改应用业务源码、模型、阈值或ROI，没有重新训练。补充了可重复运行的诊断/标签回归工具，并修正文档中的未决解释。模型仍为 `bootstrap_unvalidated`，没有独立mAP或产线精度结论。

## 复现和对照

复用上轮冻结的已验证程序DLL，避免混入工作区另一批检测任务编辑改动。输入仍为 `artifacts/aligned-detection-20261021/1111_定位检测验证.hvproj`，模型 `run_20261020_182230_e5db1fd3`，原阈值0.5。

首次自动复现命令实际执行，输出 P1/P4/P5/P6 `NOT_DETECTED candidates=0`、P2/P3各1个候选，最后输出 `REPRO: P1/P4/P5/P6四个区域未检出。`。`repro`模式捕获这组现象时有意返回3；它是观察信号，不是算法缺陷判定。

```powershell
dotnet tools/HalconDetectionInputTrial/bin/Release/net8.0-windows/HalconDetectionInputTrial.dll artifacts/aligned-detection-20261021/1111_定位检测验证.hvproj artifacts/detection-input-20261022/repro-r2 repro
```

在同图、同模型、同基准下进行输入对照，完整原始结果保存在本机 `artifacts/detection-input-20261022/probe`，随仓库保存[精简对照数据](detection-negative-audit-20261022/probe-summary.json)。

| 设置 | 实际观察 |
| --- | --- |
| 原六ROI、置信度0.5 | P2/P3各1个，其余四区没有候选 |
| 整幅矫正图、置信度0.5 | 无候选 |
| 整幅未矫正图、置信度0.5 | 无候选 |
| 原六ROI、置信度0.05 | 出现多类低分候选；同名目标数量也增加，六区均未满足数量规则 |
| 六ROI各边扩大64像素、置信度0.5 | 只有P2满足当前规则；P3也不再检出 |

因此不将整图推理、降低阈值或扩大ROI当作修复。对照不证明这些设置在其他模型或工位中无效。

## 样本与标签追溯

模型manifest指向 `active-learning/exports/20261020_102230_931bff24/结果.hdict`。该导出的COCO记录1602张裁图、1541个框、61张无框负样本；六种裁图尺寸与当前六ROI尺寸逐项一致。

对首张 `OR_1_N05A00522B_18_37_41.jpg`：当前矫正图裁图与下表训练裁图逐RGB通道比较，各通道平均绝对像素差为1.61～2.94（byte像素范围0～255），符合本工具预设的≤3对应关系检查，存在JPEG再次编码差异，不声称像素逐点相同。分类目录文件与对应训练导出文件的SHA256则完全一致。

| ROI | 原分类目录 / 文件 | 训练导出文件 | COCO框数 | 当前输出 |
| --- | --- | --- | ---: | --- |
| P1 | NGP1/0.jpg | 000001_0.jpg | 0 | 未检出普通目标 |
| P2 | P2/0.jpg | 000310_0.jpg | 1，P2 | 检出1个P2 |
| P3 | P3/0.jpg | 000577_0.jpg | 1，P3 | 检出1个P3 |
| P4 | NGP4/0.jpg | 000015_0.jpg | 0 | 未检出普通目标 |
| P5 | NGP5/0.jpg | 000029_0.jpg | 0 | 未检出普通目标 |
| P6 | NGP6/0.jpg | 000043_0.jpg | 0 | 未检出普通目标 |

分类目录为桌面 `原图/结果`。对应文件按尺寸及 `_0.jpg` 在这份检查数据中唯一定位，再进行哈希和像素核对；不把该特例映射当作通用数据集匹配方法。

“无框负样本”表示不接受普通类目标，不要求模型输出NG类别。当前模型只有P1～P6普通类别；红色未检出与蓝色检出已训练NG类是不同含义。本次核对不是缺陷物理成因分析，也没有重新审核全部负样本。

正常对照为 `OR_1_N063000DCL_14_45_59.jpg`。没有更改位置参考或ROI，完整匹配、矫正和检测执行完成，六区各接受1个对应类别，分数分别约0.9973、0.9988、0.9989、0.9936、0.9987、0.9963。不能把这些分数当作准确率。

## 验证工具与证据

[工具说明](../../tools/HalconDetectionInputTrial/README.md)。构建引用已检查发布目录，Release最终构建0警告、0错误；`audit`最终返回0。RGB比较显式逐通道调用单通道 `intensity`，避免只核对第一个通道。

```powershell
dotnet build tools/HalconDetectionInputTrial/HalconDetectionInputTrial.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false -p:ValidatedRuntimeDir=D:/AiProjects/HalconVisionPlatform/artifacts/aligned-detection-20261021/App

dotnet tools/HalconDetectionInputTrial/bin/Release/net8.0-windows/HalconDetectionInputTrial.dll artifacts/aligned-detection-20261021/1111_定位检测验证.hvproj artifacts/detection-input-20261022/label-audit-final audit C:/Users/feng/Desktop/原图/结果 C:/Users/feng/Desktop/原图/1/OR_1_N063000DCL_14_45_59.jpg
```

输出目录必须全新；复跑需换名。随仓库保存[逐ROI标签核对](detection-negative-audit-20261022/label-audit.json)、[完整执行及只读输入哈希](detection-negative-audit-20261022/audit-summary.json)、[分类裁图哈希对应](detection-negative-audit-20261022/training-label-hash-proof.json)。检查末尾核对源方案、两张原图、模型、预处理、manifest、标签及裁图文件哈希未变，两类模型缓存清空；检查进程正常退出。

## 未覆盖范围

- 样本与训练同源，正常对照只验证当前规则，不能作为独立准确率、mAP、误接受率或产品放行证据。
- 未重新执行此前全部36/542项回归、8小时稳定性、相机/PLC或多DPI视觉验收。
- 本轮没有UI改动。尝试打开独立WPF检查窗口时，界面辅助连接被另一处正在进行的检测任务工作占用；没有完成新增正负样本WPF运行截图验证。仅打开的空白检查窗口已正常关闭，另一处工作窗口保留。实际推理结论来自真实HALCON完整引擎输出，没有用设计图或生成图代替界面截图。
- 桌面入口、原1111方案和模型参数保持本轮开始时状态；进行中的检测任务源码及其截图没有包含在本次提交中。
