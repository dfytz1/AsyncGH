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
        if (sender.Document == null || !AsyncGHHooks.IsRunning(sender.Document)) return;

        var g = sender.Graphics;
        if (g == null) return;

        var state = g.Save();
        g.ResetTransform();

        var rect = sender.ClientRectangle;
        rect.Inflate(-4, -4);

        float progress = AsyncGHHooks.Progress;
        var color = System.Drawing.Color.FromArgb(0, 220, 200);

        DrawPerimeterProgress(g, rect, progress, color, thickness: 6f);

        g.Restore(state);
    }

    /// <summary>
    /// Draws a clockwise-travelling stroke around the rectangle perimeter.
    /// Progress 0 = nothing drawn; 1 = full border.
    /// Starts at the top-left corner, travels: right → down → left → up.
    /// </summary>
    private static void DrawPerimeterProgress(
        System.Drawing.Graphics g,
        System.Drawing.Rectangle rect,
        float progress,
        System.Drawing.Color color,
        float thickness)
    {
        if (progress <= 0f) return;

        float w = rect.Width;
        float h = rect.Height;
        float total = 2f * (w + h);
        float remaining = Math.Min(progress, 1f) * total;

        using var pen = new System.Drawing.Pen(color, thickness) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };

        // Top edge: left → right
        if (remaining > 0)
        {
            float seg = Math.Min(remaining, w);
            g.DrawLine(pen, rect.Left, rect.Top, rect.Left + seg, rect.Top);
            remaining -= seg;
        }

        // Right edge: top → bottom
        if (remaining > 0)
        {
            float seg = Math.Min(remaining, h);
            g.DrawLine(pen, rect.Right, rect.Top, rect.Right, rect.Top + seg);
            remaining -= seg;
        }

        // Bottom edge: right → left
        if (remaining > 0)
        {
            float seg = Math.Min(remaining, w);
            g.DrawLine(pen, rect.Right, rect.Bottom, rect.Right - seg, rect.Bottom);
            remaining -= seg;
        }

        // Left edge: bottom → top
        if (remaining > 0)
        {
            float seg = Math.Min(remaining, h);
            g.DrawLine(pen, rect.Left, rect.Bottom, rect.Left, rect.Bottom - seg);
        }
    }
}
