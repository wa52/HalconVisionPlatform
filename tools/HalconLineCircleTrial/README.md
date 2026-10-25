# 线圆测量专项检查

在具备 HALCON 24.11、.NET 8 和 Windows x64 环境的本机执行：

```powershell
dotnet run --project tools/HalconLineCircleTrial/HalconLineCircleTrial.csproj -c Release -- artifacts/line-circle-new-check
```

输出目录必须不存在。生成合成条带/圆图、空图、OK/故意公差 NG 方案、`actual-result.json` 和 `summary.json`，异常写入 `error.txt`。不修改用户原始图像、方案、模型或 HALCON 安装。

覆盖无限直线垂距及延长线语义、切/交/离、反向端点、固定随机方向、完整测量线段、显示开关、跨帧/空间/无效域拒绝、取消、100 次有效/失测交替释放、共享 RGB 不污染、真实读图/找线/找圆/任务判定、15 次完整运行释放、项目保存恢复和 WPF 任务配置。检测结果表检查三项数值可见；原始精度参与公差判断。
