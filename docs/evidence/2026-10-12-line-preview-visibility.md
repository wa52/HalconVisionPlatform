# 缩小预览下的拟合线辨识

用户截图已显示黄色矩形，但蓝色卡尺占据视线，红线仅按源图 5 px 线宽绘制，缩小后很细。

修改：红线按源图尺寸增加线宽；默认仅显示搜索框及拟合线。新增 `showDiagnostics`（默认 false），旧项目已有的 `showMeasureRegions/showEdgePoints=true` 不会自动启用诊断图形；主动启用诊断后仍按原开关显示。

回归：合成图修改前 `legacy caliper overlays stay hidden until diagnostic display is explicitly enabled` 失败，修改后验证隐藏/主动显示两种情况；实际 1900 检查完整线段、红线中段宽度、黄色框、FOUND/NOT_FOUND 与资源释放。

截图/日志位于本地 `artifacts/line-preview-1900`，只修改方案的测试内存副本，不保存用户文件。
