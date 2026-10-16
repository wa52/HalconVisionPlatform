# 运行环境与验证边界

更新：2026-10-13。

| 组件 | 当前基线 | 验证范围 |
| --- | --- | --- |
| HALCON | 24.11.1.1 Progress-Steady，x64-win64 | 本机版本记录及实际算子运行 |
| .NET | .NET 8，x64 Windows | WPF 工程构建、独立 HALCON 调用及运行回归 |
| 托管包 | MVTec.HalconDotNet / MVTec.HalconDotNet-Windows，24111.0.0 | 以项目包引用为准 |
| 图像显示 | HSmartWindowControlWPF + WPF Canvas | 真实图片、同图叠加、缩放、平移和配置恢复检查 |
| 相机 | 本机 HALCON 可用采集接口，包括 DirectShow | 已实现发现、扫描、连接与取帧；自动生命周期回归使用官方 File 接口 |

## 已有证据

- [独立 C# 调用](evidence/HALCON_SMOKE_TEST.md)：读取官方图片、threshold、connection、count_obj 成功。
- [同图叠加回归](evidence/overlay-display.md)：真实 1900 图片链路及资源生命周期检查；该记录明确列出测试时的参数调整。
- [UI 与生命周期](evidence/ui-lifecycle-20261012.md)：项目加载、运行控制、采集句柄和关闭清理。
- [2026-10-16 多 ROI 与缓存](evidence/dl-roi-validation-20261016.md)：真实分类/分割差分、100 轮回归、缓存清理及 UI 修复；仍不代表 CPU、准确率和长时间显存验收。

这些证据反映指定机器和测试条件。HALCON 调用未出现授权错误；未修改 HALCON 安装、许可证或授权机制，不能仅由算子成功推断其他机器及部署环境的许可状态。

## 待验收范围

- 每种真实物理相机及采集接口的驱动、设备识别、触发、超时和断线恢复。
- 新机器的 HALCON 原生库加载、位数、运行环境及部署配置。
- 跨裁剪、旋转、重采样节点的图形坐标变换。
- PLC/IO、HDevEngine、产线节拍、长时间稳定性及现场验收。

HALCON/相机运行库、许可证及本机检测图片不提交到仓库。
