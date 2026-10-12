# 搜索框可见性与拟合线范围修复

- 原实现仅裁剪 HALCON 返回的有限线段，起终点由有效测量点跨度决定，所以可能比搜索框短。
- 改为将拟合直线与矩形边界相交，输出及预览使用同一条延伸后的线段。评分与边缘点仍使用 HALCON 实际检测结果。斜线的几何长度可能略大于框的高度或宽度。
- 黄色边框按图像尺寸加粗，绘制顺序位于卡尺/边缘点之上，最终红线之下，缩小预览时仍可辨认。

## 回归

`--rectangle-line-check` 修改前退出 1：`fitted vertical line extends to both ends of the search rectangle`。修改后退出 0，水平、垂直线都到达两侧边界；黄色边框和红色线宽检查通过。

实际 WPF + 1900 方案检查退出 0：保存的矩形高度 836.456 px，输出线起点行 549.9065、终点行 1386.3626，长度 836.506 px。内存测试使用阈值 5、最小评分 0.2；原项目不修改。黄色边框像素、FOUND/NOT_FOUND、重复运行与关闭释放资源均通过。

本地截图/日志：`artifacts/rectangle-span-1900/found-ui.png`、`result.txt`。

Release 构建 0 警告、0 错误。发布位置 `artifacts/desktop-rectangle-span`。
