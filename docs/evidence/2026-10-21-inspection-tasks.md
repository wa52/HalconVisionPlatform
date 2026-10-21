# Phase 1：独立 ROI 任务与统一判定验收

实施开始：2026-10-21；最终复查：2026-10-22（北京时间）。原开发基线 `635f2d0`，最终工作区包含仓库后续提交 `e52cd24` 和 `b02b66d`，后者只增加文档及其他专项检查工具。仅修改 HalconVisionPlatform，未改动 HALCON 安装、许可证或其他项目。

## 交付范围

- 区域独立于算法参数；一个 ROI 可关联多个任务，不同 ROI 使用独立算法分支。节点只能属于一个任务。
- 项目菜单只有一个“检测任务”入口，区域、成员、规则及逐任务结果在独立配置窗口；检测模式不增加配置控件。
- 参数覆盖只发生在执行副本，原节点 ROI 不被改写。任务核对同轮、同帧、同空间、未标注、单图及图像边界。
- 产品判定独立于算子执行状态；缺规则、缺结果、未完成、异常和不可信定位不能作为 OK。ERROR 优先于 NG；未配置任务明确显示未配置。
- schema 1 继续可读；含任务的方案/流程保存为 schema 2，旧程序拒绝不支持的 schema。复制/导入重映射区域、节点及规则引用。
- 更改算法、连线、ROI 或规则后旧结论失效；仅运行上游取图不形成完整产品结论。

具体使用及必检/可选语义见 [功能说明](../INSPECTION_TASKS.md)。

## 运行环境与数据

Windows x64；.NET 8 运行时；HALCON 24.11.1.1 Progress-Steady，托管接口 24111.0.0.0。沿用本机已安装的 HALCON/.NET 与原生库。

使用检查工具新建的 600×400 灰度图：圆心 (150,130)、半径 25 的亮圆，以及 (90,350)–(320,460) 亮矩形。没有写入用户原始图片、项目或模型。

| 区域 | 算法与规则 | 真实结果 |
| --- | --- | --- |
| P1，(100,80)–(200,180) | Threshold → Blob，count 闭区间 [1,1] | count=1，任务 OK |
| P2，(110,330)–(300,370) | HALCON Metrology 直线，status/Code=FOUND | FOUND，长度约190 px，角度−90°，任务 OK |
| P1 不变 | count 规则改为 [2,2] | 算子执行 Success，产品 NG |

人工拖绘 P1 后实际保存坐标约 (95.385,78.551)–(204.261,187.298)；重开、重新运行仍得到 count=1 和产品 OK。坐标按原图像素保存，未重采样图像。

## 检查结果

| 检查 | 结果 | 范围 |
| --- | --- | --- |
| InspectionDecisionTrial | PASS 47 | 闭区间、零值、缺字段/null/类型、非有限数、未知状态、异常优先级、必检/可选、取消、未配置、schema2、保存/复制/导入 |
| HalconInspectionTaskTrial | PASS 262 | 真实 Blob/直线、多任务共用 ROI、帧/空间/标注/边界拒绝、100 轮交替 OK/NG 与逐轮 native 对象释放、取消、WPF 应用按钮、阈值恢复、旧结果失效、项目切换释放、图标/延时/配色 |
| HalconLayoutCheck | PASS 128 | 原工作台布局边界；任务窗口另在 920/1100/1300 宽度检查按钮边界和提示属性 |
| HalconTemplateInputTrial | PASS 34 | 当前上游帧、模板示教、文件夹第二帧、基准恢复及异常处理 |
| HalconNodeAddTrial | PASS 24 | 新项目添加按钮/双击、取消、选择及保存恢复 |
| HalconQuickMatchTrial | PASS 235 | 原有匹配可靠性回归 |
| HalconPositionCorrectionTrial | PASS 537 | 原有位置链、有效区域和资源回归 |
| Release 构建与发布 | PASS | 0 警告、0 错误 |

专项检查的计数包含参数化断言和 100 轮重复资源检查，不代表 262 种物料或精度样本。原始通过记录保存于 [inspection-tasks-20261022](inspection-tasks-20261022/)。

复跑命令（输出目录必须不存在）：

