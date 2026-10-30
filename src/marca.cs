// Marca d'água "Mod by: @AchadoIA" (--marca, o lançador passa sempre que algum mod do patch está ligado).
//   - janela em camadas, transparente ao mouse, no canto de cima à direita da área do jogo;
//   - some ao confirmar qualquer opção do menu (Enter/Espaço/clique/A) e volta ao reentrar no título;
//   - aparece só nos menus (sem o Wander carregado ou com ele na cena do título, coluna A do mapa), com o jogo em primeiro plano.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class Marca
{
    public const string Text = "Mod by: @AchadoIA";
    static MarcaForm form;

    public static void Begin(Game g, Process p)
    {
        var ui = new Thread(() =>
        {
            try { SetProcessDPIAware(); form = new MarcaForm(g, p); Application.Run(form); }
            catch (Exception e) { Game.Log("marca d'água: {0}", e.Message); }
        }) { IsBackground = true, Name = "marca" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
    }

    public static void End()
    {
        var f = form; form = null;
        if (f != null) { try { f.BeginInvoke((Action)(() => f.Close())); } catch { } }
    }

    [DllImport("user32")] static extern bool SetProcessDPIAware();
}

class MarcaForm : Form
{
    [DllImport("user32")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32")] static extern short GetAsyncKeyState(int k);
    [StructLayout(LayoutKind.Sequential)] struct XINPUT_STATE { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint i, out XINPUT_STATE s);
    [DllImport("user32", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLEND pblend, int dwFlags);
    [DllImport("user32")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32")] static extern bool DeleteObject(IntPtr o);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }

    readonly Game g; readonly Process p;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 60 };
    bool shown, inMenu = true, logged, dismissed, wasDown;
    DateTime shownSince = DateTime.MaxValue;
    int lastX, lastY, lastH;

    public MarcaForm(Game g, Process p)
    {
        this.g = g; this.p = p;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, 1, 1);
        timer.Tick += (s, e) => Tick();
        timer.Start();
    }
    protected override CreateParams CreateParams
    {
        // WS_EX_LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE | TOPMOST
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; return cp; }
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override void OnFormClosed(FormClosedEventArgs e) { timer.Stop(); base.OnFormClosed(e); Application.ExitThread(); }

    void Tick()
    {
        try
        {
            if (p.HasExited) { Close(); return; }
            bool menu = true;
            try
            {
                menu = g.PlayerObject() == 0;
                if (!menu)
                {
                    // a tela de título é uma cena 3D viva: o Wander já existe, fora do mapa (coluna A da grade, px < 427)
                    var pos = g.PlayerPos();
                    if (pos != null && (-pos[0] + 4325.51) / 4.5688 < 426.6) menu = true;
                }
            }
            catch { }
            if (menu != inMenu || !logged) { Game.Log("marca d'água: {0}", menu ? "no menu, na tela" : "escondida (jogando)"); if (menu && logged) dismissed = false; inMenu = menu; logged = true; }
            p.Refresh();
            IntPtr win = p.MainWindowHandle;
            // qualquer opção confirmada no menu (teclado, mouse ou controle) tira a marca; volta ao reentrar no título
            // só vale um aperto novo (borda), com a marca já na tela há mais de 1,5 s: o Enter/clique que passou o carregamento não conta
            string src = Confirming(); bool down = src != null;
            if (!shown || !menu) shownSince = DateTime.MaxValue; else if (shownSince == DateTime.MaxValue) shownSince = DateTime.Now;
            if (!dismissed && shown && down && !wasDown && (DateTime.Now - shownSince).TotalSeconds > 1.5 && GetForegroundWindow() == win) { dismissed = true; Game.Log("marca d'água: opção escolhida ({0}), escondida", src); }
            wasDown = down;
            if (!menu || dismissed || win == IntPtr.Zero || GetForegroundWindow() != win || IsIconic(win)) { Hide2(); return; }
            RECT rc; GetClientRect(win, out rc);
            var org = new POINT(0, 0); ClientToScreen(win, ref org);
            int cw = rc.R - rc.L, ch = rc.B - rc.T;
            if (cw < 200 || ch < 150) { Hide2(); return; }
            if (shown && org.X + cw == lastX && org.Y + ch == lastY && ch == lastH) return;   // nada mudou
            lastX = org.X + cw; lastY = org.Y + ch; lastH = ch;
            Render(org.X, org.Y, cw, ch);
        }
        catch { Hide2(); }
    }

    // Enter / Espaço / Enter do teclado numérico / botão esquerdo do mouse / A do controle
    static string Confirming()
    {
        if ((GetAsyncKeyState(0x0D) & 0x8000) != 0) return "Enter";
        if ((GetAsyncKeyState(0x20) & 0x8000) != 0) return "Espaço";
        if ((GetAsyncKeyState(0x01) & 0x8000) != 0) return "clique do mouse";
        try { XINPUT_STATE s; for (uint i = 0; i < 4; i++) if (XInputGetState(i, out s) == 0 && (s.Buttons & 0x1000) != 0) return "botão A do controle"; } catch { }
        return null;
    }

    void Hide2() { if (shown) { Visible = false; shown = false; } }

    void Render(int ox, int oy, int cw, int ch)
    {
        float px = Math.Max(12f, ch * 0.019f);
        using (var font = new Font("Georgia", px, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Pixel))
        {
            Size sz;
            using (var tmp = new Bitmap(1, 1)) using (var gt = Graphics.FromImage(tmp)) sz = Size.Ceiling(gt.MeasureString(Marca.Text, font));
            int pad = (int)(px * 0.5f);
            int W = sz.Width + pad * 2, H = sz.Height + pad * 2;
            using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            {
                using (var gr = Graphics.FromImage(bmp))
                {
                    gr.Clear(Color.Transparent);
                    gr.SmoothingMode = SmoothingMode.AntiAlias;
                    gr.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    using (var path = new GraphicsPath())
                    {
                        path.AddString(Marca.Text, font.FontFamily, (int)font.Style, px, new PointF(pad, pad), StringFormat.GenericDefault);
                        using (var shadow = new Pen(Color.FromArgb(150, 0, 0, 0), px * 0.22f) { LineJoin = LineJoin.Round }) gr.DrawPath(shadow, path);
                        using (var b = new SolidBrush(Color.FromArgb(200, 226, 196, 120))) gr.FillPath(b, path);   // dourado do patch
                    }
                }
                int margin = (int)(ch * 0.03);
                Blit(bmp, ox + cw - W - margin, oy + margin);
            }
        }
    }

    void Blit(Bitmap bmp, int x, int y)
    {
        if (!shown) { Visible = true; shown = true; }
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
        try
        {
            var size = new SIZE(bmp.Width, bmp.Height); var src = new POINT(0, 0); var dst = new POINT(x, y);
            var bl = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };   // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref bl, 2);   // ULW_ALPHA
        }
        finally { SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
    }
}
