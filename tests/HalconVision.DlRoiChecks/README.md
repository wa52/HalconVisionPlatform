# 多 ROI / 分类 / 模型缓存回归

要求 Windows x64、HALCON 24.11、本机有效运行环境和 HalconTrainingPlatform 导出的分割及分类模型。模型、图像路径通过本机组合方案传入，不硬编码或提交到仓库。该程序使用真实模型，不是精度基准。

```powershell
dotnet restore tests/HalconVision.DlRoiChecks/HalconVision.DlRoiChecks.csproj --disable-parallel -m:1 -p:NuGetAudit=false
dotnet build tests/HalconVision.DlRoiChecks/HalconVision.DlRoiChecks.csproj -c Release --no-restore -m:1
dotnet tests/HalconVision.DlRoiChecks/bin/Release/net8.0-windows/HalconVision.DlRoiChecks.dll <组合方案.hvproj> artifacts/dl-roi-baseline 3
dotnet tests/HalconVision.DlRoiChecks/bin/Release/net8.0-windows/HalconVision.DlRoiChecks.dll <组合方案.hvproj> artifacts/dl-roi-cache-stress 100 --cache
dotnet tests/HalconVision.DlRoiChecks/bin/Release/net8.0-windows/HalconVision.DlRoiChecks.dll <组合方案.hvproj> artifacts/dl-roi-ui-contract --ui-contracts
dotnet tests/HalconVision.DlRoiChecks/bin/Release/net8.0-windows/HalconVision.DlRoiChecks.dll <组合方案.hvproj> artifacts/dashboard-contract --dashboard-contract
```

组合方案应包含一个本机图片读取节点、一个分割节点、一个分类节点及有效连接。检查程序在指定输出目录另建方案，不修改输入项目、训练模型或 HALCON 安装。输出目录每次应使用新名称，以便区分历史失败文件。

覆盖：重叠 ROI、反向绘制、部分/完全越界、禁用、保存恢复、身份与名称、重复参数拒绝、原图尺寸/有效域、分类类别映射和全部评分、分割对象所属区域、逐 ROI 物理裁图的独立推理对照、原生输出释放、处理中取消、设备切换、缓存容量/清理/关闭。错误返回非零退出码并写 error.txt；成功写 results.json 和推理预览，进度写 progress.json。

`--ui-contracts` 是 WPF 叠加层布局单元契约：恢复 ROI 时必须先将 Canvas 加入布局。它不替代鼠标拖动和屏幕观察。真实 UI 绘制、缩放、保存和检测显示需独立执行，见验收记录。

`--dashboard-contract` 在独立 WPF 窗口里复现面板已加载但仍隐藏时运行，再显示面板的路径；并用托管测试标注检查相同位置的两条文字是否覆盖。只运行图像读取，不加载训练模型。

时延含流程执行、图像读取和结果后处理。首轮含设备初始化和首次真实推理预热；统计后续轮次时必须单独排除首轮。进程私有内存、工作集并非 GPU 显存，HALCON/驱动缓存也不能仅凭对象 Dispose 状态判断已归还操作系统。此工具不宣称长期稳定、分类准确率或跨机器性能。
