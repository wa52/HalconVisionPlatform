# HalconVisionPlatform

HALCON 24.11 x64 / .NET 8 WPF 节点式视觉工作台。当前目标是先完成流程编辑、项目保存和 HALCON 图像数据通路；相机、PLC、HDevEngine 和产线验收尚未接入。

## 当前能力

- 新建 `.hvproj` 单文件项目。空白项目立即包含一个空的“主流程”；流程和项目元数据保存在同一个项目文件中，不额外创建项目目录、流程目录或运行记录目录。
- 打开 `.hvproj` 项目文件，保存当前流程到该文件。旧版 `project.json` 项目仍可打开。
- 可扩展节点注册、节点画布、端口连线、属性编辑、撤销/重做、流程校验和顺序执行。
- HALCON 节点库包含读取图像、二值化和 Blob 分析。图像读取支持单张图像或一个文件夹中的图像序列；二值化使用 HALCON `Threshold`，RGB 输入先转换为灰度；Blob 分析使用 `Connection`、`SelectShape` 和 `CountObj`。
- 主图像窗和独立图像窗都支持适应窗口、按钮缩放、滚轮缩放，以及按住鼠标左键拖动平移。
- HALCON `HImage` / `HRegion` 仅在流程运行期间传递，不写入项目文件。

## 构建与运行

要求 Windows x64、.NET 8 SDK、HALCON 24.11 x64 Runtime，以及由本机环境提供的 HALCON 运行配置。HALCON 包版本为 `24111.0.0`。

```powershell
dotnet build .\HalconVisionPlatform.sln
dotnet run --project .\src\HalconVision.App\HalconVision.App.csproj
```

选择“新建”后指定 `.hvproj` 文件路径即可得到空白项目。使用“打开”选择该文件，使用“保存”将所有流程写回项目文件。

### 桌面入口与项目文件关联

发布后统一安装到固定的 `artifacts/desktop` 目录，更新桌面快捷方式及 `.hvproj` 打开命令：

```powershell
dotnet publish src/HalconVision.App/HalconVision.App.csproj -c Release -m:1 -o artifacts/publish
powershell -NoProfile -File tools/InstallDesktop.ps1 -PublishedDirectory artifacts/publish
```

双击项目文件会直接加载其中的默认流程、节点、连线和参数。安装脚本保留旧 `delete-shortcut` 发布目录的备份，并将旧入口转到固定安装目录，兼容资源管理器缓存的旧打开方式。后续发布继续使用同一安装目录。脚本不改写 Windows 的受保护默认应用选择；仍被其他应用占用时，在文件属性的“打开方式”中选择本平台。

## 当前边界

图像读取目录按文件名顺序读取当前目录下支持的图像，不递归扫描子目录。项目中的图像路径保持为用户选择的本机路径。相机、PLC、HDevEngine、生产配方管理和检测追溯仍需后续开发。

HALCON 真实调用的本机证据见 [docs/evidence/HALCON_SMOKE_TEST.md](docs/evidence/HALCON_SMOKE_TEST.md)；阶段与限制见 [docs/LOOP.md](docs/LOOP.md)。

项目路线：[docs/PLAN.md on GitHub](https://github.com/wa52/HalconVisionPlatform/blob/main/docs/PLAN.md)。
