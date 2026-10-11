# Compatibility and local validation

## Intended baseline

| Component | Baseline | Evidence |
| --- | --- | --- |
| HALCON | 24.11.1.1 Progress-Steady, x64 | Local `%HALCONROOT%\version.txt` and `%HALCONARCH%` |
| .NET | .NET 8 build target | Local .NET SDK inventory; solution build |
| UI | WPF + `HSmartWindowControlWPF` | MVTec Programmer's Guide, HALCON/.NET WPF integration section |
| License | Not yet validated | Local `license` directory contains only the vendor placeholder `license.txt`; no `license*.dat` was found. A license server or dongle is not ruled out. |

## Open gates

- The solution builds against the cached `MVTec.HalconDotNet-Windows` package `24111.0.0` and `MVTec.HalconDotNet` package `24111.0.0`.
- Run on the target Windows x64 machine and verify native runtime loading.
- Run one real operator with a valid license and capture the result.
- Verify image format behavior and WPF display against local HALCON examples.
- Confirm the license mechanism; absence of a local license file does not rule out a configured license server.

HALCON and camera runtime binaries and license files must not be committed.
