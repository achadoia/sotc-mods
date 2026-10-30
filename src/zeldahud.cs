// Corações e círculo de fôlego do Zelda (--hud, opção "Corações e fôlego do Zelda" do lançador).
//   - vida: 1 coração = 20 de vida, quartos de 5. O jogo começa com 100 (5 corações), cada colosso passa a dar +20
//     (+1 coração; a tabela dos colossos tinha +4 em entry+0x174) e o teto sobe de 570 para 600 (30 corações;
//     constantes 0x128AD80/84/88, lidas só por GetMaxHPCap 0x7ECCB0, SetMaxHP 0x7ECCC0 e AddMaxHP 0x7ECD10).
//     O Time Attack/Reminiscência começa o k-ésimo colosso com entry+0x17C = 100+4k: vira 100+20k (5+k corações).
//   - fôlego: máximo do jogo 100..700 (var 12). Um círculo verde de 100 e dois anéis em volta, de 300 cada,
//     que aparecem conforme o máximo cresce (calangos de rabo branco +8 por colosso); gasta de fora para dentro.
//   - a barra de vida e o medidor de fôlego do jogo somem: a rotina do HUD 0x778300 decide a cada quadro entre
//     mostrar (0x778464 vida / 0x778490 fôlego -> ShowHUDScene 0x242F50) e esconder (HideHUDScene 0x2433C0);
//     os dois "je" que levam ao mostrar viram NOP, então o jogo sempre esconde PlayerStatusHUD_Health/_Stamina.
//   - desenho: janela em camadas (alfa por pixel), transparente ao mouse, por cima da área do jogo, só com o jogo
//     em primeiro plano, o Wander carregado e nenhuma câmera de cutscene ativa.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class ZeldaHud
{
    public const long HP_CUR = 0x3265C78, GRIP_CUR = 0x3265C90, VARS = 0x325F410, HP_MAX = VARS + 11 * 4, GRIP_MAX = VARS + 12 * 4;
    const long HP_CAPS = 0x128AD80;                        // 3 floats 570
    const long SHOW_HEALTH = 0x77A490, SHOW_STAMINA = 0x77A536;
    static readonly byte[] ShowHealthOld = Game.Hex("0F84CEDFFFFF"), ShowStaminaOld = Game.Hex("0F8454DFFFFF"), Nop6 = Game.Hex("909090909090");
    const int BOSS_HP_BONUS = 0x174, BOSS_TA_HP = 0x17C;
    public const float HP_PER_HEART = 20, HP_CAP = 600, GRIP_BASE = 100, GRIP_RING = 300;

    static Thread ui; static HudForm form;

    public static void Begin(Game g, Process p)
    {
        Patch(g, SHOW_HEALTH, ShowHealthOld, "barra de vida do jogo");
        Patch(g, SHOW_STAMINA, ShowStaminaOld, "medidor de fôlego do jogo");
        var cap = BitConverter.GetBytes(HP_CAP);
        g.Write(HP_CAPS, cap.Concat(cap).Concat(cap).ToArray(), true);
        for (int i = 0; i < 16; i++)
        {
            long e = Game.ENTRIES + i * Game.ENTRY_SIZE;
            g.Write(e + BOSS_HP_BONUS, BitConverter.GetBytes(HP_PER_HEART));
            g.Write(e + BOSS_TA_HP, BitConverter.GetBytes(100 + HP_PER_HEART * i));
        }
        Game.Log("corações: 1 coração = {0:0} de vida, colosso +1 coração, teto {1:0} ({2:0} corações); HUD de vida/fôlego do jogo escondido",
            HP_PER_HEART, HP_CAP, HP_CAP / HP_PER_HEART);
        ui = new Thread(() =>
        {
            try { SetProcessDPIAware(); form = new HudForm(g, p); Application.Run(form); }
            catch (Exception e) { Game.Log("corações: {0}", e.Message); }
        }) { IsBackground = true, Name = "zelda-hud" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
    }

    public static void End()
    {
        var f = form; form = null;
        if (f != null) { try { f.BeginInvoke((Action)(() => f.Close())); } catch { } }
    }

    static void Patch(Game g, long va, byte[] old, string what)
    {
        var cur = g.Read(va, old.Length);
        if (cur.SequenceEqual(Nop6)) return;
        if (!cur.SequenceEqual(old)) { Game.Log("corações: {0} não escondido (bytes inesperados em {1:X})", what, va); return; }
        g.Write(va, Nop6, true);
    }

    [DllImport("user32")] static extern bool SetProcessDPIAware();
}

class HudForm : Form
{
    [DllImport("user32")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32")] static extern bool IsIconic(IntPtr h);
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
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 33 };
    DateTime nextSlow = DateTime.MinValue, gripFullSince = DateTime.MinValue, trailHoldUntil = DateTime.MinValue;
    bool inGame, shown;
    float trail = -1;   // fôlego que acabou de ser gasto (o pedaço vermelho), alcança o atual depois de um tempo

    public HudForm(Game g, Process p)
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

    float F(long va) { return BitConverter.ToSingle(g.Read(va, 4), 0); }

    void Tick()
    {
        try
        {
            if (p.HasExited) { Close(); return; }
            if (DateTime.Now >= nextSlow)
            {
                nextSlow = DateTime.Now.AddMilliseconds(200);
                bool wander = false, cuts = true;
                try { wander = g.PlayerObject() != 0; cuts = g.ActiveCuts() > 0; } catch { }
                bool now = wander && !cuts;
                if (now != inGame) Game.Log("corações: {0}", now ? "na tela" : wander ? "escondidos (câmera de cutscene)" : "escondidos (sem o Wander)");
                inGame = now;
            }
            p.Refresh();
            IntPtr win = p.MainWindowHandle;
            if (!inGame || win == IntPtr.Zero || GetForegroundWindow() != win || IsIconic(win)) { HideHud(); return; }
            RECT rc; GetClientRect(win, out rc);
            var org = new POINT(0, 0); ClientToScreen(win, ref org);
            int cw = rc.R - rc.L, ch = rc.B - rc.T;
            if (cw < 200 || ch < 150) { HideHud(); return; }
            float hp = F(ZeldaHud.HP_CUR), hpMax = F(ZeldaHud.HP_MAX), grip = F(ZeldaHud.GRIP_CUR), gripMax = F(ZeldaHud.GRIP_MAX);
            if (!(hpMax > 0) || hpMax > 10000 || !(gripMax > 0) || gripMax > 10000) { HideHud(); return; }
            Render(org.X, org.Y, cw, ch, hp, hpMax, grip, gripMax);
        }
        catch { HideHud(); }
    }

    void HideHud() { if (shown) { Visible = false; shown = false; } }

    // ---------- desenho ----------
    // coração 15x13: K contorno, '.' interior; o resto é transparente
    static readonly string[] Heart = {
        "  KKKK   KKKK  ",
        " K....K K....K ",
        "K......K......K",
        "K.............K",
        "K.............K",
        "K.............K",
        " K...........K ",
        "  K.........K  ",
        "   K.......K   ",
        "    K.....K    ",
        "     K...K     ",
        "      K.K      ",
        "       K       ",
    };
    static readonly Color Outline = Color.FromArgb(255, 12, 10, 18), Red = Color.FromArgb(255, 236, 16, 16), DarkRed = Color.FromArgb(255, 170, 8, 14),
        Shine = Color.FromArgb(255, 255, 255, 255), Empty = Color.FromArgb(235, 64, 64, 64), EmptyDark = Color.FromArgb(235, 48, 48, 48);

    static bool In(int r, int c) { return r >= 0 && r < Heart.Length && c >= 0 && c < Heart[r].Length && Heart[r][c] == '.'; }
    // quartos na ordem em que somem: 0 = cima-direita, 1 = baixo-direita, 2 = baixo-esquerda, 3 = cima-esquerda (fica por último)
    static int Quarter(int r, int c) { bool right = c > 7, top = r <= 5; return right ? (top ? 0 : 1) : (top ? 3 : 2); }

    static void DrawHeart(Graphics gr, int x, int y, int s, int quarters)
    {
        using (var b = new SolidBrush(Color.Black))
            for (int r = 0; r < Heart.Length; r++)
                for (int c = 0; c < Heart[r].Length; c++)
                {
                    char k = Heart[r][c]; if (k == ' ') continue;
                    Color col;
                    if (k == 'K') col = Outline;
                    else
                    {
                        // os quartos que sobram são os últimos da ordem: com q quartos, ficam os de índice >= 4-q
                        bool filled = Quarter(r, c) >= 4 - quarters;
                        bool edge = !In(r, c + 1) || (r >= 6 && !In(r, c - 1)) || !In(r + 1, c);
                        bool shine = (r == 2 && (c == 2 || c == 3)) || (r == 3 && c == 2);
                        col = filled ? (shine ? Shine : edge ? DarkRed : Red) : (edge ? EmptyDark : Empty);
                    }
                    b.Color = col;
                    gr.FillRectangle(b, x + c * s, y + r * s, s, s);
                }
    }

    void Render(int ox, int oy, int cw, int ch, float hp, float hpMax, float grip, float gripMax)
    {
        int s = Math.Max(2, (int)Math.Round(ch / 400.0));                  // tamanho do "pixel" do coração
        int hw = 15 * s, hh = 13 * s, gap = s * 2, perRow = 10;
        int margin = (int)(ch * 0.035);
        int hearts = Math.Max(1, (int)Math.Ceiling(hpMax / ZeldaHud.HP_PER_HEART - 0.001));
        int rows = (hearts + perRow - 1) / perRow;
        int heartsH = rows * hh + (rows - 1) * gap;

        // fôlego: anéis existentes e preenchimento
        int R = (int)(ch * 0.034);                                           // raio do círculo principal
        int ringW = Math.Max(4, (int)(R * 0.32)), ringGap = Math.Max(2, R / 10);
        float extra = Math.Max(0, gripMax - ZeldaHud.GRIP_BASE);
        int rings = extra <= 0.5f ? 0 : extra <= ZeldaHud.GRIP_RING + 0.5f ? 1 : 2;
        int Rout = R + rings * (ringGap + ringW);

        // vermelho = o que acabou de ser gasto
        if (trail < 0 || grip >= trail) { trail = grip; trailHoldUntil = DateTime.Now.AddMilliseconds(600); }
        else if (DateTime.Now > trailHoldUntil) trail = Math.Max(grip, trail - gripMax * 0.02f);
        // como no Zelda: o círculo some 1,5 s depois de ficar cheio
        bool full = grip >= gripMax - 0.5f;
        if (!full) gripFullSince = DateTime.MinValue; else if (gripFullSince == DateTime.MinValue) gripFullSince = DateTime.Now;
        bool showGrip = !full || (DateTime.Now - gripFullSince).TotalSeconds < 1.5 || trail > grip + 0.5f;

        int W = Math.Max(perRow * (hw + gap), 2 * Rout + 4) + 4, H = heartsH + gap * 3 + 2 * Rout + 4;
        using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
        {
            using (var gr = Graphics.FromImage(bmp))
            {
                gr.Clear(Color.Transparent);
                gr.SmoothingMode = SmoothingMode.None; gr.PixelOffsetMode = PixelOffsetMode.Half;
                float left = Math.Max(0, hp);
                for (int i = 0; i < hearts; i++)
                {
                    float inHeart = Math.Min(ZeldaHud.HP_PER_HEART, Math.Max(0, left - i * ZeldaHud.HP_PER_HEART));
                    int q = (int)Math.Floor(inHeart / (ZeldaHud.HP_PER_HEART / 4) + 0.001);
                    if (i == 0 && q == 0 && left > 0) q = 1;                // vivo nunca aparece com zero
                    DrawHeart(gr, 2 + (i % perRow) * (hw + gap), 2 + (i / perRow) * (hh + gap), s, q);
                }
                if (showGrip) DrawGrip(gr, 2 + Rout, 2 + heartsH + gap * 3 + Rout, R, ringW, ringGap, rings, grip, trail, gripMax);
            }
            Blit(bmp, ox + margin, oy + margin);
        }
    }

    static readonly Color Green = Color.FromArgb(255, 34, 222, 68), Gray = Color.FromArgb(215, 96, 96, 96), Trail = Color.FromArgb(255, 226, 32, 24), Ink = Color.FromArgb(255, 10, 10, 10);

    // faixa de valores de cada anel: 0 = círculo (0..100), 1 = 100..400, 2 = 400..700
    static void Range(int k, out float lo, out float hi) { lo = k == 0 ? 0 : ZeldaHud.GRIP_BASE + (k - 1) * ZeldaHud.GRIP_RING; hi = k == 0 ? ZeldaHud.GRIP_BASE : lo + ZeldaHud.GRIP_RING; }

    static void DrawGrip(Graphics gr, int cx, int cy, int R, int ringW, int ringGap, int rings, float grip, float trail, float gripMax)
    {
        gr.SmoothingMode = SmoothingMode.AntiAlias;
        bool low = grip < gripMax * 0.2f && (DateTime.Now.Millisecond / 250) % 2 == 0;   // pisca quando está acabando
        for (int k = 0; k <= rings; k++)
        {
            float lo, hi; Range(k, out lo, out hi);
            float owned = Math.Min(1, Math.Max(0, (gripMax - lo) / (hi - lo)));   // quanto do anel já foi conquistado
            float fill = Math.Min(1, Math.Max(0, (grip - lo) / (hi - lo)));
            float tr = Math.Min(1, Math.Max(0, (trail - lo) / (hi - lo)));
            if (owned <= 0) continue;
            float rOut, rIn;
            if (k == 0) { rOut = R; rIn = R * 0.36f; }
            else { rIn = R + ringGap + (k - 1) * (ringGap + ringW); rOut = rIn + ringW; }
            Wedge(gr, cx, cy, rOut, rIn, owned, 0, Gray);
            if (tr > fill) Wedge(gr, cx, cy, rOut, rIn, tr, fill, Trail);
            if (fill > 0) Wedge(gr, cx, cy, rOut, rIn, fill, 0, low ? Trail : Green);
            using (var pen = new Pen(Ink, Math.Max(1.5f, R / 18f)))
            using (var path = Arc(cx, cy, rOut, rIn, owned, 0)) gr.DrawPath(pen, path);
        }
        gr.SmoothingMode = SmoothingMode.None;
    }

    // pedaço de anel entre as frações "from" e "to" de uma volta; a volta acaba no topo e o fôlego some
    // em sentido horário a partir do topo (como na imagem de referência)
    static GraphicsPath Arc(float cx, float cy, float rOut, float rIn, float to, float from)
    {
        var path = new GraphicsPath();
        float a0 = -90 + 360 * (1 - to), sweep = 360 * (to - from);
        if (sweep >= 359.9f)
        {
            path.AddEllipse(cx - rOut, cy - rOut, 2 * rOut, 2 * rOut);
            path.AddEllipse(cx - rIn, cy - rIn, 2 * rIn, 2 * rIn);
            return path;
        }
        path.AddArc(cx - rOut, cy - rOut, 2 * rOut, 2 * rOut, a0, sweep);
        path.AddArc(cx - rIn, cy - rIn, 2 * rIn, 2 * rIn, a0 + sweep, -sweep);
        path.CloseFigure();
        return path;
    }
    static void Wedge(Graphics gr, float cx, float cy, float rOut, float rIn, float to, float from, Color c)
    {
        if (to - from <= 0.0005f) return;
        using (var path = Arc(cx, cy, rOut, rIn, to, from)) using (var b = new SolidBrush(c)) gr.FillPath(b, path);
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
