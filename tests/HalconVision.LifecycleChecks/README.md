# WPF 功能与生命周期回归

在安装 HALCON 24.11 的 Windows x64 环境执行。检查程序创建独立的测试项目和黑白图像，不读取用户项目。

```powershell
dotnet restore tests/HalconVision.LifecycleChecks/HalconVision.LifecycleChecks.csproj -m:1
dotnet build tests/HalconVision.LifecycleChecks/HalconVision.LifecycleChecks.csproj --no-restore -m:1
tests/HalconVision.LifecycleChecks/bin/Debug/net8.0-windows/HalconVision.LifecycleChecks.exe artifacts/lifecycle-checks
```

结果写入指定输出目录的 `result.txt`。失败返回非零退出码；如果连续运行饿死 WPF Dispatcher，后台看门狗会记录失败并结束本检查进程。

检查真实的 MainWindow 事件处理器、ViewModel、流程引擎和 HALCON 算子，覆盖节点复制/删除/撤销、连线恢复、参数、缩放、保存与打开、文件夹单步、二值化面积、连续运行响应、重入防护、200 次图像对象替换、反复启停、项目切换、运行中关闭、子图像窗口关闭、并发扫描时关闭及重复释放。

另检查项目启动参数（文件路径、`--project`、`--project=`、文件 URI），首次加载后的默认流程、节点与连线，以及 WPF 实际生成的节点容器，防止仅检查模型而遗漏空白画布。Windows 文件关联仍需从资源管理器实际双击验收。

相机生命周期自动检查使用 HALCON 官方 File 虚拟采集接口，将真正的 HFramegrabber 句柄注入测试工作区；不代表物理相机驱动已通过验收。物理 DirectShow 相机的实际界面验收单独记录在 `docs/evidence/ui-lifecycle-20261012.md`。

检查程序自己拥有 Application 启动入口，创建 MainWindow 前加载产品共享图标主题，避免遗漏资源或启动第二个 App 窗口。NuGet 审计保持默认启用；出现网络缓存警告时联网重新 restore 后再构建。

停止连续运行会结束采集并释放采集句柄，最后一次完整结果留在内存供节点预览；再次运行释放旧结果，切换项目或关闭工作区释放全部运行期 HALCON 图像/区域。相机停止后需要重新连接再启动。
