using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
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

    /// <summary>
    /// Latest solution request that arrived while a background solve was already
    /// running for a document. Re-dispatched once the running solve finishes so
    /// edits made during a solve are never silently dropped.
    /// </summary>
    private  static readonly Dictionary<GH_Document, (bool expireAll, GH_SolutionMode mode)> s_pending = new();

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

    // ── StructureIterator helpers ────────────────────────────────────────────────
    // Stored as ticks and accessed via Interlocked: the AbortSolution getter is
    // invoked from many background threads concurrently, so a plain DateTime
    // field could tear or throttle incorrectly.
    private static long s_lastDrawTicks;

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

                    // A background solve is already in flight for this document.
                    // Don't start a second one (that would race), but remember the
                    // request so it runs as soon as the current solve finishes —
                    // otherwise edits made mid-solve would be silently lost.
                    if (s_running.Contains(self))
                    {
                        var prev = s_pending.TryGetValue(self, out var p) ? p : default;
                        s_pending[self] = (expireAll || prev.expireAll, mode);
                        return;
                    }

                    s_running.Add(self);
                }

                // ── Dispatch to background thread ────────────────────────────────
                Task.Run(() =>
                {
                    bool             rerun     = false;
                    bool             reExpire  = false;
                    GH_SolutionMode  reMode    = mode;

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

                            if (s_pending.TryGetValue(self, out var p))
                            {
                                s_pending.Remove(self);
                                rerun    = true;
                                reExpire = p.expireAll;
                                reMode   = p.mode;
                            }
                        }

                        RhinoApp.InvokeOnUiThread(() =>
                        {
                            Instances.RedrawCanvas();
                            Instances.ActiveRhinoDoc?.Views.Redraw();

                            // Re-run on the UI thread so it flows back through this
                            // same hook and dispatches a fresh background solve.
                            if (rerun)
                                self.NewSolution(reExpire, reMode);
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

                // Default: solve sequentially on the current (background) thread.
                // The UI stays responsive because the whole solve is already off
                // the UI thread; we avoid the data races that come from touching
                // the non-thread-safe GH/Rhino object model from several threads.
                bool runSync = !Data.UseParallel;

                // If we are on the UI thread, we MUST run sequentially to avoid deadlocks.
                // This happens during initial document load or if NewSolution was called synchronously.
                if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId)
                    runSync = true;

                // Also run sequentially if this is a nested document (e.g., a cluster)
                // to avoid ThreadPool starvation from nested Task.WaitAll calls.
                if (Instances.DocumentServer?.Contains(self) != true)
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
                    Instances.RedrawCanvas();
                    Instances.ActiveRhinoDoc?.Views.Redraw();
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
                RunOnUiSync(() => orig(text)));

        var m2 = t.GetMethod("Show", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string), typeof(string) }, null);
        if (m2 != null)
            s_msgBox2 = new Hook(m2, (Func<string, string, DialogResult> orig, string text, string caption) =>
                RunOnUiSync(() => orig(text, caption)));

        var m3 = t.GetMethod("Show", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string), typeof(string), typeof(MessageBoxButtons) }, null);
        if (m3 != null)
            s_msgBox3 = new Hook(m3, (Func<string, string, MessageBoxButtons, DialogResult> orig, string text, string caption, MessageBoxButtons buttons) =>
                RunOnUiSync(() => orig(text, caption, buttons)));

        var m4 = t.GetMethod("Show", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string), typeof(string), typeof(MessageBoxButtons), typeof(MessageBoxIcon) }, null);
        if (m4 != null)
            s_msgBox4 = new Hook(m4, (Func<string, string, MessageBoxButtons, MessageBoxIcon, DialogResult> orig, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) =>
                RunOnUiSync(() => orig(text, caption, buttons, icon)));
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
                    RunOnUiSync(() => orig(self, min, max)));

        // ── GetObject.Get() ──────────────────────────────────────────────────────
        var mGetSingle = typeof(GetObject).GetMethod(
            "Get",
            BindingFlags.Instance | BindingFlags.Public,
            null, Type.EmptyTypes, null);

        if (mGetSingle != null)
            s_getObjectSingle = new Hook(mGetSingle,
                (Func<GetObject, GetResult> orig, GetObject self) =>
                    RunOnUiSync(() => orig(self)));

        // ── GetPoint.Get() ───────────────────────────────────────────────────────
        var mGetPoint = typeof(GetPoint).GetMethod(
            "Get",
            BindingFlags.Instance | BindingFlags.Public,
            null, Type.EmptyTypes, null);

        if (mGetPoint != null)
            s_getPointGet = new Hook(mGetPoint,
                (Func<GetPoint, GetResult> orig, GetPoint self) =>
                    RunOnUiSync(() => orig(self)));

        // ── GetPoint.Get(bool) ───────────────────────────────────────────────────
        var mGetPointBool = typeof(GetPoint).GetMethod(
            "Get",
            BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(bool) }, null);

        if (mGetPointBool != null)
            s_getPointGet2 = new Hook(mGetPointBool,
                (Func<GetPoint, bool, GetResult> orig, GetPoint self, bool onMouseUp) =>
                    RunOnUiSync(() => orig(self, onMouseUp)));

        // ── GetString.Get() ──────────────────────────────────────────────────────
        var mGetString = typeof(GetString).GetMethod(
            "Get",
            BindingFlags.Instance | BindingFlags.Public,
            null, Type.EmptyTypes, null);

        if (mGetString != null)
            s_getStringGet = new Hook(mGetString,
                (Func<GetString, GetResult> orig, GetString self) =>
                    RunOnUiSync(() => orig(self)));

        // ── GetInteger.Get() ─────────────────────────────────────────────────────
        var mGetInt = typeof(GetInteger).GetMethod(
            "Get",
            BindingFlags.Instance | BindingFlags.Public,
            null, Type.EmptyTypes, null);

        if (mGetInt != null)
            s_getIntegerGet = new Hook(mGetInt,
                (Func<GetInteger, GetResult> orig, GetInteger self) =>
                    RunOnUiSync(() => orig(self)));

        // ── GetNumber.Get() ──────────────────────────────────────────────────────
        var mGetNumber = typeof(GetNumber).GetMethod(
            "Get",
            BindingFlags.Instance | BindingFlags.Public,
            null, Type.EmptyTypes, null);

        if (mGetNumber != null)
            s_getNumberGet = new Hook(mGetNumber,
                (Func<GetNumber, GetResult> orig, GetNumber self) =>
                    RunOnUiSync(() => orig(self)));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static void PeriodicDraw()
    {
        long now  = DateTime.UtcNow.Ticks;
        long last = Interlocked.Read(ref s_lastDrawTicks);
        if (now - last <= TimeSpan.FromMilliseconds(50).Ticks) return;

        // Only one thread wins the throttle window; the rest bail out.
        if (Interlocked.CompareExchange(ref s_lastDrawTicks, now, last) != last) return;

        Instances.RedrawAll();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Synchronous UI-thread marshalling.
    //
    // RhinoApp.InvokeOnUiThread only *posts* a delegate to the UI message loop; it
    // does not reliably block or return a value. The interactive-input / MessageBox
    // hooks need the calling background thread to wait for the result, otherwise the
    // thread races ahead with a bogus value while a modal dialog runs on the UI
    // thread — a frequent source of crashes. These helpers block until the UI thread
    // has finished and propagate the result (and any exception).
    // ─────────────────────────────────────────────────────────────────────────────

    internal static void RunOnUiSync(Action action)
    {
        if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId)
        {
            action();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        ExceptionDispatchInfo? captured = null;

        RhinoApp.InvokeOnUiThread((Action)(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ExceptionDispatchInfo.Capture(ex); }
            finally { done.Set(); }
        }));

        done.Wait();
        captured?.Throw();
    }

    internal static T RunOnUiSync<T>(Func<T> func)
    {
        T result = default!;
        RunOnUiSync(() => { result = func(); });
        return result;
    }
}