```powershell
dotnet build tools/InspectionDecisionTrial/InspectionDecisionTrial.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
dotnet run --project tools/InspectionDecisionTrial/InspectionDecisionTrial.csproj -c Release --no-build -- artifacts/inspection-decision-new
dotnet build tools/HalconInspectionTaskTrial/HalconInspectionTaskTrial.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false
dotnet run --project tools/HalconInspectionTaskTrial/HalconInspectionTaskTrial.csproj -c Release --no-build -- artifacts/inspection-native-new
```

前期 WPF 驱动曾在清理阶段发生 CollectionView 跨线程错误，已将检查驱动修正为 Dispatcher 同步上下文与消息泵，后续完整重跑通过。一次新增默认规则断言发现直线优先选择 count，已改为默认 status/Code=FOUND 并回归通过。共享编译器连接曾超时，改用不共享编译器的构建方式，未降低编译检查或禁用 NuGet 审计。

## 实际界面操作与视觉复查

启动最终发布程序并直接传入独立 `.hvproj`，实际完成：项目恢复 → 单次检测 → 打开任务配置 → 选择图像来源 → 拖绘矩形 → Ctrl+S 保存 → 文件夹两张图循环检测约 30 秒 → 停止 → 检测模式显示两个算法和两个任务。第二次启动恢复绘制坐标；实际编辑数量上下限为 2 并应用，得到产品 NG；撤销规则修改、保存和重跑，恢复产品 OK。

发现并修正的界面问题：导航文字标记继承到操作按钮；来源下拉框使用浅色默认样式；自定义下拉框误显示内部记录；规则表默认白底导致浅色文字不可读；复选框文字对比不足。修正后截图复查，新增断言覆盖图标、名称、延时、背景及文字颜色。规则表支持横向滚动、参数区支持纵向滚动。

只对本轮受影响窗口和检测显示进行 1–5 分自评：整体协调4、层次4、排版4、组件4、空间4、交互4、完成度4。此评分不是全软件商业质量认证。主工作台仍沿用既有主题；本轮不整体重做视觉设计。

- [真实任务配置界面](../screenshots/inspection-tasks-config-20261022.jpg)
- [真实检测模式：两个任务 OK](../screenshots/inspection-tasks-result-20261022.jpg)
- [真实检测模式：P1 NG、P2 OK，产品 NG](../screenshots/inspection-tasks-ng-20261022.jpg)

截图均为本轮实际运行窗口，未混入其他应用；输入为上述受控检查图，不是真实物料精度验收。系统弹出菜单的自动化坐标映射不稳定，最后使用 Alt、方向键和 Enter 真实进入任务窗口，键盘路径通过。

检查结束正常退出本轮独立程序，检查 PID 7868、22684 已结束。100 轮 native 输出对象释放和切换项目释放已在工具内断言；停止保留最后一轮结果供查看，关闭时释放。未占用物理相机，未进行 GPU 模型长期显存检查。

最终发布目录 `artifacts/desktop-inspection-tasks-checked`。应用 DLL SHA256：`3F8684CE45F85A0D1D18BBFC8CA2080EB13B7D21FF86D6F58256ABF935B3FB2F`。已更新并读取确认桌面快捷方式、项目 ProgId 和 Applications/HalconVision.App.exe 两个注册命令均指向此已检查目录，软件名称保持不变。

直接以项目参数启动最终程序已恢复节点、连线、任务和 ROI。但实际调用 Windows Shell 打开 `.hvproj` 仍弹出默认应用选择器；系统已有 UserChoiceLatest 指向 Applications/HalconVision.App.exe，本轮不修改受保护的默认选择 Hash。自动化工具报告选择器最小化，激活后句柄失效，无法可靠完成最后选择，已请用户手动选择“始终使用本平台”。不能把注册命令正确称作系统默认打开方式已验收通过。三次尝试产生的选择器和等待命令已按已知 PID 清理。桌面快捷方式可直接启动，程序内“打开”可加载方案。

## 未覆盖与边界

本轮是轴对齐矩形 ROI、单流程内独立分支与串行调度；不含嵌套子流程、旋转/多边形 ROI、并行执行、PLC 输出、触发握手或生产追溯。未进行全套真实 DL 模型精度、物料真值误检/漏检评估、物理相机、8 小时压力、多显示器和各 DPI 组合验收。不能据此宣称已具备产线放行能力。
