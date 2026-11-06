# 归档工作台专项

```powershell
dotnet build tools/HalconArchiveWorkbenchTrial/HalconArchiveWorkbenchTrial.csproj -c Release -m:1
dotnet tools/HalconArchiveWorkbenchTrial/bin/Release/net8.0-windows/HalconArchiveWorkbenchTrial.dll artifacts/archive-workbench-new
```

输出目录必须不存在，Windows x64/.NET 8/HALCON 24.11。仅使用新生成的项目和32×24测试图，检查真实工作区自动归档、设置保存、写入失败与连续运行停止、分页/损坏/取消、WPF实际按钮/筛选/详情及三种尺寸边界。输出 summary.json 和独立检查副本，正常关闭窗口及资源。分页测试含复制的归档文件，不能当作独立生产采样数。
