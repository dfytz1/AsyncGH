using System;
using System.Threading;
using Grasshopper;
using Grasshopper.Kernel;
using Rhino;

namespace AsyncGH;

/// <summary>
/// Called early during Grasshopper plugin load. Installs all MonoMod hooks
/// and wires document lifecycle events.
/// </summary>
public class AsyncGHPriority : GH_AssemblyPriority
{
    /// <summary>UI thread managed ID captured at load time.</summary>
    internal static readonly int UiThreadId = Thread.CurrentThread.ManagedThreadId;

    public override GH_LoadingInstruction PriorityLoad()
    {
        try
        {
            var (ok, fail) = AsyncGHHooks.Install();

            AsyncToolbar.Initialize();

            Instances.CanvasCreated += OnCanvasCreated;
            if (Instances.ActiveCanvas != null)
                OnCanvasCreated(Instances.ActiveCanvas);

            RhinoApp.WriteLine(
                $"[AsyncGH] Loaded — {ok} hooks active" +
                (fail > 0 ? $", {fail} skipped (partial functionality)." : "."));
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[AsyncGH] Init error: {ex.Message}");
        }

        return GH_LoadingInstruction.Proceed;
    }

    private static void OnCanvasCreated(Grasshopper.GUI.Canvas.GH_Canvas canvas)
    {
        canvas.CanvasPostPaintWidgets -= Canvas_CanvasPostPaintWidgets;
        canvas.CanvasPostPaintWidgets += Canvas_CanvasPostPaintWidgets;
    }

    private static void Canvas_CanvasPostPaintWidgets(Grasshopper.GUI.Canvas.GH_Canvas sender)
    {
        if (sender.Document != null && AsyncGHHooks.IsRunning(sender.Document))
        {
            var g = sender.Graphics;
            if (g == null) return;

            var state = g.Save();
            g.ResetTransform();

            var rect = sender.ClientRectangle;
            rect.Inflate(-5, -5);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0, 220, 200), 10f);
            g.DrawRectangle(pen, rect);

            g.Restore(state);
        }
    }
}
