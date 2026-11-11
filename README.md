# Pipe Tagger (Revit 2021 – 2027)

Tags pipes and ducts by System Type, with a chosen tag per system, on the active view,
every view on the active sheet, or sheets selected in the Project Browser.

**Views from Scope Boxes** (panel *Views & Sheets*): select the main plan (or its viewport), tick scope
boxes -> one Independent / Independent-with-detailing / Dependent view per scope box, named from a pattern
(`{View}`, `{ScopeBox}`, `{N}`), with template + scale. Optionally each view goes on a sheet: give first and
last sheet number/name (the one number that changes counts up), a title block, and a sheet to copy
parameters, revisions and viewport position from. Existing sheets with the same number are reused.
If the main view's crop region has an edited (non-rectangular) shape, Revit locks the Scope Box parameter on
its dependents; the tool resets that shape to a rectangle on the new view and then applies the scope box.

## Layout
| Folder | What |
|---|---|
| `Loader/` | Small DLL Revit loads at startup: ribbon tab **Mechano** with **Tag Pipes & Ducts** and **Reload**. Locked while Revit runs. |
| `Core/` | All tagging logic + dialog. Hot-reloaded: rebuild while Revit is open, then click **Reload**. |
| `dist/<version>/` | Ready-to-install package per Revit version (copy into `%APPDATA%\Autodesk\Revit\Addins\<version>\`). |

## Commands
```powershell
.\build.ps1             # build every version 2021-2027 into dist\
.\build.ps1 -Install    # build + install into every Revit on this PC
.\install.ps1           # install dist\ into every Revit on this PC
dotnet build Core -p:RevitVersion=2026   # quick edit loop -> then click Reload in Revit
```

## Versions
| Revit | .NET | Notes |
|---|---|---|
| 2021 | Framework 4.8 | uses `TaggedLocalElementId` (single element per tag) |
| 2022 – 2024 | Framework 4.8 | |
| 2025 – 2026 | .NET 8 | Core reloads in an unloadable AssemblyLoadContext |
| 2027 | .NET 10 | Needs the .NET 10 SDK; without it, build.ps1 compiles against the 2026 API on .NET 8 |

Revit API references come from NuGet (`Nice3point.Revit.Api.*`), so no Revit install is needed to build.

## Install (no build)
Download `PipeTagger-dist` from the latest successful [Build](../../actions/workflows/build.yml) run, then copy
the folder for your Revit version (e.g. `2026\`) into `%APPDATA%\Autodesk\Revit\Addins\2026\` and restart Revit.

## License and code signing
MIT — see [LICENSE](LICENSE). Release builds are signed as described in [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).
