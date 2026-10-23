# 程序图标

HalconVisionPlatform 使用青绿色圆角底、白色定位四角、镜头圆环和浅绿色焦点。造型对应视觉检测中的定位与成像，16 px 时仍保留主要轮廓；不使用小字、字体图标或外部品牌素材。

## 资源与接入

- `src/HalconVision.App/Assets/Brand.xaml`：可编辑的 WPF 矢量源，资源键 `ApplicationMark`，主界面 32 px 标志直接引用。
- `HalconVision.ico`：16/20/24/32/40/48/64/128/256 px，32 位透明通道，用于程序 EXE、主窗口和桌面快捷方式。
- `HalconVision.png`：256 px 透明预览，仅为图标素材，不是软件运行截图。
- 工程通过 `ApplicationIcon` 嵌入 EXE，同时把 ICO 作为 WPF Resource 和发布 Content，供窗口加载及安装脚本使用。资源不能只保存在开发者临时目录。
- `tools/InstallDesktop.ps1` 的快捷方式优先引用安装目录 `Assets/HalconVision.ico`，缺省回退到 EXE 图标；完整安装也设置工程文件类型的 DefaultIcon。未改软件、EXE、快捷方式名称。

## 重新生成

在仓库根目录运行以下命令。脚本会按 Brand.xaml **重新生成并覆盖**这两个派生素材，不修改矢量源、用户项目、图片或模型：

```powershell
powershell.exe -NoProfile -STA -File tools/BuildApplicationIcon.ps1
dotnet build src/HalconVision.App/HalconVision.App.csproj -c Release -m:1
```

脚本逐尺寸渲染并检查 ICO 解码顺序。PNG 压缩图标面向本项目的现代 Windows 环境；未承诺旧 Windows 兼容。

2026-10-24 已复查桌面程序、尺寸和布局；[检查与部署边界](evidence/2026-10-24-application-icon.md)、[已安装程序真实窗口](screenshots/application-icon-installed-20261024.jpg)。
