using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.Kernel;
using MonoMod.RuntimeDetour;
using Rhino;
using Rhino.Input;
using Rhino.Input.Custom;

namespace AsyncGH;

/// <summary>
/// All runtime method hooks, stored as static fields so they are never GC'd.
/// </summary>
internal static class AsyncGHHooks
{
    // ── Hook objects (must stay alive) ───────────────────────────────────────────
    private static Hook? s_newSolution;
    private static Hook? s_solveAllObjects;
    private static Hook? s_redrawAll;
    private static Hook? s_iteratorAbort;
    private static Hook? s_solutionDepth;
    private static Hook? s_solutionState;

    // MessageBox hooks
    private static Hook? s_msgBox1;
    private static Hook? s_msgBox2;
    private static Hook? s_msgBox3;
    private static Hook? s_msgBox4;

    // Rhino interactive input hooks (GetObject, GetPoint, GetString, etc.)
    private static Hook? s_getObjectMultiple;
    private static Hook? s_getObjectSingle;
    private static Hook? s_getPointGet;
    private static Hook? s_getPointGet2;
    private static Hook? s_getStringGet;
    private static Hook? s_getIntegerGet;
    private static Hook? s_getNumberGet;

    // ── State ─────────────────────────────────────────────────────────────────────
    // s_lock guards s_running and s_calculating.
    private  static readonly object          s_lock       = new();
    internal static          object          SyncRoot     => s_lock;

    /// <summary>Documents with a background solve currently dispatched.</summary>
    private  static readonly HashSet<GH_Document> s_running      = new();

    /// <summary>Documents whose orig(NewSolution) is executing on a BG thread (re-entrance guard).</summary>
    private  static readonly HashSet<GH_Document> s_calculating  = new();

    internal static bool IsRunning(GH_Document doc)
    { lock (s_lock) return s_running.Contains(doc); }

    // ── Progress tracking (written from background threads, read from UI thread) ─
    internal static int TotalComponents;
    internal static int CompletedComponents;

    internal static void ResetProgress(int total)
    {
        Interlocked.Exchange(ref TotalComponents,    total);
        Interlocked.Exchange(ref CompletedComponents, 0);
    }

    internal static void IncrementCompleted()
        => Interlocked.Increment(ref CompletedComponents);

    internal static float Progress =>
        TotalComponents <= 0 ? 1f
        : Math.Min(1f, (float)CompletedComponents / TotalComponents);

    /// <summary>
    /// Serializes <see cref="IGH_ActiveObject.CollectData"/> /
    /// <see cref="IGH_ActiveObject.ComputeData"/> (writers) against UI redraw /
    /// paint paths that read volatile structures (readers).
    /// </summary>
    internal static readonly ReaderWriterLockSlim SolveLock =
        new(LockRecursionPolicy.SupportsRecursion);

    // ── StructureIterator helpers ────────────────────────────────────────────────
    private static DateTime s_lastDraw = DateTime.MinValue;

