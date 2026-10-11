# Development loop · 2026-10-11

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
