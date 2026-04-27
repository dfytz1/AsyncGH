using System;
using System.Collections.Generic;
using Grasshopper.Kernel;

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
    /// When a new top-level document is added (e.g. opening a .gh file), temporarily
    /// turn off <see cref="GH_Document.EnableSolutions"/> so Grasshopper can finish
    /// layout/deserialization before the first solve. Restores the previous global
    /// solver state and schedules a solution shortly after (only if the solver was on).
    /// </summary>
    public static bool DeferSolveOnOpen { get; set; } = true;

    /// <summary>
    /// Group objects by dependency depth and solve each level in parallel.
    /// Disable if you experience ordering bugs with certain third-party components.
    /// </summary>
    public static bool UseOrderedLevel { get; set; } = true;

    /// <summary>
    /// GUIDs of components that must be solved on the UI thread (legacy WinForms
    /// components that crash when called off-thread). Populated automatically on
    /// first InvalidOperationException.
    /// </summary>
    public static readonly List<Guid> NoAsyncObjects = new();
}
