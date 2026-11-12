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
    bool allowZ;
    public Action OnEscape, OnSpace;
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
        MinimizeBox = false; MaximizeBox = false;   // so Win+D and Win+M leave it alone
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
        Renderer = new Renderer(screen.SizePts, screen.WorldOrigin);
    }

    public void ShowAboveDesktop()
    {
        Show();
        Bounds = screen.WorkArea;   // in case a DPI change on creation moved it
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

    /// Win+D ("Desktop anzeigen") raises the desktop over every normal window, this one included.
    /// Like a desktop widget, the copy notices and climbs back on top of the desktop, still below
    /// the user's own windows.
    public void KeepAboveDesktop()
    {
        if (!Visible || IsDisposed) return;
        if (Native.IsIconic(Handle)) Native.ShowWindowAsync(Handle, Native.SW_SHOWNOACTIVATE);
        IntPtr desktop = IntPtr.Zero;
        for (var h = Native.GetWindow(Handle, Native.GW_HWNDPREV); h != IntPtr.Zero; h = Native.GetWindow(h, Native.GW_HWNDPREV))
        {
            if (!Native.IsWindowVisible(h)) continue;
            var cls = Native.ClassOf(h);
            if (cls is "Progman" or "WorkerW") desktop = h;   // keep walking: the highest one counts
        }
        if (desktop == IntPtr.Zero) return;
        var above = Native.GetWindow(desktop, Native.GW_HWNDPREV);
        allowZ = true;
        Native.SetWindowPos(Handle, above, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        allowZ = false;
    }

    const int WM_WINDOWPOSCHANGING = 0x46, WM_ERASEBKGND = 0x14, WM_KEYDOWN = 0x100, WM_DPICHANGED = 0x02E0, WM_SYSCOMMAND = 0x112, SC_MINIMIZE = 0xF020;

    public Screen Screen => screen;

    /// Several displays means several contexts on one thread: each overlay makes its own current first.
    public void MakeCurrent() => Gl.wglMakeCurrent(dc, ctx);

    protected override void WndProc(ref Message m)
    {
        // The window covers exactly one display's work area, in pixels; Windows mustn't rescale it.
        if (m.Msg == WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        // The copy of the desktop never minimizes: that would show the real icons mid-run.
        if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == SC_MINIMIZE) { m.Result = IntPtr.Zero; return; }
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

    /// All displays, so the pointer can be followed from one window to the next.
    public System.Collections.Generic.List<Screen> Screens;

    /// The pointer in world points, wherever it is. A drag that started here keeps working on the
    /// next display, because the cursor position is read rather than this window's own coordinates.
    V2 Pointer()
    {
        Native.GetCursorPos(out var cp);
        var s = Screens != null ? Screen.Under(Screens, cp.X, cp.Y) ?? screen : screen;
        return s.ToWorld(cp.X, cp.Y);
    }

    // One robot for all windows, so whatever is held is shared too.
    static Item grabbedItem;
    static V2 lastDragW, dragVelW;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Sim == null || Sim.Arena != null || e.Button != MouseButtons.Left) return;
        var p = Pointer();
        if (Sim.Cat is { } c && c.Hit(p, Sim)) { c.Pick(Sim); Capture = true; return; }
        if (Sim.HitRobot(p)) { Sim.PickUp(); Capture = true; return; }
        var it = Sim.HitItem(p);
        if (it != null && !it.Icon.Ingesting) { grabbedItem = it; it.Grabbed = true; lastDragW = p; dragVelW = V2.Zero; Capture = true; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Sim == null) return;
        var p = Pointer();
        if (Sim.Cat is { Picked: true } cat) { cat.Drag(p, Sim); return; }
        if (Sim.Picked) { Sim.DragRobot(p); return; }
        if (grabbedItem != null)
        {
            grabbedItem.Icon.Pos = p;
            dragVelW = dragVelW * 0.6f + (p - lastDragW) * 60 * 0.4f;
            lastDragW = p;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (Sim == null) return;
        Capture = false;
        Sim.Cat?.Drop(Sim);
        if (Sim.Picked) Sim.DropRobot();
        if (grabbedItem != null) { grabbedItem.Grabbed = false; grabbedItem.Icon.Vel = dragVelW; grabbedItem.Icon.AngV = dragVelW.X * 0.006f; grabbedItem = null; }
    }

    /// One frame on one window: draw this display's slice of the shared world, and present it.
    public void Render(float dt)
    {
        if (Sim == null || Renderer == null || IsDisposed) return;
        Gl.wglMakeCurrent(dc, ctx);
        Renderer.Encode(Sim, dt, 0, ClientSize.Width, ClientSize.Height);
        Gl.SwapBuffers(dc);
    }

    /// Steps the one shared sim at a fixed rate. Called once a frame, before every window draws.
    public static float Step(Sim sim, System.Collections.Generic.List<Overlay> overlays, Stopwatch clock, ref double last, ref float acc)
    {
        var now = clock.Elapsed.TotalSeconds;
        var dt = (float)(now - last); last = now;
        if (dt > 0.05f) dt = 0.05f;
        acc += dt;
        const float h = 1f / 120;
        Native.GetCursorPos(out var cp);
        var top = Native.GetAncestor(Native.WindowFromPoint(cp), 2);
        var over = overlays.Find(o => o.Handle == top);
        sim.Mouse = over != null && !sim.Picked ? over.Pointer() : null;
        sim.Lift += ((sim.Picked ? 1 : 0) - sim.Lift) * MathF.Min(1, dt * 11);
        int steps = 0;
        while (acc >= h && steps < 8) { sim.Step(h); acc -= h; steps++; }
        if (steps == 8) acc = 0;
        return dt;
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
