# Development loop · 2026-10-11

## 多 ROI 实测与缓存、UI 修复 · 2026-10-16

- 真实模型无缓存基线 51 项断言通过；缓存版 100 轮 1425 项断言通过，包含独立裁图对照、坐标/有效域、取消、设备变化、淘汰及关闭；原有生命周期 39 项通过。
- 以工作区为模型所有者，默认容量两个，独占租约保护执行与释放，停止/切换/关闭清理，首轮真实推理承担预热。观察到稳定轮均值从 2418 ms 到 1269 ms，详见 [实测与限制](evidence/dl-roi-validation-20261016.md)。
- 真实鼠标操作发现并修复 ROI 绘制后 Canvas 尺寸归零导致框消失；独立 WPF 回归复现并修复隐藏面板运行后切换检测模式不刷新，以及同位结果文字覆盖。三个回归先失败后通过。
- 折叠高级端口说明，使 ROI 配置直接可见。CPU、长期显存、准确率、CI 和生产闭环仍需推进。

## 多 ROI 分割与分类 · 2026-10-16

- 两个深度学习节点共用命名矩形 ROI 列表：新增、重绘、启用/禁用、删除、清空和项目保存；兼容旧单 ROI 参数。
- 每次执行创建一个模型会话，顺序裁剪处理全部启用 ROI，逐区域输出结果和耗时；所有临时对象即时释放，部分失败回收本节点输出。
- 分割目标带所属 ROI，独立掩膜保留原图坐标和有效域；分类逐 ROI 保留全部评分。重叠 ROI 分别统计，多 ROI 不提供虚构的总分类标量。
- 检测模式提供每区域名称/结果叠加及独立掩膜切换，完整数据仍供后续计算使用。
- 本轮范围是实现和编译集成，未运行模型、自动化测试或实际 UI 操作。既有全图证据不能作为本轮多 ROI 验收。
- `dotnet build src/HalconVision.App/HalconVision.App.csproj -c Release --no-restore -m:1 -p:NoWarn=NU1900`：0 警告、0 错误；发布到 `artifacts/desktop-multi-roi`。桌面入口和文件关联已更新，新版本携本地组合方案启动，进程响应正常；此启动检查不代表方案内容及图像交互验收通过。

## 当前进度索引 · 2026-10-13

下文各轮记录是当时的实现及限制，保留历史含义。最新功能以 [README](../README.md) 和 [ARCHITECTURE](ARCHITECTURE.md) 为准。

- HALCON 节点已包含图像读取、二值化、Blob、2D Metrology 直线查找和相机采集；已支持单次、连续、停止和单文件项目。
- 检测模式已支持同图多节点图形/判定/数值叠加；常用数据采用中文选项，高级坐标折叠显示。
- [同图显示及资源验收](evidence/overlay-display.md)：2026-10-12 记录 47 项真实图片检查和 39 项生命周期检查。
- 后续常用/高级字段调整已编译发布；本次文档同步未重新执行测试。PLC/IO、HDevEngine、真实设备及产线长时间验收仍需推进。

## Round 1: HALCON offline slice

**Goal:** establish an independent solution and prove the planned WPF/.NET/HALCON route compiles with the locally installed HALCON 24.11.1.1 Progress-Steady files.

**Implemented:**

- Added `HalconVision.Domain`, `HalconVision.Application`, `HalconVision.Halcon`, `HalconVision.Persistence`, and `HalconVision.App` projects.
- Added a WPF image window using MVTec's `HSmartWindowControlWPF`.
- Added image loading, a configurable rectangular ROI and grayscale threshold, connected-component count, expected-count OK/NG decision, and component/ROI rectangle overlays.
- Added JSON recipe load/save and result persistence containing a copy of the source image and result metadata.
- Errors, including result persistence failure, remain `ERROR`; no stale result is shown as OK.
- Added an environment banner that reports the HALCON runtime version/architecture and whether a local license file is present.
- Kept HALCON image handles and operator calls inside the HALCON adapter; the UI receives image dimensions and immutable inspection result data.

**Evidence:**

