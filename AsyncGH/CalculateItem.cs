using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Rhino;

namespace AsyncGH;

/// <summary>
/// Represents one batch of GH active objects to be solved together.
/// When UseOrderedLevel is on, all objects at the same dependency depth are
/// grouped into one batch and solved in parallel via the thread pool.
/// </summary>
internal class CalculateItem
{
    private CalculateItem(GH_Document doc, params IGH_ActiveObject[] items)
    {
        Items = items;
        Doc = doc;
    }

    public IGH_ActiveObject[] Items { get; }
    private GH_Document Doc { get; }

    // ── Factory ─────────────────────────────────────────────────────────────────

    public static IEnumerable<CalculateItem> Create(GH_Document doc)
    {
        var items = doc.Objects.OfType<IGH_ActiveObject>().ToList();

        if (!Data.UseOrderedLevel)
            return items.Select(i => new CalculateItem(doc, i));

        var depthCache = new Dictionary<IGH_ActiveObject, int>();
        var visiting = new HashSet<IGH_ActiveObject>();

        int GetDepth(IGH_ActiveObject obj)
        {
            if (depthCache.TryGetValue(obj, out var d)) return d;
            
            // Cycle detection: if we are already visiting this object, break the cycle
            if (!visiting.Add(obj)) return 0; 

            var upstream = GetUpstream(obj);
            int depth = upstream.Length == 0 ? 0 : upstream.Max(GetDepth) + 1;

            visiting.Remove(obj);
            return depthCache[obj] = depth;
        }

        var groups = items.GroupBy(GetDepth);
        return groups
            .OrderBy(g => g.Key)
            .Select(g => new CalculateItem(doc, g.ToArray()));
    }

    private static IGH_ActiveObject[] GetUpstream(IGH_ActiveObject obj)
    {
        if (obj is IGH_Param param)
            return param.Sources
                .Where(s => s?.Attributes?.GetTopLevel?.DocObject != null)
                .Select(s => s.Attributes.GetTopLevel.DocObject)
                .OfType<IGH_ActiveObject>()
                .ToHashSet()
                .ToArray();

        if (obj is IGH_Component comp)
            return comp.Params.Input
                .Where(p => p != null)
                .SelectMany(p => GetUpstream(p))
                .ToHashSet()
                .ToArray();

        return Array.Empty<IGH_ActiveObject>();
    }

    // ── Solving ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Solve all items in this batch.
    /// </summary>
    /// <param name="mode">The solution mode.</param>
    /// <param name="runSync">If true, solves everything sequentially on the current thread to avoid deadlocks.</param>
    public void Solve(GH_SolutionMode mode, bool runSync)
    {
        if (runSync)
        {
            foreach (var item in Items)
                SolveOne(item, mode, Doc);
            return;
        }

        var uiItems = Items.Where(i => Data.IsNoAsync(i.ComponentGuid)).ToArray();
        var bgItems = Items.Except(uiItems).ToArray();

        // Start background tasks, but don't wait yet.
        var tasks = bgItems.Select(i => Task.Run(() => SolveOne(i, mode, Doc))).ToArray();

        // UI-thread items run synchronously via InvokeOnUiThread.
        // We are guaranteed to be on a background thread here (runSync is false),
        // so WaitAll below won't deadlock the UI thread.
        foreach (var item in uiItems)
        {
            if (Thread.CurrentThread.ManagedThreadId == AsyncGHPriority.UiThreadId)
                SolveOne(item, mode, Doc);
            else
                RhinoApp.InvokeOnUiThread(() => SolveOne(item, mode, Doc));
        }

        // Wait for all background tasks.
        if (tasks.Length > 0)
            Task.WaitAll(tasks);
    }

    private static void SolveOne(IGH_ActiveObject item, GH_SolutionMode mode, GH_Document doc)
    {
        try
        {
            if (item.Phase == GH_SolutionPhase.Computed) return;

            try
            {
                item.CollectData();
                item.ComputeData();
            }
            catch (Exception ex) when (IsUiThreadError(ex))
            {
                // Component tried to touch UI/AppKit from a background thread.
                // Register it as UI-only so future solves route it to the main thread.
                if (Data.MarkNoAsync(item.ComponentGuid))
                    RhinoApp.WriteLine(
                        $"[AsyncGH] '{item.Name}' requires the UI thread — " +
                        "will be re-routed on the next solve. " +
                        $"({ex.GetType().Name}: {ex.Message})");

                // Re-run on the UI thread (blocking until it finishes) so this solve
                // cycle still produces output before downstream components run.
                AsyncGHHooks.RunOnUiSync(() =>
                {
                    item.CollectData();
                    item.ComputeData();
                });
            }
        }
        catch (Exception ex)
        {
            item.Phase = GH_SolutionPhase.Failed;
            item.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            RhinoApp.WriteLine(
                $"[AsyncGH] Exception in '{item.Name}' [{item.NickName}]: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            item.Attributes?.ExpireLayout();
            AsyncGHHooks.IncrementCompleted();
        }
    }

    /// <summary>
    /// Returns true for exceptions that are likely caused by cross-thread UI access.
    /// Catches common managed wrappers of AppKit / WinForms thread-affinity errors.
    /// </summary>
    private static bool IsUiThreadError(Exception ex)
    {
        if (ex is InvalidOperationException) return true;
        if (ex is ThreadStateException)      return true;

        // AppKit / macOS ObjC thread-affinity messages
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("main thread",    StringComparison.OrdinalIgnoreCase)
            || msg.Contains("UI thread",      StringComparison.OrdinalIgnoreCase)
            || msg.Contains("must be called from the main", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("NSAlert",        StringComparison.OrdinalIgnoreCase)
            || msg.Contains("NSApplication",  StringComparison.OrdinalIgnoreCase)
            || (ex.GetType().Name.Contains("ObjC",  StringComparison.OrdinalIgnoreCase))
            || (ex.GetType().Name.Contains("AppKit", StringComparison.OrdinalIgnoreCase));
    }
}
