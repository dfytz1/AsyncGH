using System;
using System.Collections.Generic;

namespace AsyncGH;

/// <summary>
/// Runtime settings for AsyncGH. Toggle these via the Grasshopper menu
/// (Extensions > AsyncGH > ...) that AssemblyPriority wires up.
/// </summary>
internal static class Data
{
    /// <summary>Run solutions on a background thread so the UI stays responsive.</summary>
    public static bool UseAsyncSolution { get; set; } = true;

    /// <summary>
    /// Group objects by dependency depth so they can be solved level-by-level.
    /// Disable if you experience ordering bugs with certain third-party components.
    /// </summary>
    public static bool UseOrderedLevel { get; set; } = true;

    /// <summary>
    /// Solve components at the same dependency depth concurrently across the
    /// thread pool. Grasshopper / RhinoCommon are NOT thread-safe, so this is
    /// OFF by default — the solution still runs off the UI thread (keeping the
    /// UI responsive), just sequentially on a single background thread, which is
    /// far more stable. Enable only if you understand the risks.
    /// </summary>
    public static bool UseParallel { get; set; } = false;

    /// <summary>
    /// GUIDs of components that must be solved on the UI thread (legacy WinForms
    /// components that crash when called off-thread). Populated automatically on
    /// first InvalidOperationException. Guarded by its own lock because it can be
    /// read/written from multiple background threads in parallel mode.
    /// </summary>
    private static readonly HashSet<Guid> s_noAsyncObjects = new();
    private static readonly object        s_noAsyncLock    = new();

    public static bool IsNoAsync(Guid guid)
    {
        lock (s_noAsyncLock) return s_noAsyncObjects.Contains(guid);
    }

    /// <summary>Marks a component GUID as UI-thread-only. Returns true if newly added.</summary>
    public static bool MarkNoAsync(Guid guid)
    {
        lock (s_noAsyncLock) return s_noAsyncObjects.Add(guid);
    }
}
