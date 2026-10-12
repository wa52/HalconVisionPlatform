# 同图多节点叠加显示验收

日期：2026-10-12。参考：wa52/VisionInspection，提交 843cf6da8b059cb6afa4814a8598b1f159d7acb4。

## 环境及实现

- Windows / .NET 8 / HALCON 24.11.1.1，x64。
- HSmartWindowControl 显示底图，WPF Canvas 显示托管坐标图形和固定字号文本。
- 一个图像内容可组合多个节点。每个节点独立选择图形、判定、评分、坐标等输出字段。
- 默认原图底图；旧四内容布局载入时合并为同图配置，保存前不改原文件。
- 保存/重开及复制流程保留图层设置；复制节点 ID 同步重映射。
- 未找到、未运行、节点删除、尺寸/来源不匹配不会沿用旧拟合线。
- 修复初次进入检测模式时 HSmartWindow 尚未初始化导致的空引用异常。

## 1900 真实图像回归

命令：

```powershell
dotnet build tests/HalconVision.LifecycleChecks/HalconVision.LifecycleChecks.csproj -c Release --no-restore -m:1 -p:NoWarn=NU1900
dotnet tests/HalconVision.LifecycleChecks/bin/Release/net8.0-windows/HalconVision.LifecycleChecks.dll artifacts/overlay-scene-check C:/Users/feng/Desktop/1900.hvproj
```

47 项检查通过，退出码 0。原图 2592×1944；真实 metrology 得到 1 条拟合线，评分 1，长度 836.506 像素，覆盖矩形高度。测试在内存中使用阈值 5、最低评分 0.2，未保存用户原始项目；不能据此声称原方案参数也会得到相同评分。

证据：`artifacts/overlay-scene-check/result.txt`、`combined-detection-ui.png`、`layer-settings-ui.png`。截图含读取节点和直线查找节点共用的一张原图、黄色矩形、完整红线、两节点判定及 score/line.RowBegin 数值。

检查缩放、平移、刷新保留视野、取消图层、独立图形/判定/字段开关、尺寸不匹配、删除节点、配置序列化、实际项目保存重开、复制流程、NOT_FOUND 清理旧线、重跑和关闭时释放 HALCON 对象。

## 生命周期回归

```powershell
dotnet tests/HalconVision.LifecycleChecks/bin/Release/net8.0-windows/HalconVision.LifecycleChecks.dll artifacts/overlay-scene-lifecycle
```

39 项检查通过，退出码 0。覆盖 200 次执行释放 400 个旧图像/区域、可见检测模式连续刷新、10 次启停、项目切换、关闭期间等待任务和相机扫描、相机句柄释放、三次窗口重开。本次连续模式 UI 心跳 63 次，停止耗时 15 ms。

相机生命周期用 HALCON 官方 File 采集接口验证；本轮不声称完成真实物理相机验证。源节点及尺寸校验只覆盖当前不改变坐标的节点，未来裁剪/旋转节点需要显式坐标变换。

发布目录：`artifacts/desktop-overlay-scene`。桌面快捷方式及 .hvproj 关联指向该目录。
