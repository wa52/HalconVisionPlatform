# 圆查找真实检查

Windows x64、.NET 8、HALCON 24.11，使用本机正常可用的 HALCON 运行环境。只在全新输出目录写检查副本，不修改安装、许可证、原始图片或项目。

```powershell
dotnet build tools/HalconCircleTrial/HalconCircleTrial.csproj -c Release -m:1
& tools/HalconCircleTrial/bin/Release/net8.0-windows/HalconCircleTrial.exe artifacts/circle-new-check
```

已有目录会拒绝。退出 0 表示通过，异常保存在 error.txt；summary.json 保留已完成断言及数值。工具使用 STA/WPF DispatcherSynchronizationContext，调用真实 HALCON 算法和真实窗口事件，关闭自己创建的窗口并释放图像、模型和工作区。

当前最终检查 314 项：灰度/RGB/uint2/real、极性、阈值、无圆、偏移、框外干扰、部分圆拒绝、半径/残差拒绝、严格 ROI/domain、多图对象拒绝、预览像素与输入不变、取消清理、100 次圆/空图交替、统一 OK/NG/ERROR、预览误接保护、项目/流程保存恢复、基本/高级参数、1180/1366/1580 宽度布局、任务绑定的真实绘制入口及标签避让。

合成图使用 HALCON gen_circle；整数半径会使所请求的整数圆心按其栅格对称规则落到半像素中心。检查通过 AreaCenter 确认实际中心，再检查各轴及半径误差 <0.25 px，不把生成规则当作拟合偏差。

另读取本机官方 circle_plate.png 的副本验证链路及可视结果；该图片没有计量真值，不能据此宣称真实工业测量精度。缺少该官方图片会报错，不以合成图冒充官方样本成功。

开发中曾出现新工具缺少 DispatcherSynchronizationContext 的托管异常，已修正工具。任务 ROI 绘制入口在生产修复前有确定性失败，修复后通过。两者均不作为先前间歇原生 0xc0000005 的复现或解决证据。完整记录见 [本轮证据](../../docs/evidence/2026-10-23-circle-find.md)。
