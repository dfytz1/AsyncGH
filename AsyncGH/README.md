# AsyncGH — Async Grasshopper Plugin for Rhino 8 Mac

**AsyncGH** keeps the Grasshopper UI responsive while your definition is computing.
Inspired by [SolutionAsync](https://github.com/Ted-Jin-Lab/SolutionAsync), rebuilt from scratch for **Rhino 8 Mac / .NET 7** with two extra features:

| Feature | Description |
|---|---|
| **Canvas navigation during computation** | Pan and zoom the canvas freely while GH is solving |
| **Save during computation** | Press **Cmd+S** at any time – the definition saves immediately |
| **Parallel level solving** | Objects at the same dependency depth solve in parallel |
| **Escape to abort** | Press **Escape** to abort the running solution |

---

## How it works

Grasshopper's default solver blocks the entire UI thread. AsyncGH patches
`GH_Document.NewSolution` and `SolveAllObjects` (via [HarmonyLib](https://github.com/pardeike/Harmony)) to:

1. **Dispatch each solution to a `Task.Run` thread pool worker** so the UI thread
   is free for canvas events and menu commands.
2. **Replace the sequential solve loop** with a depth-ordered parallel loop –
   objects at the same dependency level are solved concurrently.
3. **Report `SolutionDepth = 0` to the UI thread** while computing, so
   Grasshopper's save check passes and Cmd+S works normally.

---

## Requirements

- Rhino 8 for Mac (tested on Rhino 8.30+)
- .NET 7 SDK (for building from source)

---

## Installation

### Option A — Developer settings (for testing)

1. Build the project:
   ```
   cd AsyncGH
   dotnet build -c Release
   ```
2. Open Rhino → type `GrasshopperDeveloperSettings` in the Rhino command line.
3. Add the folder `AsyncGH/bin/Release/net7.0/` as a **Libraries** folder.
4. Restart Grasshopper.

### Option B — Manual install

1. Build the project (see above).
2. Copy these files to your Grasshopper Libraries folder
   (`~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper/Libraries/`):
   - `AsyncGH.gha`
   - `0Harmony.dll`
3. Restart Grasshopper.

---

## Compatibility

AsyncGH uses runtime patching, which can interact with other plugins that also
patch `GH_Document`. If you experience issues:

- Toggle async off by calling `Data.UseAsyncSolution = false` from a C# Script
  component, or by unloading the plugin.
- Components that require the UI thread will be automatically detected and
  re-routed. Their GUIDs are stored in `Data.NoAsyncObjects`.

---

## License

MIT
