# 独立启动检查与原生异常采集

Windows x64 / .NET 8。只启动本项目的检查 exe，不附加用户正在编辑的窗口，不修改 HALCON 安装、许可证、DLL 搜索路径或异常处理机制。工具本身不引用 HALCON。

## 构建与复跑

在仓库根目录执行，输出路径必须不存在：

```powershell
dotnet build tools/HalconStartupAudit/HalconStartupAudit.csproj -c Release -m:1
tools/HalconStartupAudit/bin/Release/net8.0-windows/HalconStartupAudit.exe --self-check artifacts/startup-control-new

tools/HalconStartupAudit/bin/Release/net8.0-windows/HalconStartupAudit.exe fixture tests/HalconVision.LifecycleChecks/bin/Release/net8.0-windows/HalconVision.LifecycleChecks.exe artifacts/startup-fixture-new 100

tools/HalconStartupAudit/bin/Release/net8.0-windows/HalconStartupAudit.exe fixture tests/HalconVision.LifecycleChecks/bin/Release/net8.0-windows/HalconVision.LifecycleChecks.exe artifacts/startup-capture-new 10 --capture

tools/HalconStartupAudit/bin/Release/net8.0-windows/HalconStartupAudit.exe lifecycle tests/HalconVision.LifecycleChecks/bin/Release/net8.0-windows/HalconVision.LifecycleChecks.exe artifacts/startup-lifecycle-new 1

tools/HalconStartupAudit/bin/Release/net8.0-windows/HalconStartupAudit.exe layout tools/HalconLayoutCheck/bin/Release/net8.0-windows/HalconLayoutCheck.exe artifacts/startup-layout-new 1
```

运行 `fixture` / `lifecycle` 前先构建 LifecycleChecks，运行 `layout` 前先构建 HalconLayoutCheck。`--self-check` 必须通过生成的 exe 调用，不能用 `dotnet ...dll`，因为它需要明确启动自己的控制子进程。

## 输出及判读

- 次数 1–200，逐次串行执行；遇到非零退出、超时或检查输出缺失立即返回失败。后续启动不是失败重试。
- `environment.json` 保存检查 exe、关键依赖 SHA256、版本与只读 HALCON 环境信息；`started.json` 在子进程执行初期保存 PID。
- `summary.json`、逐轮 `result.json` 保存十六进制退出码、耗时、超时和退出确认。Fixture 还要求四个原生阶段完整、两张测试 PNG 已写出；生命周期要求 result.txt 存在。
- 普通模式保存原始 `stdout.bin` / `stderr.bin`；先严格 UTF-8 解码，不符时用 Windows OEM 代码页解码。原始字节保留，便于复核中文错误信息。
- 采集模式用 Windows DEBUG_ONLY_THIS_PROCESS 创建自己的子进程，记录加载模块及版本、AV 的读/写/执行类型、故障地址和模块偏移。只处理初始系统断点；其他异常继续交给原进程/系统，不吞异常、不重启。
- CLR 可能在首次原生 AV 后直接终止，未必提供第二次机会。首次 AV 也采集，但**首次 AV 不等于崩溃**：正常空引用也可能触发。须联合退出码、阶段、检查结果判读。
- 每次异常使用独立文件；`DumpPath` 是最近成功转储，`DumpFiles` 保留历史。前 32 次首次异常及后续第二次机会异常写转储，达到上限会明确记检查错误，不声称采集完整。
- 非零退出并不一定有 AV（例如托管错误）；必须查看 StandardError、阶段和 DumpError。模块地址未知也不能直接归因为 HALCON，JIT 地址也可能没有普通模块映射。
- 采集模式不重定向控制台，StandardOutput / StandardError 为空，须结合检查目录的 error.txt / result.txt 与原生事件；完整控制台原始字节由普通模式保留。异常事件最多记录 200 项，达到上限会明确记检查错误。
- 普通检查每轮上限 20 秒，完整生命周期 90 秒。超时只终止本工具创建的子进程；采集模式使用保留的进程句柄，不枚举并结束同名软件。每轮等待子进程退出并记录 CleanupConfirmed。

`--self-check` 含 12 个控制场景：成功、已处理托管异常、已处理空引用、退出42、超时，以及采集模式下的受控 AV 和“已处理空引用后再 AV”。它验证失败能够被记录、不会被误报为通过，并直接核对转储的异常流、异常地址及线程上下文。**受控 RaiseException 不是真实 HALCON 间歇故障的复现。**

## 转储与边界

输出应放在本仓库忽略的 `artifacts/`。转储可能含线程栈和运行期信息，默认不上传；本仓库也忽略 `*.dmp`。共享诊断优先使用审查后的 JSON。采集模式会改变调度与启动时序，普通启动与采集启动必须分别记录。

不配置全局 WER / 注册表、不安装调试器、不修改系统或授权文件，不读取模型和生产图像。本工具不是常驻监控，也不自动抓取日常桌面程序的故障；需要在独立检查入口触发异常。没有真实故障转储时，不能宣称根因已确认或故障已修复。

参考 Microsoft 的 [调试循环](https://learn.microsoft.com/en-us/windows/win32/debug/writing-the-debugger-s-main-loop)、[异常继续语义](https://learn.microsoft.com/en-us/windows/win32/api/debugapi/nf-debugapi-continuedebugevent)、[MiniDumpWriteDump](https://learn.microsoft.com/en-us/windows/win32/api/minidumpapiset/nf-minidumpapiset-minidumpwritedump) 和 [SDK 结构声明](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/minidumpapiset.h)。
