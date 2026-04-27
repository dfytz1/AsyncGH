# AsyncGH

**Keep Grasshopper's UI fully responsive while solutions run — on Rhino 8 Mac (ARM64 / .NET 8).**

---

## Features

| Feature | Details |
|---|---|
| **Async computation** | Solutions run on a background thread — canvas stays interactive while GH computes |
| **Canvas navigation** | Pan and zoom freely during computation |
| **Component moving** | Rearrange components while the solution runs |
| **Save during computation** | Save the definition at any time |
| **Clusters** | Cluster sub-documents solve correctly inline |
| **MessageBox support** | `MessageBox.Show()` is auto-marshalled to the UI thread — no crash |
| **Rhino input support** | `GetObject`, `GetPoint`, `GetString`, etc. are auto-marshalled to the UI thread |
| **Progress bar** | A teal bar along the top of the canvas fills with magenta as components complete |
| **Toolbar toggle** | One-click enable/disable via the GH toolbar button |

---

## Requirements

- Rhino 8 Mac (tested on 8.x, ARM64)
- .NET 8 runtime (bundled with Rhino 8)

---

## Installation

1. Copy all files from the release into your Grasshopper Libraries folder:
   - `AsyncGH.gha`
   - `MonoMod.RuntimeDetour.dll`
   - `MonoMod.Core.dll`
   - `MonoMod.Utils.dll`
   - `MonoMod.Backports.dll`
   - `MonoMod.ILHelpers.dll`
   - `MonoMod.Iced.dll`
   - `Mono.Cecil.dll`
   - `Mono.Cecil.Pdb.dll`
   - `Mono.Cecil.Mdb.dll`
   - `Mono.Cecil.Rocks.dll`

2. Restart Rhino.

The Grasshopper Libraries folder is usually:
```
~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper/Libraries
```

---

## Faster opening of large definitions

Opening a `.gh` file still spends time **deserializing** thousands of components on the UI thread — AsyncGH cannot remove that work.

What *does* help is **deferring the first solution**: when a new top-level document is added, AsyncGH briefly sets the global Grasshopper flag `GH_Document.EnableSolutions` to `false` (same effect as the solver lock), lets the UI finish layout, then restores your previous solver state and schedules `NewSolution` for each deferred document (only if the solver was already on).

This is controlled by `Data.DeferSolveOnOpen` (default **on**) in source, or you can add a menu toggle later. **Note:** `EnableSolutions` is global — while a file is in this short deferral window, every open Grasshopper document pauses solving (usually only a few hundred milliseconds).

---

## How it works

AsyncGH uses [MonoMod.RuntimeDetour](https://github.com/MonoMod/MonoMod) to patch Grasshopper's internal methods at runtime:

- **`GH_Document.NewSolution`** — dispatches solutions to a background `Task`
- **`GH_Document.SolveAllObjects`** — replaces the sequential solve loop with a depth-ordered parallel one
- **`GH_Document.SolutionDepth`** — reports 0 (idle) on the UI thread so saves are always allowed
- **`GH_Document.SolutionState`** — reports `PreProcess` on the UI thread so canvas editing is always allowed
- **`Instances.RedrawAll`** — marshals canvas redraws to the UI thread
- **`MessageBox.Show`** — marshals all overloads to the UI thread
- **`GetObject.GetMultiple / Get`** — marshals interactive selection to the UI thread
- **`GetPoint / GetString / GetInteger / GetNumber .Get()`** — same

Components that require the UI thread are automatically detected on first solve and re-routed on subsequent solves without error.

---

## Building from source

```bash
git clone https://github.com/dfytz1/AsyncGH
cd AsyncGH
dotnet build AsyncGH/AsyncGH.csproj -c Release
```

Requires .NET 7 SDK (or later). The output `.gha` and all required DLLs appear in `AsyncGH/bin/Release/net7.0/`.

---

## License

MIT