    private static readonly Type? s_iterType =
        typeof(GH_Component).GetNestedType("GH_StructureIterator",
            BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? s_iterDocField =
        s_iterType?.GetField("m_document",
            BindingFlags.NonPublic | BindingFlags.Instance);

    // ── Thread-local state ───────────────────────────────────────────────────────
    private static readonly AsyncLocal<bool> s_isSolving = new();

    // ─────────────────────────────────────────────────────────────────────────────
    // Public install entry point
    // ─────────────────────────────────────────────────────────────────────────────

    internal static (int ok, int fail) Install()
    {
        var ok   = 0;
        var fail = 0;

        TryHook("NewSolution",       InstallNewSolution,       ref ok, ref fail);
        TryHook("SolveAllObjects",   InstallSolveAllObjects,   ref ok, ref fail);
        TryHook("RedrawAll",         InstallRedrawAll,         ref ok, ref fail);
        TryHook("StructureIterator", InstallStructureIterator, ref ok, ref fail);
        TryHook("SolutionDepth",     InstallSolutionDepth,     ref ok, ref fail);
        TryHook("SolutionState",     InstallSolutionState,     ref ok, ref fail);
        TryHook("MessageBox",        InstallMessageBox,        ref ok, ref fail);
        TryHook("RhinoInput",        InstallRhinoInputHooks,   ref ok, ref fail);

        return (ok, fail);
    }

    private static void TryHook(string name, Action install, ref int ok, ref int fail)
    {
        try { install(); ok++; }
        catch (Exception ex)
        {
            fail++;
            RhinoApp.WriteLine(
                $"[AsyncGH] Hook '{name}' skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // GH_Document.NewSolution(bool, GH_SolutionMode)
    // ─────────────────────────────────────────────────────────────────────────────

    private static void InstallNewSolution()
    {
        var method = typeof(GH_Document).GetMethod(
            nameof(GH_Document.NewSolution),
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[] { typeof(bool), typeof(GH_SolutionMode) },
            null)
            ?? throw new MissingMethodException("GH_Document.NewSolution not found");

        s_newSolution = new Hook(method,
            (Action<GH_Document, bool, GH_SolutionMode> orig,
             GH_Document self, bool expireAll, GH_SolutionMode mode) =>
            {
                // ── Already on a background thread OR inside an active solve ──
                // Covers: clusters, sub-documents, any nested solve inside a
                // component's ComputeData. Run synchronously so the caller gets
                // a completed result before it returns.
                if (Thread.CurrentThread.ManagedThreadId != AsyncGHPriority.UiThreadId || s_isSolving.Value)
                {
                    orig(self, expireAll, mode);
                    return;
                }

                // ── Synchronous fallback ─────────────────────────────────────────
                if (!Data.UseAsyncSolution) { orig(self, expireAll, mode); return; }

                lock (s_lock)
                {
                    // Re-entrant call from inside an ongoing background solve → inline.
                    if (s_calculating.Contains(self)) { orig(self, expireAll, mode); return; }

                    // A background solve is already queued for this document.
                    // Drop this duplicate request; GH will reschedule if needed
                    // once the current solve finishes and it still sees expired objects.
                    if (s_running.Contains(self)) return;

                    s_running.Add(self);
                }

                // ── Dispatch to background thread ────────────────────────────────
                Task.Run(() =>
                {
                    try
                    {
                        lock (s_lock) s_calculating.Add(self);
                        orig(self, expireAll, mode);
                    }
                    finally
                    {
                        lock (s_lock)
                        {
                            s_calculating.Remove(self);
                            s_running.Remove(self);
                        }

                        RhinoApp.InvokeOnUiThread(() =>
                        {
                            SolveLock.EnterReadLock();
                            try
                            {
                                Instances.RedrawCanvas();
                                Instances.ActiveRhinoDoc?.Views.Redraw();
                            }
                            finally
                            {
                                SolveLock.ExitReadLock();
                            }
                        });
                    }
                });
            });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // GH_Document.SolveAllObjects(GH_SolutionMode)  — private
    // ─────────────────────────────────────────────────────────────────────────────

    private static void InstallSolveAllObjects()
    {
        var method = typeof(GH_Document).GetMethod(
            "SolveAllObjects",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("GH_Document.SolveAllObjects not found");

        s_solveAllObjects = new Hook(method,
            (Action<GH_Document, GH_SolutionMode> orig,
             GH_Document self, GH_SolutionMode mode) =>
            {
                if (!Data.UseAsyncSolution) { orig(self, mode); return; }

                // If we are on the UI thread, we MUST run sequentially to avoid deadlocks.
                // This happens during initial document load or if NewSolution was called synchronously.
                bool runSync = Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId;

                // Nested / cluster sub-documents have an Owner; top-level docs do not.
                // Do NOT use DocumentServer.Contains — during first file load the doc is
                // not registered yet, which incorrectly forced sync and froze the UI.
                if (self.Owner != null)
                    runSync = true;

                // Prevent nested parallelism on the same thread (e.g. recursive solve from a component)
                if (s_isSolving.Value)
                    runSync = true;

                bool isTopLevel = !s_isSolving.Value;
                bool wasSolving = s_isSolving.Value;
                s_isSolving.Value = true;
                try
                {
                    var batches = CalculateItem.Create(self).ToList();

                    // Reset progress counter only for the top-level document solve.
                    if (isTopLevel)
                    {
                        int total = batches.Sum(b => b.Items.Length);
                        ResetProgress(total);
                    }

                    foreach (var batch in batches)
                    {
                        if (GH_Document.IsEscapeKeyDown()) self.RequestAbortSolution();
                        if (self.AbortRequested) break;
                        batch.Solve(mode, runSync);
                    }
                }
                finally
                {
                    s_isSolving.Value = wasSolving;
                }
                // Do NOT call orig — our loop replaces it entirely.
            });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Instances.RedrawAll()
    // ─────────────────────────────────────────────────────────────────────────────

    private static void InstallRedrawAll()
    {
        var method = typeof(Instances).GetMethod(
            nameof(Instances.RedrawAll),
            BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMethodException("Instances.RedrawAll not found");

        s_redrawAll = new Hook(method,
            (Action orig) =>
            {
                RhinoApp.InvokeOnUiThread(() =>
                {
                    SolveLock.EnterReadLock();
                    try
                    {
                        Instances.RedrawCanvas();
                        Instances.ActiveRhinoDoc?.Views.Redraw();
                    }
                    finally
                    {
                        SolveLock.ExitReadLock();
                    }
                });
            });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // GH_StructureIterator.AbortSolution getter
    // ─────────────────────────────────────────────────────────────────────────────

    private static void InstallStructureIterator()
    {
        if (s_iterType == null)
            throw new MissingMemberException("GH_StructureIterator type not found");

        var getter = s_iterType
            .GetProperty("AbortSolution",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetGetMethod(nonPublic: true)
            ?? throw new MissingMemberException("AbortSolution getter not found");

        s_iteratorAbort = new Hook(getter,
            (Func<object, bool> orig, object self) =>
            {
                PeriodicDraw();

                if (s_iterDocField?.GetValue(self) is GH_Document doc && doc.AbortRequested)
                    return true;

                return orig(self);
            });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // GH_Document.SolutionDepth getter — makes saves work during async solve
    // ─────────────────────────────────────────────────────────────────────────────

    private static void InstallSolutionDepth()
    {
        var getter = typeof(GH_Document)
            .GetProperty(nameof(GH_Document.SolutionDepth))
            ?.GetGetMethod()
            ?? throw new MissingMemberException("SolutionDepth getter not found");

        s_solutionDepth = new Hook(getter,
            (Func<GH_Document, int> orig, GH_Document self) =>
            {
                var depth = orig(self);
                // Report 0 (idle) on the UI thread so GH's save guard passes.
                if (Data.UseAsyncSolution
                    && IsRunning(self)
                    && Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId)
                {
                    return 0;
                }
                return depth;
            });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // GH_Document.SolutionState getter — makes canvas modifiable during async solve
    // ─────────────────────────────────────────────────────────────────────────────

    private static void InstallSolutionState()
    {
        var getter = typeof(GH_Document)
            .GetProperty(nameof(GH_Document.SolutionState))
            ?.GetGetMethod()
            ?? throw new MissingMemberException("SolutionState getter not found");

        s_solutionState = new Hook(getter,
            (Func<GH_Document, GH_ProcessStep> orig, GH_Document self) =>
            {
                var state = orig(self);
                // Report PreProcess (0) on the UI thread so GH allows canvas modifications
                if (Data.UseAsyncSolution
                    && IsRunning(self)
                    && Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId)
                {
                    return GH_ProcessStep.PreProcess;
                }
                return state;
            });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // System.Windows.Forms.MessageBox.Show
    // ─────────────────────────────────────────────────────────────────────────────

    private static void InstallMessageBox()
    {
        var t = typeof(MessageBox);

        var m1 = t.GetMethod("Show", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null);
        if (m1 != null)
            s_msgBox1 = new Hook(m1, (Func<string, DialogResult> orig, string text) =>
            {
                if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId) return orig(text);
                DialogResult res = DialogResult.None;
                RhinoApp.InvokeOnUiThread((Action)(() => res = orig(text)));
                return res;
            });

        var m2 = t.GetMethod("Show", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string), typeof(string) }, null);
        if (m2 != null)
            s_msgBox2 = new Hook(m2, (Func<string, string, DialogResult> orig, string text, string caption) =>
            {
                if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId) return orig(text, caption);
                DialogResult res = DialogResult.None;
                RhinoApp.InvokeOnUiThread((Action)(() => res = orig(text, caption)));
                return res;
            });

        var m3 = t.GetMethod("Show", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string), typeof(string), typeof(MessageBoxButtons) }, null);
        if (m3 != null)
            s_msgBox3 = new Hook(m3, (Func<string, string, MessageBoxButtons, DialogResult> orig, string text, string caption, MessageBoxButtons buttons) =>
            {
                if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId) return orig(text, caption, buttons);
                DialogResult res = DialogResult.None;
                RhinoApp.InvokeOnUiThread((Action)(() => res = orig(text, caption, buttons)));
                return res;
            });

        var m4 = t.GetMethod("Show", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string), typeof(string), typeof(MessageBoxButtons), typeof(MessageBoxIcon) }, null);
        if (m4 != null)
            s_msgBox4 = new Hook(m4, (Func<string, string, MessageBoxButtons, MessageBoxIcon, DialogResult> orig, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) =>
            {
                if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId) return orig(text, caption, buttons, icon);
                DialogResult res = DialogResult.None;
                RhinoApp.InvokeOnUiThread((Action)(() => res = orig(text, caption, buttons, icon)));
                return res;
            });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Rhino interactive input — GetObject, GetPoint, GetString, GetInteger, GetNumber
    //
    // All of these run a modal AppKit loop and MUST execute on the UI thread.
    // When called from a background thread we marshal synchronously via
    // InvokeOnUiThread so the background thread blocks until the user finishes.
    // ─────────────────────────────────────────────────────────────────────────────

    private static void InstallRhinoInputHooks()
    {
        // ── GetObject.GetMultiple(int, int) ──────────────────────────────────────
        var mGetMultiple = typeof(GetObject).GetMethod(
            "GetMultiple",
            BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(int), typeof(int) }, null);

        if (mGetMultiple != null)
            s_getObjectMultiple = new Hook(mGetMultiple,
                (Func<GetObject, int, int, GetResult> orig,
                 GetObject self, int min, int max) =>
                {
                    if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId)
                        return orig(self, min, max);
                    GetResult res = GetResult.Cancel;
                    RhinoApp.InvokeOnUiThread((Action)(() => res = orig(self, min, max)));
                    return res;
                });

        // ── GetObject.Get() ──────────────────────────────────────────────────────
        var mGetSingle = typeof(GetObject).GetMethod(
            "Get",
            BindingFlags.Instance | BindingFlags.Public,
            null, Type.EmptyTypes, null);

        if (mGetSingle != null)
            s_getObjectSingle = new Hook(mGetSingle,
                (Func<GetObject, GetResult> orig, GetObject self) =>
                {
                    if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId)
                        return orig(self);
                    GetResult res = GetResult.Cancel;
                    RhinoApp.InvokeOnUiThread((Action)(() => res = orig(self)));
                    return res;
                });

        // ── GetPoint.Get() ───────────────────────────────────────────────────────
        var mGetPoint = typeof(GetPoint).GetMethod(
            "Get",
            BindingFlags.Instance | BindingFlags.Public,
            null, Type.EmptyTypes, null);

        if (mGetPoint != null)
            s_getPointGet = new Hook(mGetPoint,
                (Func<GetPoint, GetResult> orig, GetPoint self) =>
                {
                    if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId)
                        return orig(self);
                    GetResult res = GetResult.Cancel;
                    RhinoApp.InvokeOnUiThread((Action)(() => res = orig(self)));
                    return res;
                });

        // ── GetPoint.Get(bool) ───────────────────────────────────────────────────
        var mGetPointBool = typeof(GetPoint).GetMethod(
            "Get",
            BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(bool) }, null);

