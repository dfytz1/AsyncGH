using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI.Canvas;

namespace AsyncGH;

internal static class AsyncToolbar
{
    private const string ButtonName = "AsyncGHBtn";
    private const string SepName    = "AsyncGHSep";
    private const int ToolbarInsertIndex = 5;
    private const int IconSize = 24;

    private static ToolStripButton? _toolButton;
    private static bool _toolbarRegistered;
    private static bool _toolbarRegisterPending;
    private static readonly HashSet<GH_Canvas> HookedCanvases = new();

    private static Bitmap? _iconBase;

    // ── Icon loading ─────────────────────────────────────────────────────────────

    private static Bitmap IconBase => _iconBase ??= LoadEmbedded();

    private static Bitmap LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("AsyncGH.AsyncIcon.png");
        if (stream == null)
            return new Bitmap(IconSize, IconSize);

        // Copy to MemoryStream before creating Bitmap to avoid GDI+ stream-lifetime issues.
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        using var raw = new Bitmap(ms);

        if (raw.Width == IconSize && raw.Height == IconSize)
            return EnsureArgb(new Bitmap(raw));

        var scaled = new Bitmap(IconSize, IconSize);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode    = PixelOffsetMode.Half;
            g.SmoothingMode      = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(raw, 0, 0, IconSize, IconSize);
        }
        return EnsureArgb(scaled);
    }

    private static Bitmap EnsureArgb(Bitmap bmp)
    {
        if (bmp.PixelFormat == PixelFormat.Format32bppArgb)
            return bmp;
        try
        {
            var converted = bmp.Clone(new Rectangle(0, 0, bmp.Width, bmp.Height), PixelFormat.Format32bppArgb);
            bmp.Dispose();
            return converted;
        }
        catch
        {
            return bmp;
        }
    }

    private static Bitmap MakeIcon(bool active)
    {
        if (active) return new Bitmap(IconBase);

        // Dimmed (50%) version when async is off
        var bmp = new Bitmap(IconBase.Width, IconBase.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var cm = new ColorMatrix(new[]
            {
                new[] { 0.5f, 0f,   0f,   0f, 0f },
                new[] { 0f,   0.5f, 0f,   0f, 0f },
                new[] { 0f,   0f,   0.5f, 0f, 0f },
                new[] { 0f,   0f,   0f,   0.5f, 0f },
                new[] { 0f,   0f,   0f,   0f, 1f },
            });
            using var attrs = new ImageAttributes();
            attrs.SetColorMatrix(cm);
            g.DrawImage(IconBase,
                new Rectangle(0, 0, bmp.Width, bmp.Height),
                0, 0, IconBase.Width, IconBase.Height,
                GraphicsUnit.Pixel, attrs);
        }
        return bmp;
    }

    // ── Toolbar management ───────────────────────────────────────────────────────

    public static void Initialize()
    {
        Instances.CanvasCreated += OnCanvasCreated;
        if (Instances.ActiveCanvas != null)
            HookCanvas(Instances.ActiveCanvas);
    }

    private static void OnCanvasCreated(GH_Canvas canvas) => HookCanvas(canvas);

    private static void HookCanvas(GH_Canvas? canvas)
    {
        if (canvas == null || !HookedCanvases.Add(canvas)) return;
        EnsureToolbarOnEditor();
    }

    private static void EnsureToolbarOnEditor()
    {
        if (_toolbarRegistered || _toolbarRegisterPending) return;
        var editor = Instances.DocumentEditor;
        if (editor == null) return;

        _toolbarRegisterPending = true;

        void Register()
        {
            try
            {
                if (_toolbarRegistered) return;
                if (TryAddToolbarButton())
                {
                    _toolbarRegistered = true;
                    WatchEditorForClose(editor);
                }
            }
            finally
            {
                _toolbarRegisterPending = false;
            }
        }

        try
        {
            if (editor.IsHandleCreated)
                editor.BeginInvoke(new Action(Register));
            else
            {
                EventHandler? load = null;
                load = (_, _) => { editor.Load -= load; Register(); };
                editor.Load += load;
            }
        }
        catch
        {
            Register();
        }
    }

    /// <summary>
    /// Reset registration state when the Grasshopper editor window closes, so the toolbar button
    /// is re-added if the editor is reopened (a fresh editor/toolbar is created each time, which
    /// would otherwise be skipped because of the one-shot <see cref="_toolbarRegistered"/> latch).
    /// </summary>
    private static void WatchEditorForClose(Form editor)
    {
        FormClosedEventHandler? closed = null;
        closed = (_, _) =>
        {
            editor.FormClosed -= closed;
            _toolbarRegistered      = false;
            _toolbarRegisterPending = false;
            _toolButton             = null;
        };
        editor.FormClosed += closed;
    }

    private static bool TryAddToolbarButton()
    {
        var editor = Instances.DocumentEditor;
        if (editor == null || editor.Controls.Count < 1) return false;
        var panel = editor.Controls[0];
        if (panel.Controls.Count < 2) return false;

        var toolbar = panel.Controls[1] as ToolStrip;
        if (toolbar == null) return false;

        // Remove existing if already inserted (handles re-load).
        for (var i = toolbar.Items.Count - 1; i >= 0; i--)
        {
            var n = toolbar.Items[i].Name;
            if (n == ButtonName || n == SepName)
                toolbar.Items.RemoveAt(i);
        }

        _toolButton = new ToolStripButton
        {
            Name         = ButtonName,
            ToolTipText  = "Toggle Async Solution (AsyncGH)",
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            Image        = MakeIcon(Data.UseAsyncSolution),
            CheckOnClick = false,
            Checked      = Data.UseAsyncSolution,
            Alignment    = ToolStripItemAlignment.Left,
            ImageScaling = ToolStripItemImageScaling.None,
            AutoSize     = false,
            Size         = new Size(28, 22),
        };
        _toolButton.Click += (_, _) => ToggleFeature();

        var idx = Math.Min(ToolbarInsertIndex, toolbar.Items.Count);
        toolbar.Items.Insert(idx, _toolButton);
        toolbar.Items.Insert(idx + 1, new ToolStripSeparator
        {
            Name      = SepName,
            Alignment = ToolStripItemAlignment.Left,
        });

        toolbar.PerformLayout();
        toolbar.Invalidate();

        // Force a deferred repaint — on Mac the image stays blank until the
        // button is redrawn asynchronously after layout has settled.
        editor.BeginInvoke(new Action(() =>
        {
            if (_toolButton == null || _toolButton.IsDisposed) return;
            var old = _toolButton.Image;
            _toolButton.Image = MakeIcon(Data.UseAsyncSolution);
            old?.Dispose();
            _toolButton.Invalidate();
            toolbar.Invalidate();
            toolbar.Update();
        }));

        return true;
    }

    private static void ToggleFeature()
    {
        Data.UseAsyncSolution = !Data.UseAsyncSolution;

        if (_toolButton != null)
        {
            _toolButton.Checked = Data.UseAsyncSolution;
            var old = _toolButton.Image;
            _toolButton.Image = MakeIcon(Data.UseAsyncSolution);
            old?.Dispose();
            _toolButton.Invalidate();
        }

        Instances.ActiveCanvas?.Refresh();
    }
}
