# HALCON 本机运行探测

日期：2026-10-11（Asia/Shanghai）

**结果：成功。** 未更改 HALCON 安装、授权文件、授权配置或现有视觉检测平台代码。程序使用当前会话已有的环境变量与 DLL 搜索路径，没有设置或替换授权机制。

## 检测环境

- HALCON 安装版本：`24.11.1.1`，Progress-Steady，x64。
- HALCON/.NET 程序集：`MVTec.HalconDotNet.dll`，Assembly/File version `24111.0.0.0`；运行时加载自 `tools/HalconSmokeTest/bin/Debug/net8.0/MVTec.HalconDotNet.dll`。
- 原生 HALCON 主 DLL：`%HALCONROOT%\bin\x64-win64\halcon.dll`，File version `24.11.1.0`；进程确认从 HALCON 安装目录加载。
- 进程同时加载了 `mvloglib.dll`（`1.1.2`）、`hcanvas.dll`（`24.11.1.1`）、`halcondl.dll`（`24.11.1.1`）及 `libiomp5md.dll`（`20210428`）。
- Windows `10.0.26200` x64；.NET `8.0.30`；进程 64 位。
- 输入：HALCON 官方样例 `a01.png`，图像尺寸 `307 × 379`。

## 算子结果

| 步骤 | 结果 |
| --- | --- |
| `read_image`（通过 `HImage` 文件路径构造调用） | 成功，307 × 379 |
| `threshold(128, 255)` | 成功 |
| `connection` | 成功 |
| `count_obj` | 成功，2 个对象 |

退出码：`0`。测试期间没有出现 HALCON 授权错误。此结果证明本次进程可以执行这些算子；不代表确认了授权文件位置、授权类型或授权有效期。

完整原始输出（包括每个实际 DLL 路径、版本和运行环境）：[halcon-smoke-test-20261011.txt](halcon-smoke-test-20261011.txt)