        if (mGetPointBool != null)
            s_getPointGet2 = new Hook(mGetPointBool,
                (Func<GetPoint, bool, GetResult> orig, GetPoint self, bool onMouseUp) =>
                {
                    if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId)
                        return orig(self, onMouseUp);
                    GetResult res = GetResult.Cancel;
                    RhinoApp.InvokeOnUiThread((Action)(() => res = orig(self, onMouseUp)));
                    return res;
                });

        // ── GetString.Get() ──────────────────────────────────────────────────────
        var mGetString = typeof(GetString).GetMethod(
            "Get",
            BindingFlags.Instance | BindingFlags.Public,
            null, Type.EmptyTypes, null);

        if (mGetString != null)
            s_getStringGet = new Hook(mGetString,
                (Func<GetString, GetResult> orig, GetString self) =>
                {
                    if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId)
                        return orig(self);
                    GetResult res = GetResult.Cancel;
                    RhinoApp.InvokeOnUiThread((Action)(() => res = orig(self)));
                    return res;
                });

        // ── GetInteger.Get() ─────────────────────────────────────────────────────
        var mGetInt = typeof(GetInteger).GetMethod(
            "Get",
            BindingFlags.Instance | BindingFlags.Public,
            null, Type.EmptyTypes, null);

        if (mGetInt != null)
            s_getIntegerGet = new Hook(mGetInt,
                (Func<GetInteger, GetResult> orig, GetInteger self) =>
                {
                    if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId)
                        return orig(self);
                    GetResult res = GetResult.Cancel;
                    RhinoApp.InvokeOnUiThread((Action)(() => res = orig(self)));
                    return res;
                });

        // ── GetNumber.Get() ──────────────────────────────────────────────────────
        var mGetNumber = typeof(GetNumber).GetMethod(
            "Get",
            BindingFlags.Instance | BindingFlags.Public,
            null, Type.EmptyTypes, null);

        if (mGetNumber != null)
            s_getNumberGet = new Hook(mGetNumber,
                (Func<GetNumber, GetResult> orig, GetNumber self) =>
                {
                    if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId)
                        return orig(self);
                    GetResult res = GetResult.Cancel;
                    RhinoApp.InvokeOnUiThread((Action)(() => res = orig(self)));
                    return res;
                });
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static void PeriodicDraw()
    {
        if (DateTime.Now - s_lastDraw <= TimeSpan.FromMilliseconds(50)) return;
        s_lastDraw = DateTime.Now;
        Instances.RedrawAll();
    }
}
