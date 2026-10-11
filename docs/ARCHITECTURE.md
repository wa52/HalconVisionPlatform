# Architecture · node workspace and offline inspection workbench

The solution is a modular monolith. HALCON, WPF, persistence, and domain rules are separated by project references; no service or process boundary is introduced in this prototype.

```text
HalconVision.App ──────> HalconVision.Application ──────> HalconVision.Domain
       │                           │                              ▲
       ├──────> HalconVision.Halcon ───── implements IInspectionEngine
       │                           │                              │
       └──────> HalconVision.Persistence <───────────────────────┘
```

- **Domain:** inspection/result data plus serializable project, flow, node, edge, port, and execution records. No WPF or HALCON types.
- **Application:** inspection runners plus node definition/executor/registry contracts, graph validation, stable topological ordering, and sequential workflow execution. Built-in nodes are limited to text constant, pass-through, and log test nodes.
- **HALCON:** owns `HImage`/`HRegion` lifetime, loads and displays images, applies ROI + threshold + connected components + area filtering, returns scalar/rectangle snapshots.
- **Persistence:** project workspace manifests and flow JSON under the project root, safe resource imports, project/flow archive operations, workflow run records, plus the earlier recipe and inspection stores.
- **App:** WPF node workbench (project tree, node library, multi-flow tabs, canvas, properties, preview and logs) alongside a separately opened Blob inspection window. Node canvas editing and Blob algorithms remain separate.

## Batch result behavior

Each directory batch receives a unique ID and stores start/completion timestamps, recipe name, per-file status, count, runtime, source path, per-image record path, and exception text when an image fails. An image failure does not stop later files. Processing remains sequential within one batch. Directory enumeration is top-level only, with supported raster extensions filtered before processing.

Per-image records live in `results`; batch summaries live in `batches`. The summary is written via a temporary file and replacement. A machine-specific absolute source path is retained for diagnostics; it is not a portable recipe reference.

No HALCON algorithm is registered as a workflow node yet. Camera, communication, authorization, production workflow and desktop interaction acceptance remain open.
