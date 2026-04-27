using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.Kernel;
using Rhino;

namespace AsyncGH;

/// <summary>
/// Temporarily disables the global Grasshopper solver (<see cref="GH_Document.EnableSolutions"/>)
/// while new top-level documents attach, then restores the previous flag and schedules
/// a first solve for each deferred document (if the solver was on).
/// </summary>
internal static class OpenSolveDeferral
{
    private static readonly object s_gate = new();
    private static int s_depth;
    private static bool s_savedEnableSolutions;
    private static readonly HashSet<GH_Document> s_pending = new();

    internal static void Initialize()
    {
        Instances.DocumentServer.DocumentAdded += OnDocumentAdded;
    }

    private static void OnDocumentAdded(GH_DocumentServer sender, GH_Document doc)
    {
        if (!Data.DeferSolveOnOpen || doc == null || doc.Nested)
            return;

        Control? editor = Instances.DocumentEditor;
        if (editor == null)
        {
            RhinoApp.WriteLine("[AsyncGH] Open deferral skipped — DocumentEditor not ready.");
            return;
        }

        lock (s_gate)
        {
            if (!s_pending.Add(doc))
                return;

            if (s_depth == 0)
                s_savedEnableSolutions = GH_Document.EnableSolutions;
            s_depth++;
            GH_Document.EnableSolutions = false;
        }

        // Two BeginInvoke hops: lets Grasshopper finish control/layout work for this
        // message before we re-enable the solver and queue the first solution.
        try
        {
            editor.BeginInvoke(new Action(() =>
            {
                editor.BeginInvoke(new Action(FlushDeferred));
            }));
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[AsyncGH] Open deferral BeginInvoke failed: {ex.Message}");
            FlushDeferred();
        }
    }

    private static void FlushDeferred()
    {
        bool saved;
        GH_Document[] docs;

        lock (s_gate)
        {
            s_depth = Math.Max(0, s_depth - 1);
            if (s_depth > 0)
                return;

            saved = s_savedEnableSolutions;
            docs = new GH_Document[s_pending.Count];
            s_pending.CopyTo(docs);
            s_pending.Clear();
        }

        GH_Document.EnableSolutions = saved;

        if (!saved) return;

        foreach (var d in docs)
        {
            try
            {
                if (d is { Nested: false })
                    d.ScheduleSolution(1, _ => _.NewSolution(false));
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[AsyncGH] ScheduleSolution after open failed: {ex.Message}");
            }
        }
    }
}
