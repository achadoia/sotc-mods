// Estilo neobrutalista compartilhado pelo patcher e pelo trainer: fundo creme, bordas pretas grossas, sombra dura deslocada,
// cores chapadas (amarelo / rosa / ciano / verde-limão), cantos retos, tipografia pesada.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

static class Neo
{
    public static readonly Color Bg = Color.FromArgb(250, 249, 245), Ink = Color.FromArgb(20, 20, 19), White = Color.White;
    public static readonly Color Yellow = Color.FromArgb(255, 214, 10), Pink = Color.FromArgb(255, 107, 157), Cyan = Color.FromArgb(76, 201, 240),
                                 Lime = Color.FromArgb(166, 235, 60), Orange = Color.FromArgb(255, 111, 51), Terra = Color.FromArgb(217, 119, 87), Grey = Color.FromArgb(214, 211, 202);
    public const int Border = 3, Shadow = 5;

    public static Font Black(float px) { return new Font("Arial Black", px, FontStyle.Regular, GraphicsUnit.Pixel); }
    public static Font Bold(float px) { return new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel); }
    public static Font Text(float px) { return new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel); }
    public static Font Mono(float px) { return new Font("Consolas", px, FontStyle.Bold, GraphicsUnit.Pixel); }

    // caixa com sombra dura (à direita/abaixo) e borda grossa; r = área da caixa SEM a sombra
    public static void Box(Graphics g, Rectangle r, Color fill, int shadow = Shadow, int border = Border)
    {
        g.SmoothingMode = SmoothingMode.None;
        if (shadow > 0) using (var b = new SolidBrush(Ink)) g.FillRectangle(b, r.X + shadow, r.Y + shadow, r.Width, r.Height);
        using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
        using (var p = new Pen(Ink, border) { Alignment = PenAlignment.Inset }) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    public static void Label(Graphics g, string text, Font f, Color c, Rectangle r, ContentAlignment al = ContentAlignment.MiddleLeft)
    {
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        if (al == ContentAlignment.MiddleCenter) flags |= TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
        else if (al == ContentAlignment.MiddleRight) flags |= TextFormatFlags.Right | TextFormatFlags.VerticalCenter;
        else flags |= TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(g, text, f, r, c, flags);
    }
}

// botão: caixa colorida com sombra; ao apertar, desce em cima da sombra
class NeoButton : Button
{
    public Color Fill = Neo.Yellow, Hover = Neo.White;
    bool down, over;
    public NeoButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; Cursor = Cursors.Hand; Font = Neo.Black(14); BackColor = Neo.Bg;
        TabStop = true;
    }
    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs pe)
    {
        var g = pe.Graphics; g.Clear(Parent != null ? Parent.BackColor : Neo.Bg);
        int s = Neo.Shadow, o = down && Enabled ? s : 0;
        var r = new Rectangle(o, o, Width - s, Height - s);
        Neo.Box(g, r, !Enabled ? Neo.Grey : (over ? Hover : Fill), down && Enabled ? 0 : s);
        Neo.Label(g, Text.ToUpperInvariant(), Font, Enabled ? Neo.Ink : Color.FromArgb(120, 116, 108), r, ContentAlignment.MiddleCenter);
        if (Focused && ShowFocusCues) using (var p = new Pen(Neo.Ink) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, r.X + 5, r.Y + 5, r.Width - 11, r.Height - 11);
    }
}

// caixa de seleção: quadrado grosso que vira verde com um "X" preto
class NeoCheck : CheckBox
{
    public NeoCheck()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false; Cursor = Cursors.Hand; Font = Neo.Bold(14); ForeColor = Neo.Ink;
    }
    protected override void OnMouseEnter(EventArgs e) { Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs pe)
    {
        var g = pe.Graphics; g.Clear(BackColor);
        int bx = 2, by = (Height - 22) / 2;
        var box = new Rectangle(bx, by, 20, 20);
        bool hot = ClientRectangle.Contains(PointToClient(Cursor.Position)) && Enabled;
        Neo.Box(g, box, !Enabled ? Neo.Grey : (Checked ? Neo.Lime : (hot ? Neo.Yellow : Neo.White)), 2, 3);
        if (Checked)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Enabled ? Neo.Ink : Color.FromArgb(120, 116, 108), 3.4f) { StartCap = LineCap.Square, EndCap = LineCap.Square })
            { g.DrawLine(p, box.X + 5, box.Y + 5, box.Right - 6, box.Bottom - 6); g.DrawLine(p, box.Right - 6, box.Y + 5, box.X + 5, box.Bottom - 6); }
        }
        Neo.Label(g, Text, Font, Enabled ? Neo.Ink : Color.FromArgb(130, 126, 118), new Rectangle(bx + 30, 0, Width - bx - 30, Height));
    }
}
