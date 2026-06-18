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
    /// <summary>
    /// UI thread managed ID. Captured explicitly in <see cref="PriorityLoad"/>,
    /// which Grasshopper invokes on the main UI thread during startup. Every
    /// thread-affinity decision in the plugin compares against this value, so it
    /// must reflect the genuine UI thread.
    /// </summary>
    internal static int UiThreadId { get; private set; } = Thread.CurrentThread.ManagedThreadId;

    public override GH_LoadingInstruction PriorityLoad()
    {
        try
        {
            // Re-capture here to be certain we record the real UI thread, even if
            // this type was first touched on some other thread.
            UiThreadId = Thread.CurrentThread.ManagedThreadId;

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
        if (sender.Document == null || !AsyncGHHooks.IsRunning(sender.Document)) return;

        var g = sender.Graphics;
        if (g == null) return;

        var state = g.Save();
        g.ResetTransform();

        var rect = sender.ClientRectangle;
        rect.Inflate(-4, -4);

        float progress = Math.Clamp(AsyncGHHooks.Progress, 0f, 1f);
        var baseColor = System.Drawing.Color.FromArgb(0, 220, 200);
        var fillColor = System.Drawing.Color.FromArgb(225, 59, 147);

        DrawTopProgressBar(g, rect, progress, baseColor, fillColor, barHeight: 6f);

        g.Restore(state);
    }

    /// <summary>
    /// Horizontal bar along the top inset: full-width teal base, magenta fill
    /// growing left → right with <paramref name="progress"/>.
    /// </summary>
    private static void DrawTopProgressBar(
        System.Drawing.Graphics g,
        System.Drawing.Rectangle rect,
        float progress,
        System.Drawing.Color baseColor,
        System.Drawing.Color fillColor,
        float barHeight)
    {
        if (rect.Width <= 0 || barHeight <= 0) return;

        int h = (int)Math.Ceiling(barHeight);
        var fullBar = new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Width, h);

        using (var baseBrush = new System.Drawing.SolidBrush(baseColor))
            g.FillRectangle(baseBrush, fullBar);

        int pinkW = (int)Math.Round(fullBar.Width * progress);
        if (pinkW > 0)
        {
            pinkW = Math.Min(pinkW, fullBar.Width);
            using var fillBrush = new System.Drawing.SolidBrush(fillColor);
            g.FillRectangle(fillBrush, fullBar.Left, fullBar.Top, pinkW, h);
        }
    }
}
