# P1–P6 独立任务真实检查

Windows x64、.NET 8、HALCON 24.11，直接引用当前 App 工程。使用本机已有模型和图像，输出九节点、六个必检任务的独立方案；拒绝覆盖已有输出目录，不写用户原始方案、图片或模型。

```powershell
dotnet build tools/HalconP1P6TaskTrial/HalconP1P6TaskTrial.csproj -c Release -m:1
dotnet run --project tools/HalconP1P6TaskTrial/HalconP1P6TaskTrial.csproj -c Release --no-build -- `
  '<已验证的四节点1111.hvproj>' '<正常原图.jpg>' '<负样本原图.jpg>' `
  '<全新输出目录>' verify docs/evidence/detection-negative-audit-20261022/audit-summary.json
```

参数前四项依次为源方案、正常原图、负样本原图、全新输出目录，第五项为模式：

| 模式 | 检查 |
|---|---|
| `cache-probe` | 两分支实推理，第二节点须命中同一个模型会话 |
| `cache-lifecycle` | 复制模型到输出目录，实测容量淘汰、版本变化、类型拒绝和清空重载 |
| `verify` | 标签哈希、六分支正负样本、阈值隔离、错误/取消、30 轮、保存重开/复制/导入、工作区停止和关闭 |
| `create-ui` | 复制两张图，实际重新示教来源基准，验证正负结果并保存两图回放方案 |
| `memory-probe` | 同一九节点真实链路 60 轮，采样私有内存、工作集、托管堆、HALCON 临时缓存与会话数量 |
| `preprocess-probe` | 不加载 GPU 模型，对原图六区域执行 24 次真实官方预处理，记录耗时及 real 像素 SHA256 |
| `preprocess-check` | 同上，并与第七参数提供的基线 `preprocessing.json` 比较像素和耗时，检查取消、错误恢复、线程切换及释放 |

`verify`、`create-ui` 及三个性能模式必须提供第六项标签审查 JSON，逐文件核对其哈希。`preprocess-check` 另要求第七项为同机、同数据集的优化前基线，其热预处理中位耗时必须降至基线一半以下；这项性能门槛不用于跨硬件 CI。输入必须符合该审查的 P1–P6 既有方案和样本，不能任意替换后沿用结论。`cache-lifecycle` 会在自有输出中复制两份约 234 MB 模型，仅修改副本时间戳测试失效。

`summary.json` 记录断言、逐任务结果、模型命中及只读文件哈希；失败保存 `failed-summary.json` / `error.txt` 并返回非零。`verify` 另保存 `memory.json`。每次执行检查原生输出释放；退出释放模型。没有模拟推理数据，也不是模型准确率工具。

`memory-probe` 的 60 轮不强制 GC；清空模型后仅在诊断阶段显式 GC，用于区分托管堆与进程保留内存。此调用只存在于检查工具，不能移入产品循环。HALCON 的线程临时缓存采样只代表采样线程，进程私有内存不是 GPU 显存，也不能据短时波动认定泄漏。`preprocessing.json` 保存逐次像素哈希和耗时，过程加载耗时与首个调用、热调用分别记录。

首次新进程 GPU 推理可能耗时约一分钟，冷启动和后续耗时分开记录。未执行真实窗口操作的工作区检查只代表 ViewModel 生命周期，UI 必须另用实际程序验证。
