# 直线查找结果显示与旧入口修复

## 原因

- 本机正在运行 `artifacts/delete-shortcut/HalconVision.App.exe`，该历史目录联接指向旧 `artifacts/desktop`。
- 算子正常完成但未找到直线时，流程状态为 Success；界面未展示节点输出的 NOT_FOUND，导致误判。
- 当前用户保存的 1900 方案只有旧 referenceLine 参数，且阈值 30、最小评分 0.7。保留原文件，测试只修改内存副本。

## 修改

- 删除参考线绘制 UI，只保留“绘制矩形范围”。兼容旧参数读取，但不再把参考线画成结果。
- 图像面板和结果表显示“找到直线”或“未找到直线”；前者显示评分、长度、角度，后者提供调整范围、方向、阈值提示。
- 图像摘要以绿色表示找到，橙色表示未找到或运行错误。
- 安装脚本支持独立发布目录，并更新受检查的历史目录联接、桌面快捷方式和文件关联。

## 验证

构建 Release：0 警告、0 错误。

真实 WPF 事件与 HALCON 检查：

```powershell
dotnet tests/HalconVision.LifecycleChecks/bin/Release/net8.0-windows/HalconVision.LifecycleChecks.dll artifacts/detection-status-final C:/Users/feng/Desktop/1900.hvproj
```

退出码 0。验证仅有矩形绘制按钮、FOUND 与 NOT_FOUND 在两个面板显示、单一二端点直线、图像输出、重复运行释放旧图像/轮廓以及关闭窗口释放资源。

真实图片矩形 `417,1978,1575,2415`，边缘阈值 5、最小评分 0.2：FOUND，评分 0.833，25 个边缘点，长度 1020.0 px，角度 -89.54°。这是测试调参结果，不表示用户旧参数一定能检测成功。

截图与日志：`artifacts/detection-status-final/found-ui.png`、`not-found-ui.png`、`result.txt`（本地 artifacts 不提交）。

发布：`artifacts/desktop-detection-status`。旧进程已载入的代码仍需用户关闭并重新打开；安装不强制关闭未保存的工作窗口。