- Local `%HALCONROOT%\version.txt`: `24.11.1.1`.
- Local `%HALCONARCH%`: `x64-win64`.
- MVTec packages `24111.0.0` are present in the user's NuGet cache. Network access to NuGet and GitHub from the build shell is blocked, so restore used those local package directories.
- `dotnet build .\HalconVisionPlatform.sln --no-restore`: succeeded, 0 warnings, 0 errors.
- The independent C# smoke probe subsequently read `a01.png`, ran `threshold(128,255)`, `connection`, and `count_obj` successfully (2 regions, exit code 0); see `evidence/HALCON_SMOKE_TEST.md`.

**Gates still open:**

- No `license*.dat` is present in the local HALCON license directory. No MVTec/HALCON license service or license-specific environment variable was found; a remote license server or dongle may still be configured, so actual authorization has not been established.
- No image-processing run or WPF visual inspection was performed, so threshold behavior, native runtime loading, and overlays remain unverified at runtime.
- Mouse-drawn ROI, richer filters, malformed-image cases, result replay, and automated tests are not implemented.
- Camera, PLC, production scheduling, installation, and acceptance gates are outside this first offline slice.

**Round 1 follow-up:** interactive ROI and richer Blob filtering were implemented in Round 2. Authorization and production deployment remain outside this development slice.

## Round 2: Blob workbench and file replay · 2026-10-11

**Goal:** move the independent app from a basic smoke-test window to an interactive, configurable Blob inspection workbench.

**Implemented:**

- Replaced the starter window with a three-panel workbench: input/method rail, HALCON image viewport, parameter/result inspector, and event log.
- Added image-coordinate drag drawing for a rectangular ROI using `HSmartWindowControlWPF` mouse events. The inspector exposes the resulting X/Y/width/height and the ROI is drawn in cyan.
- Extended recipes with minimum and maximum component area. The HALCON adapter now applies `SelectShape("area", "and", ...)` after `Connection`, then counts and returns boxes for the surviving regions.
- Added multi-select file sequence playback with per-frame detection. Playback is explicitly labeled as file replay, and per-frame results are not persisted as inspection history.
- Preserved existing recipe load/save and single-image result persistence. Runtime banner reports the installed HALCON version/architecture without inspecting or changing licensing configuration.

**Evidence:**

- `dotnet build .\HalconVisionPlatform.sln --no-restore`: succeeded, 0 warnings, 0 errors.
- The cached HALCON WPF package documents image-space `Row`/`Column` on `HSmartWindowControlWPF.HMouseEventArgsWPF`; handlers use those coordinates directly.
- Windows UI screenshot inspection was unavailable in this session because the computer-use surface returned no native app windows. Startup layout and drag interaction therefore remain unverified visually.

**Gates still open:**

- Run the WPF app with representative images and visually confirm viewport sizing, drag ROI, overlays, and replay cadence.
- Verify a saved recipe round-trips the added area fields and check actual OK/NG samples.
- Camera SDK/streaming, HDevEngine, PLC/IO, production scheduling, and deployment are not part of this round.

## Round 3: Directory batch and traceable summaries · 2026-10-11

**Goal:** close the offline directory-processing gap in the V0.1 plan and keep individual file failures visible in the batch outcome.

**Implemented:**

- Added a directory picker and top-level image enumeration for BMP, PNG, JPEG, TIFF, and JP2 files, sorted deterministically by path.
- Added a sequential application runner. Each image independently becomes OK, NG, or ERROR; a failure does not abort the remaining directory.
- Successful inspections save the existing per-image record and source-image copy. Every batch saves a JSON summary with a unique ID, timestamps, recipe name, source paths, statuses, measurements, record paths, and full error details.
- Batch processing runs off the WPF dispatcher thread; progress updates return to the UI. HALCON processing remains single-threaded per batch.
- Updated README and architecture notes so the documented feature set matches the implementation.

**Evidence:**

- Domain, persistence, HALCON, and application projects each built with `--no-restore`, 0 warnings and 0 errors.
- WPF app built to the ignored `artifacts/verified` output directory with project references disabled, 0 warnings and 0 errors. The regular app output was locked by the already-running `HalconVision.App` process from the prior UI launch, so the app was compiled to a separate output path rather than stopping a process we could not control.
- No test suite was added or run. End-to-end batch behavior and actual ProgramData writes remain to be confirmed with a representative image directory on the target UI.

