using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace RoboDust;

/// The copy of the desktop: a borderless window over the work area, just above the real icons and
/// below every other window. Takes the pointer, so stray clicks can't land on the hidden icons.
public sealed class Overlay : Form
{
    public Sim Sim;
    public Renderer Renderer;
    readonly Screen screen;
    IntPtr dc, ctx;
    Item grabbed;
    V2 lastDrag, dragVel;
    float acc;
    readonly Stopwatch clock = Stopwatch.StartNew();
    double last;
    bool allowZ;
    public Action OnFinished, OnEscape, OnSpace;
    bool finishedFired;
    public bool KeepBelowWindows = true;

    public Overlay(Screen screen)
    {
        this.screen = screen;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.WorkArea;
        BackColor = System.Drawing.Color.Black;
        Text = "RoboDust";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;   // not in Alt+Tab
            cp.ClassStyle |= 0x20;                   // CS_OWNDC, which OpenGL wants
            return cp;
        }
    }

    /// Creates the window and its OpenGL context without showing it.
    public void Prepare()
    {
        CreateHandle();
        (dc, ctx) = Gl.CreateContext(Handle);
        unsafe { if (Gl.SwapIntervalEXT != null) Gl.SwapIntervalEXT(0); }
        Renderer = new Renderer(screen.SizePts);
    }

    public void ShowAboveDesktop()
    {
        Show();
        PlaceAboveDesktop();
    }

    /// Directly above the desktop's own window, so every app window stays in front.
    public void PlaceAboveDesktop()
    {
        var progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return;
        var above = Native.GetWindow(progman, Native.GW_HWNDPREV);
        if (above == Handle) return;
        allowZ = true;
        Native.SetWindowPos(Handle, above == IntPtr.Zero ? IntPtr.Zero : above, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        allowZ = false;
    }

    const int WM_WINDOWPOSCHANGING = 0x46, WM_ERASEBKGND = 0x14, WM_KEYDOWN = 0x100;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_WINDOWPOSCHANGING && KeepBelowWindows && !allowZ && Visible)
        {
            // Clicking it would normally bring it to the front, over the user's windows.
            unsafe
            {
                var wp = (int*)m.LParam;
                // WINDOWPOS: hwnd, hwndInsertAfter, x, y, cx, cy, flags. On 64-bit the two handles take 4 ints.
                var flagsIndex = IntPtr.Size == 8 ? 8 : 6;
                wp[flagsIndex] |= (int)Native.SWP_NOZORDER;
            }
        }
        if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }
        if (m.Msg == WM_KEYDOWN)
        {
            var k = (Keys)(int)m.WParam;
            if (k == Keys.Escape) { OnEscape?.Invoke(); return; }
            if (k == Keys.Space) { OnSpace?.Invoke(); return; }
        }
        base.WndProc(ref m);
    }

    V2 Pt(MouseEventArgs e) => new(e.X / screen.Scale, e.Y / screen.Scale);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Sim == null || e.Button != MouseButtons.Left) return;
        var p = Pt(e);
        if (Sim.HitRobot(p)) { Sim.PickUp(); Capture = true; return; }
        var it = Sim.HitItem(p);
        if (it != null && !it.Icon.Ingesting) { grabbed = it; it.Grabbed = true; lastDrag = p; dragVel = V2.Zero; Capture = true; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Sim == null) return;
        var p = Pt(e);
        if (Sim.Picked) { Sim.DragRobot(p); return; }
        if (grabbed != null)
        {
            grabbed.Icon.Pos = p;
            dragVel = dragVel * 0.6f + (p - lastDrag) * 60 * 0.4f;
            lastDrag = p;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (Sim == null) return;
        Capture = false;
        if (Sim.Picked) Sim.DropRobot();
        if (grabbed != null) { grabbed.Grabbed = false; grabbed.Icon.Vel = dragVel; grabbed.Icon.AngV = dragVel.X * 0.006f; grabbed = null; }
    }

    /// One frame: step the sim at a fixed rate, draw, present.
    public void Frame()
    {
        if (Sim == null || Renderer == null || IsDisposed) return;
        var now = clock.Elapsed.TotalSeconds;
        var dt = (float)(now - last); last = now;
        if (dt > 0.05f) dt = 0.05f;
        acc += dt;
        const float h = 1f / 120;
        Native.GetCursorPos(out var cp);
        var onTop = Native.GetAncestor(Native.WindowFromPoint(cp), 2) == Handle;
        Sim.Mouse = onTop && !Sim.Picked ? new V2((cp.X - screen.WorkArea.Left) / screen.Scale, (cp.Y - screen.WorkArea.Top) / screen.Scale) : null;
        Sim.Lift += ((Sim.Picked ? 1 : 0) - Sim.Lift) * MathF.Min(1, dt * 11);
        int steps = 0;
        while (acc >= h && steps < 8) { Sim.Step(h); acc -= h; steps++; }
        if (steps == 8) acc = 0;
        Gl.wglMakeCurrent(dc, ctx);
        Renderer.Encode(Sim, dt, 0, ClientSize.Width, ClientSize.Height);
        Gl.SwapBuffers(dc);
        if (Sim.Phase == Phase.Finished && !finishedFired) { finishedFired = true; OnFinished?.Invoke(); }
    }

    public void Present() { Gl.wglMakeCurrent(dc, ctx); Gl.SwapBuffers(dc); }

    /// Fades the copy away to reveal the real desktop, which by now matches it.
    public void SetAlpha(float a)
    {
        var ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        if ((ex & Native.WS_EX_LAYERED) == 0) Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex | Native.WS_EX_LAYERED);
        Native.SetLayeredWindowAttributes(Handle, 0, (byte)(M.Clamp(a, 0, 1) * 255), Native.LWA_ALPHA);
    }

    protected override void Dispose(bool disposing)
    {
        if (ctx != IntPtr.Zero) { Gl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); Gl.wglDeleteContext(ctx); ctx = IntPtr.Zero; }
        if (dc != IntPtr.Zero && IsHandleCreated) { Native.ReleaseDC(Handle, dc); dc = IntPtr.Zero; }
        base.Dispose(disposing);
    }
}
