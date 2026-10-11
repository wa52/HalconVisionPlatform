# Architecture · first offline slice

The solution is a modular monolith. HALCON, WPF, persistence, and domain rules are separated by project references; no service or process boundary is introduced in this prototype.

```text
HalconVision.App ──────> HalconVision.Application ──────> HalconVision.Domain
       │                           │                              ▲
       ├──────> HalconVision.Halcon ───── implements IInspectionEngine
       │                           │                              │
       └──────> HalconVision.Persistence <───────────────────────┘
```

- **Domain:** `InspectionRecipe`, `InspectionResult`, batch item/status records, image dimensions, and validation. No WPF or HALCON types.
- **Application:** `IInspectionEngine`, `InspectionRunner`, and `BatchInspectionRunner`; batch work is sequential, catches failures per image, and emits progress.
- **HALCON:** owns `HImage`/`HRegion` lifetime, loads and displays images, applies ROI + threshold + connected components + area filtering, returns scalar/rectangle snapshots.
- **Persistence:** JSON recipe files, per-image records with an input-image copy, and a batch summary under the per-machine ProgramData directory.
- **App:** WPF controls, recipe fields, dialogs, status and logs. Directory batch execution runs off the UI thread; the HALCON adapter remains the only operator implementation.

## Batch result behavior

Each directory batch receives a unique ID and stores start/completion timestamps, recipe name, per-file status, count, runtime, source path, per-image record path, and exception text when an image fails. An image failure does not stop later files. Processing remains sequential within one batch. Directory enumeration is top-level only, with supported raster extensions filtered before processing.

Per-image records live in `results`; batch summaries live in `batches`. The summary is written via a temporary file and replacement. A machine-specific absolute source path is retained for diagnostics; it is not a portable recipe reference.

The current dependency graph is intentionally small and will grow only when camera, communication, authorization, and production workflow requirements are validated.