**Gates still open:**

- Visually verify batch progress/status, single-image outputs, and summary JSON on a real folder with OK, NG, malformed, and mixed-size images.
- Add resume/cancel policy only after agreeing how partial batches should be represented and whether incomplete batches may be resumed.
- The offline graph editor, measurements/calibration, camera/PLC, deployment, and production acceptance remain open.

## Round 4: Extensible node workspace foundation · 2026-10-11

**Goal:** implement the project/flow foundation and a usable node editor before adding visual algorithms. The architecture and acceptance specification is `docs/NODE_PLATFORM_FRAMEWORK.md` (GitHub spec commit `469a3fb`).

**Implemented:**

- Added serializable project, flow, node, edge, port, and execution models with stable IDs and JSON parameters.
- Added node definition/executor/registry contracts, three test nodes, graph validation (port/type/cardinality/required-input/cycle checks), deterministic DAG order, sequential execution, node status/log results, cancellation, and persisted run records.
- Added a project store for create/open/save, project-relative resource paths, image import, folder management, safe rename/move and referenced-resource deletion, flow import/export, and guarded ZIP project import/export.
- Replaced the startup page with a WPF workspace: project tree, node library, flow tabs, canvas, node properties, validation messages, image preview, and run logs. Canvas supports adding, moving, connecting, deleting, copy/paste, zoom, undo/redo, and save/reopen. The prior Blob workbench is retained as its own window.
- Kept algorithm execution out of the node system; only test nodes are registered. The Blob workbench continues to use its existing recipe and HALCON path.

**Evidence:**

- `dotnet build .\src\HalconVision.App\HalconVision.App.csproj --no-restore -p:OutputPath=D:\AiProjects\HalconVisionPlatform\artifacts\verified\`: succeeded, 0 warnings, 0 errors, including all referenced projects.
- No GUI automation surface was available in this turn. WPF startup, mouse interaction, actual flow round-trip, and archive round-trip have not been visually or end-to-end verified.
- No new automated test suite was added. Build evidence verifies compilation only.

**Gates still open:**

- Manually verify create/open/save, node drag and selection, click-to-connect, copy/paste/delete/undo, wheel zoom, test-node run, resource import/preview, and project archive import/export in the running WPF app.
- Add automated domain/persistence tests for malformed flows and archive/path safety before broadening the plugin surface.
- Add HALCON algorithm nodes only in later, separately scoped work; camera/PLC/HDevEngine and production acceptance remain outside this round.

## Round 5: Dark engineering workspace layout · 2026-10-11

**Goal:** apply the approved UI layout concept to the existing WPF workspace while keeping the canvas as the primary editing surface.

**Implemented:**

- Restyled the main window as a dark industrial engineering workspace with menu and command bars, a project/file and node-library rail, central multi-flow canvas, right-side node inspector, bottom image and run-monitoring docks, and a status bar.
- Added drag-resizable splitters, hide/restore commands for the left, right, and bottom panels, node-library filtering, run-result rows, and status-bar HALCON/runtime, flow, run, elapsed-time, and zoom fields.
- Added an independent maximized image-view window for the currently previewed project image. The processing-result and compare tabs clearly indicate that algorithm nodes are not connected yet.
- Preserved the separate Blob workbench and kept the workflow node catalog limited to the test nodes.

**Evidence:**

- `dotnet build .\src\HalconVision.App\HalconVision.App.csproj --no-restore -p:OutputPath=D:\AiProjects\HalconVisionPlatform\artifacts\verified\`: succeeded, 0 warnings, 0 errors.
- `git diff --check`: no whitespace errors. Git reported only the repository's normal LF-to-CRLF notices for edited text files.
- The compiled WPF application was launched and its window process reported responsive with the expected main-window title. No screenshot/UI automation surface was available, so panel resizing/hide-restore, multi-monitor image viewing, and appearance at target DPI remain unverified by direct interaction.
