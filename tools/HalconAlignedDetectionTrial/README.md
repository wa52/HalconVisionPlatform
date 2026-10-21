# 完整定位与目标检测链路检查

读取已经配置好的四节点 `.hvproj`，在真实 HALCON/GPU 上执行。输入方案、图片和模型只读，所有输出写入**全新**目录；已有目录会被拒绝。

该工具专门验证1111 / P1～P6方案，不是通用模型评估器。模型和位置参考必须已经配置，输入文件夹取按名称排序的前三张 `OR_*.jpg`，随后检查空图阻断与正常恢复。

为避免把并行开发中的应用改动混入发布验收，编译时引用已经检查过的发布 DLL，而不编译应用源码。必须提供完整发布目录，含 Domain、Application、Halcon、Persistence 及 MVTec.HalconDotNet DLL。默认使用本机 `artifacts/desktop-position-correction-checked`，其他机器需显式指定：

```powershell
dotnet run --project tools/HalconAlignedDetectionTrial/HalconAlignedDetectionTrial.csproj -c Release -p:NuGetAudit=false -p:ValidatedRuntimeDir=D:/checked/App -- D:/checks/1111.hvproj D:/checks/new-output
```

覆盖：图校验、实际匹配/矫正/检测、矫正空间标记、六ROI类别与坐标、无位姿阻断且无旧框、恢复、启动前取消、逐帧句柄释放、有界缓存与清空、另存恢复、输入方案及模型哈希不变。结果写入 `summary.json` 和逐帧JSON；失败写 `error.txt` 并返回非零。

正常检出与未检出均是被测结果，不要求把所有 ROI 判为 OK。没有独立真值，不输出准确率、mAP或产品合格结论。外部进程修改输入文件会触发哈希检查失败，检查期间应保持输入静止。
